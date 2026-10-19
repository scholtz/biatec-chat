using SessionTier = Biatec.Chat.Core.Data.SessionTier;
using ActionPreview = Biatec.Chat.Core.Data.ActionPreview;
using ActionStep = Biatec.Chat.Core.Data.ActionStep;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Biatec.Chat.Api.Auth.Elevation;
using Biatec.Chat.Api.Chat.Confirm;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Limits;
using Biatec.Chat.Api.Orchestration;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Core.Capabilities;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Biatec.Chat.Core.Previews;
using Biatec.Chat.Tools.Limits;
using Biatec.Chat.Tools.Mcp;
using Biatec.Chat.Tools.Signing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Biatec.Chat.Tests.Limits;

/// <summary>The lowering-only policy, the limits HTTP client and the capability gate (T-026). No database.</summary>
public sealed class LimitsPolicyTests
{
    internal static SpendingLimit L(string key, string amount) => new(key, LimitsPolicy.PeriodOf(key), amount, "EUR");

    internal static readonly IReadOnlyList<SpendingLimit> Current = [L("per_day_eur", "500"), L("per_week_eur", "2000"), L("per_month_eur", "5000")];

    internal static Dictionary<string, string> Req(params (string, string)[] items) => items.ToDictionary(i => i.Item1, i => i.Item2);

    // ---- policy ----

    [Fact]
    public void Parse_reads_the_nested_map_with_currency_from_the_key()
    {
        var l = LimitsPolicy.ParseLimits("""{"limits":{"per_day_eur":"500","per_transaction_usd":1000,"note":"x","cap":null}}""")!;
        Assert.Equal(2, l.Count);
        Assert.Equal(("daily", "500", "EUR"), (l[0].Period, l[0].Amount, l[0].Currency));
        Assert.Equal("1000 USD", l[1].Display);
    }

    [Fact]
    public void Parse_defaults_the_currency_to_eur_or_the_top_level_one()
    {
        Assert.Equal("EUR", LimitsPolicy.ParseLimits("""{"daily":"5"}""")![0].Currency);
        Assert.Equal("CHF", LimitsPolicy.ParseLimits("""{"currency":"chf","daily":"5"}""")![0].Currency);
        Assert.Null(LimitsPolicy.ParseLimits("[1]"));
        Assert.Null(LimitsPolicy.ParseLimits("not json"));
    }

    [Fact]
    public void Lowering_builds_a_full_body_with_only_the_lowered_limit_changed()
    {
        var (plan, refusal) = LimitsPolicy.Plan(Current, Req(("daily", "200")));
        Assert.Null(refusal);
        Assert.Equal("200", plan!.Body["per_day_eur"]);
        Assert.Equal("2000", plan.Body["per_week_eur"]);
        Assert.Equal("5000", plan.Body["per_month_eur"]);
        var ch = Assert.Single(plan.Changes);
        Assert.Equal(("daily", "500", "200"), (ch.Period, ch.From, ch.To));
        Assert.Equal(64, plan.RequestHash.Length);
    }

    [Fact]
    public void Request_hash_is_the_sha256_of_the_canonical_body_like_the_protocol_vector()
    {
        var (canonical, hash) = LimitsPolicy.Canonicalize(Req(("per_transaction_eur", "2000"), ("per_day_eur", "5000")));
        Assert.Equal("""{"per_day_eur":"5000","per_transaction_eur":"2000"}""", canonical);
        Assert.Equal("22091d0dfc4ee5068e79431862533b695bb5f70ec0f4a6ccb01b870541deaf90", hash);
    }

    [Theory]
    [InlineData("501")]
    [InlineData("100000")]
    public void Any_raise_is_refused_with_an_explanation(string amount)
    {
        var (plan, refusal) = LimitsPolicy.Plan(Current, Req(("daily", amount)));
        Assert.Null(plan);
        Assert.Equal("raise_refused", refusal!.Code);
        Assert.Contains("only lower", refusal.Message);
    }

    [Fact]
    public void A_raise_next_to_a_lowering_refuses_the_whole_request()
    {
        var (plan, refusal) = LimitsPolicy.Plan(Current, Req(("daily", "100"), ("weekly", "9999")));
        Assert.Null(plan);
        Assert.Equal("raise_refused", refusal!.Code);
    }

    [Fact]
    public void Unchanged_unknown_and_invalid_requests_are_refused()
    {
        Assert.Equal("no_change", LimitsPolicy.Plan(Current, Req(("daily", "500"))).Refusal!.Code);
        Assert.Equal("no_change", LimitsPolicy.Plan(Current, Req()).Refusal!.Code);
        Assert.Equal("unknown_limit", LimitsPolicy.Plan([L("per_day_eur", "5")], Req(("weekly", "1"))).Refusal!.Code);
        Assert.Equal("invalid_amount", LimitsPolicy.Plan(Current, Req(("daily", "-1"))).Refusal!.Code);
        Assert.Equal("invalid_amount", LimitsPolicy.Plan(Current, Req(("daily", "abc"))).Refusal!.Code);
    }

    [Fact]
    public void CheckLowering_refuses_a_body_that_would_now_raise_a_limit()
    {
        Assert.Null(LimitsPolicy.CheckLowering(Current, Req(("per_day_eur", "500"), ("per_week_eur", "10"))));
        Assert.Equal("raise_refused", LimitsPolicy.CheckLowering([L("per_day_eur", "100")], Req(("per_day_eur", "200")))!.Code);
        Assert.Equal("unknown_limit", LimitsPolicy.CheckLowering(Current, Req(("per_year_eur", "1")))!.Code);
    }

    [Fact]
    public void Registry_hides_limits_update_without_the_tenant_flag_but_not_limits_view()
    {
        var reg = CapabilityRegistry.CreateDefault();
        var off = new GateContext(SessionTier.Authenticated, ["openid"], LimitsUpdateEnabled: false);
        var on = off with { LimitsUpdateEnabled = true };
        Assert.DoesNotContain(reg.ToolsFor(off), c => c.Name == "limits.update");
        Assert.Contains(reg.ToolsFor(on), c => c.Name == "limits.update");
        Assert.Contains(reg.ToolsFor(off), c => c.Name == "limits.view");
    }

    // ---- the HTTP client ----

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Path, string? Auth, string? Body)> Calls = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return respond(request);
        }
    }

    internal static HttpResponseMessage Json(int status, string json) =>
        new((System.Net.HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static WalletLimitsClient ClientFor(StubHandler h) => new(new HttpClient(h) { BaseAddress = new Uri("https://oidc.example.test/") });

    [Fact]
    public async Task Client_gets_limits_from_the_oidc_wallet_endpoint_with_the_bearer()
    {
        var h = new StubHandler(_ => Json(200, """{"limits":{"per_day_eur":"500"}}"""));
        var r = await ClientFor(h).GetAsync("TOKEN");
        Assert.True(r.Ok);
        var call = Assert.Single(h.Calls);
        Assert.Equal((HttpMethod.Get, "/wallet/limits", "Bearer TOKEN"), (call.Method, call.Path, call.Auth));
    }

    [Fact]
    public async Task Client_puts_the_body_and_classifies_failures()
    {
        var h = new StubHandler(_ => Json(200, "{}"));
        var ok = await ClientFor(h).PutAsync("T", Req(("per_day_eur", "200")));
        Assert.True(ok.Applied);
        Assert.Equal((HttpMethod.Put, "/wallet/limits", """{"per_day_eur":"200"}"""), (h.Calls[0].Method, h.Calls[0].Path, h.Calls[0].Body));

        Task<LimitsWriteResult> Put(int status, string json) => ClientFor(new StubHandler(_ => Json(status, json))).PutAsync("T", Req(("a", "1")));
        Assert.Equal(LimitsFailure.StepUp, (await Put(401, """{"error":"insufficient_user_authentication","auth_req_id":"x"}""")).Failure);
        Assert.Equal(LimitsFailure.Unauthorized, (await Put(401, """{"error":"invalid_token"}""")).Failure);
        Assert.Equal(LimitsFailure.Forbidden, (await Put(403, """{"error":"insufficient_scope"}""")).Failure);
        Assert.Equal(LimitsFailure.Unavailable, (await Put(503, "{}")).Failure);
        Assert.Equal(LimitsFailure.Rejected, (await Put(400, "{}")).Failure);
    }

    [Fact]
    public async Task Client_network_failure_is_an_outage()
    {
        var h = new StubHandler(_ => throw new HttpRequestException("down"));
        var r = await ClientFor(h).GetAsync("T");
        Assert.Equal(LimitsFailure.Unavailable, r.Failure);
    }

}
