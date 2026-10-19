using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Biatec.Chat.Core.Capabilities;

/// <summary>
/// Minimal JSON Schema validator for tool-call arguments. Supports type, enum, required, properties,
/// additionalProperties (boolean), items, minLength, maxLength, pattern, minimum, maximum, minItems and maxItems.
/// Unsupported keywords in a schema are ignored, so registry schemas must stick to this subset.
/// </summary>
public static class JsonSchemaValidator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private const int MaxDepth = 16;

    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        "type", "enum", "required", "properties", "additionalProperties", "items", "minLength", "maxLength",
        "pattern", "minimum", "maximum", "minItems", "maxItems", "minProperties",
    };

    /// <summary>Throws if a schema uses a keyword this validator would silently ignore (fail closed at startup).</summary>
    public static void AssertSupported(JsonObject schema, string path = "$")
    {
        foreach (var (k, v) in schema)
        {
            if (!Supported.Contains(k)) throw new ArgumentException($"{path}: unsupported schema keyword '{k}'.");
            if (k == "additionalProperties" && !(v is JsonValue ap && ap.TryGetValue<bool>(out _)))
                throw new ArgumentException($"{path}: additionalProperties must be boolean.");
            if (k == "required" && !(v is JsonArray ra && ra.All(x => x is JsonValue xv && xv.TryGetValue<string>(out _))))
                throw new ArgumentException($"{path}: required must be an array of strings.");
            if (k == "enum" && v is not JsonArray) throw new ArgumentException($"{path}: enum must be an array.");
            if (k == "items" && v is not JsonObject) throw new ArgumentException($"{path}: items must be a schema object.");
        }
        if (schema["type"] is not JsonValue) throw new ArgumentException($"{path}: schema must declare a type.");
        if (schema["properties"] is JsonObject props)
            foreach (var (k, v) in props)
                if (v is JsonObject o) AssertSupported(o, $"{path}.{k}");
                else throw new ArgumentException($"{path}.{k}: property schema must be an object.");
        if (schema["items"] is JsonObject items) AssertSupported(items, path + "[]");
    }

    /// <summary>Returns a list of human-readable violations (JSON-pointer-ish path plus message); empty when valid.</summary>
    public static IReadOnlyList<string> Validate(JsonObject schema, JsonNode? value)
    {
        var errors = new List<string>();
        Check(schema, value, "$", errors, 0);
        return errors;
    }

    private static void Check(JsonObject schema, JsonNode? value, string path, List<string> errors, int depth)
    {
        if (depth > MaxDepth) { errors.Add($"{path}: nesting too deep"); return; }
        var kind = value?.GetValueKind() ?? JsonValueKind.Null;

        if (schema["type"] is JsonValue tv && tv.TryGetValue<string>(out var type) && !TypeMatches(type, value, kind))
        {
            errors.Add($"{path}: expected {type}");
            return;
        }

        if (schema["enum"] is JsonArray en && !en.Any(e => JsonNode.DeepEquals(e, value)))
            errors.Add($"{path}: not one of the allowed values");

        switch (kind)
        {
            case JsonValueKind.String:
                var s = value!.GetValue<string>();
                if (Int(schema, "minLength") is { } minL && s.Length < minL) errors.Add($"{path}: shorter than {minL}");
                if (Int(schema, "maxLength") is { } maxL && s.Length > maxL) errors.Add($"{path}: longer than {maxL}");
                if (schema["pattern"] is JsonValue pv && pv.TryGetValue<string>(out var pat))
                {
                    try
                    {
                        if (!Regex.IsMatch(s, pat, RegexOptions.CultureInvariant, RegexTimeout))
                            errors.Add($"{path}: does not match the required pattern");
                    }
                    catch (Exception ex) when (ex is RegexMatchTimeoutException or ArgumentException) { errors.Add($"{path}: pattern check failed"); }
                }
                break;

            case JsonValueKind.Number:
                if (!Elem(value!).TryGetDouble(out var d)) { errors.Add($"{path}: number out of range"); break; }
                if (Num(schema, "minimum") is { } min && d < min) errors.Add($"{path}: below minimum {min}");
                if (Num(schema, "maximum") is { } max && d > max) errors.Add($"{path}: above maximum {max}");
                break;

            case JsonValueKind.Array:
                var arr = value!.AsArray();
                if (Int(schema, "minItems") is { } minI && arr.Count < minI) errors.Add($"{path}: fewer than {minI} items");
                if (Int(schema, "maxItems") is { } maxI && arr.Count > maxI) errors.Add($"{path}: more than {maxI} items");
                if (schema["items"] is JsonObject itemSchema)
                    for (var i = 0; i < arr.Count; i++) Check(itemSchema, arr[i], $"{path}[{i}]", errors, depth + 1);
                break;

            case JsonValueKind.Object:
                var obj = value!.AsObject();
                if (Int(schema, "minProperties") is { } minP && obj.Count < minP) errors.Add($"{path}: fewer than {minP} properties");
                var props = schema["properties"] as JsonObject;
                if (schema["required"] is JsonArray req)
                    foreach (var r in req)
                        if (r?.GetValue<string>() is { } name && !obj.ContainsKey(name))
                            errors.Add($"{path}.{name}: required");
                foreach (var (k, v) in obj)
                {
                    if (props?[k] is JsonObject propSchema) Check(propSchema, v, $"{path}.{k}", errors, depth + 1);
                    else if (schema["additionalProperties"] is JsonValue ap && ap.TryGetValue<bool>(out var allowed) && !allowed)
                        errors.Add($"{path}: unexpected property"); // name omitted: it is model-controlled text
                }
                break;
        }
    }

    private static bool TypeMatches(string type, JsonNode? value, JsonValueKind kind) => type switch
    {
        "object" => kind == JsonValueKind.Object,
        "array" => kind == JsonValueKind.Array,
        "string" => kind == JsonValueKind.String,
        "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
        "null" => kind == JsonValueKind.Null,
        "number" => kind == JsonValueKind.Number,
        "integer" => kind == JsonValueKind.Number && IsInteger(value!),
        _ => false,
    };

    private static bool IsInteger(JsonNode value)
    {
        // Strict: only plain integer literals; 1.0 and 5e2 are not integers for tool arguments.
        return long.TryParse(Elem(value).GetRawText(), System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out _);
    }

    private static JsonElement Elem(JsonNode n) => JsonSerializer.SerializeToElement(n);

    private static int? Int(JsonObject o, string key) =>
        o[key] is JsonValue v && Elem(v) is { ValueKind: JsonValueKind.Number } e && e.TryGetInt32(out var i) ? i : null;

    private static double? Num(JsonObject o, string key) =>
        o[key] is JsonValue v && Elem(v) is { ValueKind: JsonValueKind.Number } e ? e.GetDouble() : null;
}
