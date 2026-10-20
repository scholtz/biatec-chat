using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Biatec.Chat.Api.Chat.DataCards;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Model;
using Biatec.Chat.Api.Model.Guardrails;
using Biatec.Chat.Core.Capabilities;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Biatec.Chat.Api.Evidence.Recording;
using Microsoft.EntityFrameworkCore;
using DataMessage = Biatec.Chat.Core.Data.Message;
using DataTier = Biatec.Chat.Core.Data.SessionTier;
using ModelMessage = Amazon.BedrockRuntime.Model.Message;

namespace Biatec.Chat.Api.Orchestration;

/// <summary>One user turn to run.</summary>
public sealed record ChatTurn(
    Guid ConversationId,
    string SessionId,
    Tenant Tenant,
    DataTier Tier,
    IReadOnlyCollection<string> Scopes,
    string Message,
    HostContext? Context,
    bool SignInAvailable,
    AgentMode Mode = AgentMode.Prepare,
    bool Paused = false);

/// <summary>
/// The tool loop: model, then gate, then executor, then the result (wrapped as untrusted data) back to the model,
/// until the model answers in text. Every tool call is checked by <see cref="ToolGate"/> regardless of what the model
/// was shown. Streams <see cref="ChatEvent"/>s and persists every message after redaction.
/// </summary>
public sealed class ConversationOrchestrator(
    IModelClient model,
    ToolGate gate,
    IEnumerable<IToolExecutor> executors,
    IMessageRedactor redactor,
    IToolResultGuard toolGuard,
    ChatDbContext db,
    TimeProvider clock,
    ILogger<ConversationOrchestrator> log,
    IEvidenceRecorder? evidence = null)
{
    public const int MaxToolRounds = 8;
    public const int MaxHistoryRows = 60;
    public const int MaxContextFieldLength = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, IToolExecutor> _executors =
        executors.GroupBy(e => e.CapabilityName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

    private sealed record StoredToolCall(string Id, string Name, string Input);
    private sealed record StoredToolResult(string ToolUseId, string Name, string Content, bool IsError);

    public async IAsyncEnumerable<ChatEvent> RunAsync(ChatTurn turn, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var gateCtx = new GateContext(turn.Tier, turn.Scopes, turn.Tenant.X402Enabled, turn.Tenant.LimitsUpdateEnabled,
            turn.SessionId, turn.Tenant.Id, turn.Tier == DataTier.Anonymous ? AgentMode.Analyze : turn.Mode, turn.Paused);
        var visible = gate.ToolsFor(gateCtx);
        // All tool-bearing capabilities map back, so a call to a hidden tool is reported as such (tier or scope) by the gate.
        var wireToCapability = gate.Registry.All.Where(c => c.ToolName is not null).ToDictionary(c => PromptBuilder.WireName(c.Name), c => c.Name, StringComparer.Ordinal);
        var specs = visible.Select(ToSpec).ToList();
        var catalogue = PromptBuilder.Catalogue(visible);
        var profile = turn.Tier == DataTier.Anonymous ? ModelProfile.Anonymous : ModelProfile.Authenticated;

        var messages = await LoadHistoryAsync(turn.ConversationId, ct);
        var nextSeq = await NextSeqAsync(turn.ConversationId, ct);

        var userText = redactor.Redact(MessageRole.User, null, turn.Message);
        await StoreAsync(turn.ConversationId, nextSeq++, MessageRole.User, userText, null, null, null, null, ct);
        await RecordAsync(turn.ConversationId, EvidenceKinds.UserMessage, EvidencePayloads.Message(EvidenceKinds.UserMessage, userText), CancellationToken.None);
        AddMerged(messages, UserMessage(turn.Message, turn.Context));

        for (var round = 0; round < MaxToolRounds; round++)
        {
            var text = new StringBuilder();
            var toolUses = new List<ModelToolUse>();
            ModelMessageStop? stop = null;
            ModelUsage? usage = null;
            string? failure = null;

            var request = new ModelRequest
            {
                Profile = profile,
                Persona = PromptBuilder.Persona,
                Catalogue = catalogue,
                Messages = messages.ToList(),
                Tools = specs,
            };

            await using var stream = model.StreamAsync(request, ct).GetAsyncEnumerator(ct);
            while (true)
            {
                bool has;
                try { has = await stream.MoveNextAsync(); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    log.LogWarning("Model call failed for conversation {Conversation}: {ErrorType}", turn.ConversationId, ex.GetType().Name);
                    failure = "model_unavailable";
                    break;
                }
                if (!has) break;
                switch (stream.Current)
                {
                    case ModelTextDelta d when d.Text.Length > 0:
                        text.Append(d.Text);
                        yield return new TextDeltaEvent(d.Text);
                        break;
                    case ModelToolUse t:
                        toolUses.Add(t);
                        break;
                    case ModelMessageStop s:
                        stop = s;
                        break;
                    case ModelUsage u:
                        usage = u;
                        break;
                }
            }

            if (failure is not null)
            {
                if (text.Length > 0)
                {
                    var partial = redactor.Redact(MessageRole.Assistant, null, text.ToString());
                    await StoreAsync(turn.ConversationId, nextSeq++, MessageRole.Assistant, partial, null, null, null, null, ct);
                    await RecordAsync(turn.ConversationId, EvidenceKinds.AssistantMessage, EvidencePayloads.Message(EvidenceKinds.AssistantMessage, partial), CancellationToken.None);
                }
                yield return new ErrorEvent(failure, "The assistant could not answer right now. Please try again.", true);
                yield break;
            }

            var storedCalls = toolUses.Select(t => new StoredToolCall(t.ToolUseId, ResolveName(t.Name, wireToCapability), t.InputJson)).ToList();
            // Tool arguments are redacted by the capability's input schema before they are stored or recorded.
            var storedForDb = storedCalls.Select(c => c with { Input = redactor.Redact(MessageRole.Assistant, c.Name, RedactBySchema(c.Name, c.Input, input: true)) }).ToList();
            var assistantText = redactor.Redact(MessageRole.Assistant, null, text.ToString());
            await StoreAsync(turn.ConversationId, nextSeq++, MessageRole.Assistant, assistantText,
                storedCalls.Count == 0 ? null : JsonSerializer.Serialize(storedForDb, Json),
                null, usage?.ModelId, usage is null ? null : JsonSerializer.Serialize(usage, Json), ct);
            await RecordAsync(turn.ConversationId, EvidenceKinds.AssistantMessage,
                EvidencePayloads.Message(EvidenceKinds.AssistantMessage, assistantText, usage?.ModelId, storedCalls.Count), CancellationToken.None);
            foreach (var c in storedForDb)
                await RecordAsync(turn.ConversationId, EvidenceKinds.ToolCall, EvidencePayloads.ToolCall(c.Id, c.Name, c.Input), CancellationToken.None);

            if (toolUses.Count == 0) yield break;

            messages.Add(stop?.Message ?? AssistantMessage(text.ToString(), storedCalls));

            var results = new List<StoredToolResult>();
            var blocks = new List<ContentBlock>();
            foreach (var call in toolUses)
            {
                var capName = ResolveName(call.Name, wireToCapability);
                var cap = gate.Registry.Find(capName);
                var label = cap?.Name ?? "tool";
                yield return new ToolStatusEvent(call.ToolUseId, label, ToolRunState.Started);

                ToolExecutionResult result;
                var fromExecutor = false;
                if (!TryParse(call.InputJson, out var args))
                {
                    result = new ToolExecutionResult("The tool arguments were not valid JSON.", true);
                }
                else
                {
                    // The gate sees the model-supplied name and arguments and fails closed on anything unknown.
                    var decision = gate.Check(capName, args, gateCtx);
                    if (!decision.Allowed)
                        result = new ToolExecutionResult(decision.ModelError ?? "The tool call was refused.", true);
                    else if (!_executors.TryGetValue(decision.Capability!.Name, out var executor))
                        result = new ToolExecutionResult($"'{decision.Capability.Name}' is not available yet.", true);
                    else
                    {
                        args ??= new JsonObject();
                        var ctx = new ToolExecutionContext(decision.Capability, call.ToolUseId, turn.ConversationId,
                            turn.SessionId, turn.Tenant.Id, turn.Tier, turn.Scopes, turn.Tenant, turn.SignInAvailable);
                        label = executor.StatusLabel(args) ?? label;
                        result = await RunExecutorAsync(executor, ctx, args, ct);
                        fromExecutor = true;
                    }
                }

                // Defence in depth for indirect prompt injection; the gate above stays the authority. Gate and
                // executor errors are screened too, since error text may be upstream data; only the orchestrator's
                // own fixed messages skip it. A withheld result is also what gets stored and drops its events.
                if (fromExecutor && await toolGuard.ScreenAsync("tool." + capName, result.Content, ct) is { Withheld: true })
                    result = new ToolExecutionResult(GuardrailMessages.ToolResultWithheld, true);

                foreach (var ev in result.Events ?? []) yield return ev;
                // Holdings card: built from the (screened) wallet.balance result, never from model text, and only for signed-in users.
                if (HoldingsCardBuilder.ShouldBuild(turn.Tier, capName, fromExecutor, result.IsError)
                    && HoldingsCardBuilder.TryBuild(result.Content, clock.GetUtcNow()) is { } holdings)
                    yield return holdings;
                yield return new ToolStatusEvent(call.ToolUseId, label, result.IsError ? ToolRunState.Failed : ToolRunState.Succeeded);

                // Anything the tool's output schema marks sensitive is removed before it is stored or recorded.
                var storedContent = redactor.Redact(MessageRole.Tool, capName, RedactBySchema(capName, result.Content, input: false));
                results.Add(new StoredToolResult(call.ToolUseId, capName, storedContent, result.IsError));
                await RecordAsync(turn.ConversationId, EvidenceKinds.ToolResult, EvidencePayloads.ToolResult(call.ToolUseId, capName, result.IsError, storedContent), CancellationToken.None);
                blocks.Add(new ContentBlock
                {
                    ToolResult = new ToolResultBlock
                    {
                        ToolUseId = call.ToolUseId,
                        Status = result.IsError ? ToolResultStatus.Error : ToolResultStatus.Success,
                        Content = [new ToolResultContentBlock { Text = UntrustedData.Wrap("tool." + capName, result.Content) }],
                    },
                });
            }

            await StoreAsync(turn.ConversationId, nextSeq++, MessageRole.Tool, "", null, JsonSerializer.Serialize(results, Json), null, null, ct);
            AddMerged(messages, new ModelMessage { Role = ConversationRole.User, Content = blocks });
        }

        yield return new ErrorEvent("tool_loop_limit", "The assistant took too many steps. Please rephrase and try again.", false);
    }

    private Task RecordAsync(Guid conversationId, string kind, System.Text.Json.Nodes.JsonObject payload, CancellationToken ct) =>
        evidence is null ? Task.CompletedTask : evidence.RecordAsync(conversationId, kind, payload, ct);

    private string RedactBySchema(string capabilityName, string json, bool input)
    {
        var cap = gate.Registry.Find(capabilityName);
        return EvidencePayloads.RedactJsonText(json, input ? cap?.InputSchema : cap?.OutputSchema);
    }

    private async Task<ToolExecutionResult> RunExecutorAsync(IToolExecutor executor, ToolExecutionContext ctx, JsonNode args, CancellationToken ct)
    {
        try
        {
            return await executor.ExecuteAsync(ctx, args, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // The exception text may contain upstream data; the model and the user only learn that it failed.
            log.LogWarning("Tool {Tool} failed: {ErrorType}", ctx.Capability.Name, ex.GetType().Name);
            return new ToolExecutionResult("The tool failed. Tell the user it could not be completed.", true);
        }
    }

    private static string ResolveName(string wireName, Dictionary<string, string> wireToCapability) =>
        wireToCapability.TryGetValue(wireName, out var n) ? n : wireName;

    private static bool TryParse(string json, out JsonNode? node)
    {
        try
        {
            node = string.IsNullOrWhiteSpace(json) ? new JsonObject() : JsonNode.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            node = null;
            return false;
        }
    }

    private static ToolSpecification ToSpec(Capability c) => new()
    {
        Name = PromptBuilder.WireName(c.Name),
        Description = c.Description,
        InputSchema = new ToolInputSchema { Json = DocumentJson.FromJson(c.InputSchema!.ToJsonString()) },
    };

    /// <summary>Host page context is not the user's words: it travels as a separate, wrapped block.</summary>
    internal static ModelMessage UserMessage(string text, HostContext? context)
    {
        var content = new List<ContentBlock> { new() { Text = text } };
        if (context is not null)
        {
            static string Clip(string? s) => s is null ? "" : new string(s.Where(c => !char.IsControl(c)).Take(MaxContextFieldLength).ToArray());
            var ctx = $"route={Clip(context.Route)}\nproduct={Clip(context.Product)}\nlocale={Clip(context.Locale)}";
            content.Add(new ContentBlock { Text = UntrustedData.Wrap("host_page_context", ctx) });
        }
        return new ModelMessage { Role = ConversationRole.User, Content = content };
    }

    private static ModelMessage AssistantMessage(string text, IReadOnlyList<StoredToolCall> calls)
    {
        var content = new List<ContentBlock>();
        if (text.Length > 0) content.Add(new ContentBlock { Text = text });
        foreach (var c in calls)
            content.Add(new ContentBlock { ToolUse = new ToolUseBlock { ToolUseId = c.Id, Name = PromptBuilder.WireName(c.Name), Input = DocumentJson.FromJson(string.IsNullOrWhiteSpace(c.Input) ? "{}" : c.Input) } });
        return new ModelMessage { Role = ConversationRole.Assistant, Content = content };
    }

    private async Task<List<ModelMessage>> LoadHistoryAsync(Guid conversationId, CancellationToken ct)
    {
        var rows = await db.Messages.AsNoTracking().Where(m => m.ConversationId == conversationId).OrderBy(m => m.Seq).ToListAsync(ct);
        if (rows.Count > MaxHistoryRows)
        {
            // Cut on a user turn so no tool result is left without its tool call.
            var tail = rows.Skip(rows.Count - MaxHistoryRows).SkipWhile(r => r.Role != MessageRole.User).ToList();
            rows = tail;
        }

        var result = new List<ModelMessage>();
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            switch (r.Role)
            {
                case MessageRole.User when r.Content.Length > 0:
                    AddMerged(result, new ModelMessage { Role = ConversationRole.User, Content = [new ContentBlock { Text = r.Content }] });
                    break;
                case MessageRole.Assistant:
                {
                    var calls = ParseCalls(r.ToolCalls);
                    if (r.Content.Length == 0 && calls.Count == 0) break;
                    AddMerged(result, AssistantMessage(r.Content, calls));
                    if (calls.Count == 0) break;
                    var stored = i + 1 < rows.Count && rows[i + 1].Role == MessageRole.Tool ? ParseResults(rows[i + 1].ToolResults) : [];
                    var blocks = calls.Select(c =>
                    {
                        var res = stored.FirstOrDefault(s => s.ToolUseId == c.Id);
                        var failed = res is null || res.IsError;
                        return new ContentBlock
                        {
                            ToolResult = new ToolResultBlock
                            {
                                ToolUseId = c.Id,
                                Status = failed ? ToolResultStatus.Error : ToolResultStatus.Success,
                                Content = [new ToolResultContentBlock { Text = UntrustedData.Wrap("tool." + c.Name, res?.Content ?? "The tool did not finish.") }],
                            },
                        };
                    }).ToList();
                    AddMerged(result, new ModelMessage { Role = ConversationRole.User, Content = blocks });
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Bedrock Converse needs strictly alternating roles. A turn that ended without a reply (model failure, loop limit,
    /// cancellation) leaves adjacent same-role messages; those become one message with several content blocks.
    /// </summary>
    internal static void AddMerged(List<ModelMessage> list, ModelMessage next)
    {
        if (list.Count > 0 && list[^1].Role == next.Role)
        {
            var merged = new List<ContentBlock>(list[^1].Content);
            merged.AddRange(next.Content);
            list[^1] = new ModelMessage { Role = next.Role, Content = merged };
        }
        else list.Add(next);
    }

    private static List<StoredToolCall> ParseCalls(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<StoredToolCall>>(json, Json) ?? [];

    private static List<StoredToolResult> ParseResults(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<StoredToolResult>>(json, Json) ?? [];

    private async Task<long> NextSeqAsync(Guid conversationId, CancellationToken ct) =>
        (await db.Messages.Where(m => m.ConversationId == conversationId).MaxAsync(m => (long?)m.Seq, ct) ?? 0) + 1;

    private async Task StoreAsync(Guid conversationId, long seq, MessageRole role, string content, string? toolCalls, string? toolResults,
        string? modelId, string? usage, CancellationToken ct)
    {
        // Saved with CancellationToken.None: a disconnected browser must not lose the transcript.
        var now = clock.GetUtcNow();
        db.Messages.Add(new DataMessage
        {
            ConversationId = conversationId,
            Seq = seq,
            Role = role,
            Content = content,
            ToolCalls = toolCalls,
            ToolResults = toolResults,
            ModelId = modelId,
            Usage = usage,
            CreatedAt = now,
        });
        await db.Conversations.Where(c => c.Id == conversationId)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.LastMessageAt, now), CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
