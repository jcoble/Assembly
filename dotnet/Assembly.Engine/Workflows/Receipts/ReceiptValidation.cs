using System.Text.Json;

namespace Assembly.Engine.Workflows.Receipts;

/// <summary>Pure shape checks only. Git, prior finding membership, real files and evidence require contextual checks.</summary>
public static class ReceiptValidation
{
    public static string? Shape(string kind, JsonElement receipt)
    {
        try
        {
            switch (kind)
            {
                case "build":
                    Object(receipt, "", "commits:a", "tests_added:a", "summary:s");
                    Strings(receipt.GetProperty("commits"), "commits"); Strings(receipt.GetProperty("tests_added"), "tests_added");
                    Text(receipt, "summary"); break;
                case "recon":
                    Object(receipt, "", "summary:s", "existing:a?", "none_found_because:s?~", "touches:a");
                    Text(receipt, "summary"); Strings(receipt.GetProperty("touches"), "touches");
                    if (receipt.TryGetProperty("existing", out var existing)) Strings(existing, "existing");
                    if (existing.ValueKind == JsonValueKind.Undefined || existing.GetArrayLength() == 0)
                        Text(receipt, "none_found_because");
                    break;
                case "spec":
                    Object(receipt, "", "url:s", "summary:s", "requirements:a", "acceptance:a", "reuse:a", "budget:s", "assumptions:a");
                    DocumentUrl(receipt, "url"); Text(receipt, "summary"); Text(receipt, "budget");
                    Strings(receipt.GetProperty("requirements"), "requirements", nonempty: true);
                    Strings(receipt.GetProperty("reuse"), "reuse"); Strings(receipt.GetProperty("assumptions"), "assumptions");
                    Acceptance(receipt.GetProperty("acceptance"), "acceptance"); break;
                case "plan": Plan(receipt); break;
                case "review": Review(receipt); break;
                case "proof":
                    Object(receipt, "", "summary:s", "files:a", "proofUrls:a", "head:s", "checks:a");
                    Text(receipt, "summary"); Strings(receipt.GetProperty("files"), "files"); Head(receipt);
                    var urls = receipt.GetProperty("proofUrls");
                    if (urls.GetArrayLength() == 0) throw Invalid("proofUrls");
                    var index = 0;
                    foreach (var url in urls.EnumerateArray()) Https(url, "proofUrls[" + index++ + "]");
                    index = 0;
                    foreach (var check in receipt.GetProperty("checks").EnumerateArray())
                    {
                        var field = "checks[" + index++ + "]";
                        Object(check, field, "name:s", "result:s", "receiptUrl:s");
                        Text(check, "name", field); Text(check, "result", field);
                        Https(check.GetProperty("receiptUrl"), field + ".receiptUrl");
                    }
                    break;
                default: return "kind";
            }
            return null;
        }
        catch (ReceiptFieldException error) { return error.Field; }
    }

    private static void Review(JsonElement receipt)
    {
        Object(receipt, "", "verdict:s", "findings:a", "accepted:a?", "new_names:a?", "head:s");
        var verdict = Text(receipt, "verdict");
        if (verdict is not ("pass" or "fail")) throw Invalid("verdict");
        Head(receipt);
        var findings = receipt.GetProperty("findings");
        if (verdict == "pass" && findings.GetArrayLength() != 0 || verdict == "fail" && findings.GetArrayLength() == 0)
            throw Invalid("verdict");
        if (receipt.TryGetProperty("accepted", out var accepted)) Strings(accepted, "accepted");
        var grades = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var finding in findings.EnumerateArray())
        {
            var field = "findings[" + index++ + "]";
            Object(finding, field, "severity:s", "late:b", "why_late:s?~", "path_line:s", "text:s", "fix:s", "grades_of_earlier:a?");
            if (Text(finding, "severity", field) is not ("blocking" or "minor")) throw Invalid(field + ".severity");
            Text(finding, "path_line", field); Text(finding, "text", field); Text(finding, "fix", field);
            if (finding.GetProperty("late").GetBoolean()) Text(finding, "why_late", field);
            if (!finding.TryGetProperty("grades_of_earlier", out var earlier)) continue;
            var gradeIndex = 0;
            foreach (var grade in earlier.EnumerateArray())
            {
                var gradeField = field + ".grades_of_earlier[" + gradeIndex++ + "]";
                Object(grade, gradeField, "text:s", "grade:s");
                var text = Text(grade, "text", gradeField).Trim();
                if (Text(grade, "grade", gradeField) is not ("fixed" or "open") || !grades.Add(text)) throw Invalid(gradeField);
            }
        }
        if (!receipt.TryGetProperty("new_names", out var names)) return;
        index = 0;
        foreach (var name in names.EnumerateArray())
        {
            var field = "new_names[" + index++ + "]";
            Object(name, field, "name:s", "verdict:s"); Text(name, "name", field);
            if (Text(name, "verdict", field) is not ("pass" or "fail")) throw Invalid(field + ".verdict");
        }
    }

    private static void Plan(JsonElement receipt)
    {
        Object(receipt, "", "url:s", "slices:a"); DocumentUrl(receipt, "url");
        var slices = receipt.GetProperty("slices"); if (slices.GetArrayLength() == 0) throw Invalid("slices");
        var graph = new Dictionary<uint, List<uint>>(); var index = 0;
        foreach (var slice in slices.EnumerateArray())
        {
            var field = "slices[" + index++ + "]";
            Object(slice, field, "number:n", "title:s", "needs:a", "budget:s", "files:a", "acceptance:a");
            if (!slice.GetProperty("number").TryGetUInt32(out var number)) throw Invalid(field + ".number");
            Text(slice, "title", field); Text(slice, "budget", field); Strings(slice.GetProperty("files"), field + ".files");
            Acceptance(slice.GetProperty("acceptance"), field + ".acceptance");
            var needs = new List<uint>();
            foreach (var need in slice.GetProperty("needs").EnumerateArray())
            {
                if (need.ValueKind != JsonValueKind.Number || !need.TryGetUInt32(out var dependency)) throw Invalid("slices.needs");
                needs.Add(dependency);
            }
            if (!graph.TryAdd(number, needs)) throw Invalid(field + ".number");
        }
        if (graph.Values.SelectMany(n => n).Any(n => !graph.ContainsKey(n))) throw Invalid("slices.needs");
        var successors = graph.Keys.ToDictionary(id => id, _ => new List<uint>());
        foreach (var (id, needs) in graph)
            foreach (var need in needs) successors[need].Add(id);
        var pending = graph.ToDictionary(pair => pair.Key, pair => pair.Value.Count);
        var ready = new Queue<uint>(pending.Where(p => p.Value == 0).Select(p => p.Key)); var seen = 0;
        while (ready.TryDequeue(out var id))
        {
            seen++;
            foreach (var next in successors[id])
                if (--pending[next] == 0) ready.Enqueue(next);
        }
        if (seen != graph.Count) throw Invalid("slices.needs");
    }

    private static void Acceptance(JsonElement items, string field)
    {
        var edgeFound = false; var index = 0;
        foreach (var item in items.EnumerateArray())
        {
            var path = field + "[" + index++ + "]";
            Object(item, path, "id:s", "text:s", "edge:b?", "fixture:s?~", "restart:b?~", "proved_by:s");
            Text(item, "id", path); Text(item, "text", path);
            var edge = item.TryGetProperty("edge", out var e) && e.GetBoolean();
            edgeFound |= edge;
            if (edge && (!item.TryGetProperty("fixture", out var fixture) || string.IsNullOrWhiteSpace(fixture.GetString())) &&
                (!item.TryGetProperty("restart", out var restart) || restart.ValueKind != JsonValueKind.True)) throw Invalid(path);
            var proof = Text(item, "proved_by", path);
            if (proof is not ("app" or "owner-check") && !(proof.StartsWith("test:", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(proof[5..])))
                throw Invalid(path + ".proved_by");
        }
        if (!edgeFound) throw Invalid(field);
    }

    private static void Object(JsonElement value, string field, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid(field);
        var known = fields.ToDictionary(f => f[..f.IndexOf(':')], StringComparer.Ordinal);
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            var path = Field(field, property.Name);
            if (!known.TryGetValue(property.Name, out var format) || !present.Add(property.Name)) throw Invalid(path);
            var type = format[format.IndexOf(':') + 1]; var kind = property.Value.ValueKind;
            if (kind == JsonValueKind.Null && format.EndsWith('~')) continue;
            if (!(type switch
            {
                's' => kind == JsonValueKind.String,
                'a' => kind == JsonValueKind.Array,
                'n' => kind == JsonValueKind.Number,
                'b' => kind is JsonValueKind.True or JsonValueKind.False,
                _ => false
            })) throw Invalid(path);
        }
        foreach (var (name, format) in known)
            if (!format.Contains('?') && !present.Contains(name)) throw Invalid(Field(field, name));
    }

    private static void Strings(JsonElement value, string field, bool nonempty = false)
    {
        if (nonempty && value.GetArrayLength() == 0) throw Invalid(field);
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())) throw Invalid(field + "[" + index + "]");
            index++;
        }
    }

    private static string Text(JsonElement value, string name, string field = "")
    {
        if (!value.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            throw Invalid(Field(field, name));
        return item.GetString()!;
    }

    private static void Head(JsonElement value)
    {
        var head = Text(value, "head");
        if (head.Length is not (40 or 64) || !head.All(Uri.IsHexDigit)) throw Invalid("head");
    }

    private static void DocumentUrl(JsonElement value, string name)
    {
        Https(value.GetProperty(name), name);
        var host = new Uri(value.GetProperty(name).GetString()!).Host;
        if (host is not ("notion.so" or "notion.com") && !host.EndsWith(".notion.so", StringComparison.OrdinalIgnoreCase) &&
            !host.EndsWith(".notion.com", StringComparison.OrdinalIgnoreCase)) throw Invalid(name);
    }

    private static void Https(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.String || !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var url) || url.Scheme != "https" || url.Host.Length == 0)
            throw Invalid(field);
    }

    private static string Field(string parent, string name) => parent.Length == 0 ? name : parent + "." + name;
    private static ReceiptFieldException Invalid(string field) => new(field.Length == 0 ? "receipt" : field);
    private sealed class ReceiptFieldException(string field) : Exception { internal string Field { get; } = field; }
}
