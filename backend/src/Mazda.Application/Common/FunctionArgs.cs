using System.Text.Json;

namespace Mazda.Application.Common;

/// <summary>
/// Shallow validation of a function-call argument object against the
/// function's <c>ParametersJsonSchema</c>.
///
/// Deliberately NOT a JSON Schema implementation. It checks the four things
/// the catalogue generator actually emits — required properties, primitive
/// types, enums, and unknown property names — and ignores everything else
/// (nested schemas, oneOf/anyOf, formats, numeric bounds). That covers the
/// mistakes a human makes while hand-testing a function, without taking on a
/// schema-validation dependency. If <c>PlanInterpreter</c>'s unimplemented
/// <c>SchemaMismatch</c> path ever needs real validation, that is the moment
/// to pull one in and retire this.
///
/// Returns null when the args are acceptable, or a human-readable sentence
/// naming what is wrong — it goes straight into a 400.
/// </summary>
public static class FunctionArgs
{
    public static string? Validate(string parametersJsonSchema, string argsJson)
    {
        JsonElement args;
        JsonDocument argsDoc;
        try
        {
            argsDoc = JsonDocument.Parse(argsJson);
            args = argsDoc.RootElement;
        }
        catch (JsonException ex)
        {
            return $"Arguments are not valid JSON: {ex.Message}";
        }

        using (argsDoc)
        {
            if (args.ValueKind != JsonValueKind.Object)
                return "Arguments must be a JSON object, e.g. {\"index\": 0}.";

            JsonElement schema;
            JsonDocument schemaDoc;
            try
            {
                schemaDoc = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(parametersJsonSchema) ? "{}" : parametersJsonSchema);
                schema = schemaDoc.RootElement;
            }
            catch (JsonException)
            {
                // A malformed schema is the function author's problem, not the
                // caller's — don't block the test run over it.
                return null;
            }

            using (schemaDoc)
            {
                if (schema.ValueKind != JsonValueKind.Object) return null;

                var props = schema.TryGetProperty("properties", out var p)
                            && p.ValueKind == JsonValueKind.Object
                    ? p
                    : default;

                // Unknown names are checked BEFORE required ones: a typo'd
                // 'labell' would otherwise be reported as "missing required
                // argument 'label'", which is true but sends the reader
                // looking in the wrong place.
                if (props.ValueKind == JsonValueKind.Object)
                {
                    var declaredNames = props.EnumerateObject().Select(x => x.Name).ToList();
                    foreach (var supplied in args.EnumerateObject())
                    {
                        if (props.TryGetProperty(supplied.Name, out _)) continue;
                        return declaredNames.Count == 0
                            ? $"'{supplied.Name}' is not an argument of this function — it takes none."
                            : $"'{supplied.Name}' is not an argument of this function. Expected: {string.Join(", ", declaredNames)}.";
                    }
                }

                // Required properties must be present and non-null.
                if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
                {
                    var missing = req.EnumerateArray()
                        .Where(r => r.ValueKind == JsonValueKind.String)
                        .Select(r => r.GetString()!)
                        .Where(name => !args.TryGetProperty(name, out var v)
                                       || v.ValueKind == JsonValueKind.Null)
                        .ToList();

                    if (missing.Count > 0)
                    {
                        return missing.Count == 1
                            ? $"Missing required argument '{missing[0]}'."
                            : $"Missing required arguments: {string.Join(", ", missing)}.";
                    }
                }

                if (props.ValueKind != JsonValueKind.Object) return null;

                foreach (var supplied in args.EnumerateObject())
                {
                    // Names were validated above, so anything here is declared.
                    if (!props.TryGetProperty(supplied.Name, out var propSchema)) continue;

                    // Null clears an optional argument; nothing to type-check.
                    if (supplied.Value.ValueKind == JsonValueKind.Null) continue;

                    var typeError = CheckType(supplied.Name, supplied.Value, propSchema);
                    if (typeError is not null) return typeError;

                    var enumError = CheckEnum(supplied.Name, supplied.Value, propSchema);
                    if (enumError is not null) return enumError;
                }
            }
        }

        return null;
    }

    private static string? CheckType(string name, JsonElement value, JsonElement propSchema)
    {
        if (!propSchema.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String)
            return null;

        var expected = t.GetString();
        var ok = expected switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            // JSON has one number type; "integer" additionally requires whole.
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => true,   // unknown/compound type declaration — not our business
        };

        return ok ? null : $"Argument '{name}' must be a {expected}, but got {Describe(value)}.";
    }

    private static string? CheckEnum(string name, JsonElement value, JsonElement propSchema)
    {
        if (!propSchema.TryGetProperty("enum", out var e) || e.ValueKind != JsonValueKind.Array)
            return null;

        var allowed = e.EnumerateArray().ToList();
        if (allowed.Count == 0) return null;

        var raw = value.GetRawText();
        if (allowed.Any(a => a.GetRawText() == raw)) return null;

        var names = string.Join(", ", allowed.Select(a =>
            a.ValueKind == JsonValueKind.String ? a.GetString() : a.GetRawText()));
        return $"Argument '{name}' must be one of: {names}.";
    }

    private static string Describe(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        JsonValueKind.Null => "null",
        _ => "an unknown value",
    };
}
