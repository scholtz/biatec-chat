using System.Text.Json;
using Amazon.Runtime.Documents;

namespace Biatec.Chat.Api.Model;

/// <summary>Converts JSON to the SDK's <see cref="Document"/>, used for tool input schemas and tool-use inputs.</summary>
public static class DocumentJson
{
    public static Document FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return FromElement(doc.RootElement);
    }

    public static Document FromElement(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => new Document(FromObject(e)),
        JsonValueKind.Array => new Document(e.EnumerateArray().Select(FromElement).ToList()),
        JsonValueKind.String => new Document(e.GetString()!),
        JsonValueKind.Number when e.TryGetInt64(out var l) => new Document(l),
        // Document has no ulong or decimal, so an integer beyond Int64 becomes a double. Callers that need exact
        // values (an Algorand uint64) read the raw JSON, which ModelToolUse.InputJson keeps.
        JsonValueKind.Number => new Document(e.GetDouble()),
        JsonValueKind.True => new Document(true),
        JsonValueKind.False => new Document(false),
        _ => new Document(),
    };

    // A duplicate key keeps its last value, as JSON.parse does.
    private static Dictionary<string, Document> FromObject(JsonElement e)
    {
        var d = new Dictionary<string, Document>();
        foreach (var p in e.EnumerateObject()) d[p.Name] = FromElement(p.Value);
        return d;
    }
}
