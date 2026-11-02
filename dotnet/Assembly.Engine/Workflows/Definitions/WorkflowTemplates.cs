using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Text.Encodings.Web;
using System.Text;
using System.Text.Json;

namespace Assembly.Engine.Workflows.Definitions;

public sealed record PreparedShellCommand(string Script, IReadOnlyDictionary<string, string> Bindings);

public static class WorkflowTemplates
{
    public static string Render(string template, JsonElement values) => Substitute(template, values, static value => value);

    public static bool Condition(string expression, JsonElement values)
    {
        var rendered = Render(expression, values).Trim();
        var trimmed = expression.Trim();
        if (trimmed.StartsWith("${{", StringComparison.Ordinal) && trimmed.EndsWith("}}", StringComparison.Ordinal))
        {
            var key = trimmed[3..^2].Trim().TrimStart('!').Trim();
            var value = values;
            foreach (var part in key.Split('.'))
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value))
                    throw new WorkflowFieldException("if", "condition requires boolean value");
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new WorkflowFieldException("if", "condition requires boolean value");
        }
        return rendered switch
        {
            "true" => true,
            "false" => false,
            _ => throw new WorkflowFieldException("if", "condition must render a boolean")
        };
    }

    public static IReadOnlyList<PreparedShellCommand> Commands(IReadOnlyList<string> templates, JsonElement values) =>
        templates.Select(template =>
        {
            var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
            var script = Substitute(template, values, value =>
            {
                var name = $"ASSEMBLY_V{bindings.Count}";
                bindings.Add(name, value);
                return "${" + name + "}";
            });
            return new PreparedShellCommand(script, new ReadOnlyDictionary<string, string>(bindings));
        }).ToImmutableArray();

    private static string JsonText(JsonElement value)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) value.WriteTo(writer);
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    private static string Substitute(string template, JsonElement values, Func<string, string> emit)
    {
        var output = new StringBuilder();
        var offset = 0;
        while (true)
        {
            var start = template.IndexOf("${{", offset, StringComparison.Ordinal);
            if (start < 0) { output.Append(template, offset, template.Length - offset); break; }
            output.Append(template, offset, start - offset);
            var end = template.IndexOf("}}", start + 3, StringComparison.Ordinal);
            if (end < 0) throw new WorkflowFieldException("template", "unclosed template value");
            var expression = template[(start + 3)..end].Trim();
            var negate = expression.StartsWith('!');
            var key = negate ? expression[1..].Trim() : expression;
            var parts = key.Split('.');
            var supported = parts.Length == 2 && parts[0] switch
            {
                "inputs" => parts[1].Length > 0,
                "task" => parts[1] is "key" or "title" or "body",
                "redo" => parts[1] is "findings" or "output",
                "run" => parts[1] is "proof_dir" or "pr_body",
                _ => false
            };
            supported |= parts.Length == 3 && parts[0] == "steps" && parts[1].Length > 0 && parts[2].Length > 0;
            if (!supported) throw new WorkflowFieldException(key, "unsupported template value");
            var value = values;
            var found = true;
            foreach (var part in parts)
            {
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value)) { found = false; break; }
            }
            var missing = !found || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
            string text;
            if (missing && parts[0] == "redo" && !negate) text = "";
            else if (missing) throw new WorkflowFieldException(key, "missing template value");
            else if (negate)
            {
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new WorkflowFieldException(key, "negation requires boolean");
                text = value.GetBoolean() ? "false" : "true";
            }
            else text = value.ValueKind == JsonValueKind.String ? value.GetString()! : JsonText(value);
            output.Append(emit(text));
            offset = end + 2;
        }
        return output.ToString();
    }
}
