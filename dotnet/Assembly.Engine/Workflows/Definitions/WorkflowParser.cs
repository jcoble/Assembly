using Assembly.Engine.Workflows.Policy;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Assembly.Engine.Workflows.Definitions;

public static class WorkflowParser
{
    public static WorkflowSnapshot Parse(WorkflowSource source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var yaml = source.Yaml;
        var value = WorkflowYaml.Parse(yaml, cancellationToken);
        Fields(value, "workflow", "name", "inputs", "limits", "policy", "steps");
        var name = Text(value, "name", "name");
        if (name.Any(c => c is '\r' or '\n' or '\v' or '\f' or '\u0085' or '\u2028' or '\u2029')) throw Error("name", "must be one line");
        var inputs = ImmutableSortedDictionary.CreateBuilder<string, WorkflowInputSpec>(StringComparer.Ordinal);
        if (value.TryGetProperty("inputs", out var inputMap))
        {
            Object(inputMap, "inputs");
            foreach (var input in inputMap.EnumerateObject())
            {
                var field = "inputs." + input.Name;
                Fields(input.Value, field, "description", "required", "default");
                inputs.Add(input.Name, new(OptionalText(input.Value, "description", field + ".description"), Boolean(input.Value, "required", field, false),
                    input.Value.TryGetProperty("default", out var fallback) ? fallback.Clone() : null));
            }
        }
        var limits = new WorkflowLimits();
        if (value.TryGetProperty("limits", out var limitMap))
        {
            Fields(limitMap, "limits", "max_sessions", "step_timeout_minutes", "max_rounds");
            limits = new(Positive(limitMap, "max_sessions", "limits", 3), Positive(limitMap, "step_timeout_minutes", "limits", 60), Positive(limitMap, "max_rounds", "limits", 5));
            try { _ = limits.StepTimeoutMilliseconds; }
            catch (OverflowException) { throw Error("limits.step_timeout_minutes", "milliseconds overflow"); }
        }
        var policy = ReadPolicy(value);
        var steps = ImmutableSortedDictionary.CreateBuilder<string, WorkflowStepSpec>(StringComparer.Ordinal);
        var prompts = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        if (!value.TryGetProperty("steps", out var stepMap)) throw Error("steps", "required mapping");
        Object(stepMap, "steps");
        foreach (var entry in stepMap.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = entry.Name; var step = entry.Value; var field = "steps." + id;
            if (id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) throw Error(field, "invalid step ID");
            Fields(step, field, "needs", "agent", "prompt", "run", "review", "gate", "on_fail", "expect_files", "if", "repeat", "result", "workspace", "required", "max_attempts", "display");
            var kinds = new[] { "agent", "run", "review", "gate" }.Where(k => step.TryGetProperty(k, out _)).ToArray();
            if (kinds.Length != 1) throw Error(field, "requires exactly one of agent, run, review, gate");
            var kind = kinds[0]; var isAgent = kind is "agent" or "review";
            string? provider = null, model = null, permission = null, prompt = null, target = null;
            if (isAgent)
            {
                var owner = step; var ownerField = field;
                if (kind == "review")
                {
                    owner = step.GetProperty("review"); ownerField += ".review";
                    Fields(owner, ownerField, "of", "agent", "prompt");
                    target = Text(owner, "of", ownerField + ".of");
                }
                if (!owner.TryGetProperty("agent", out var agent)) throw Error(ownerField + ".agent", "required mapping");
                Fields(agent, ownerField + ".agent", "provider", "model", "permission");
                provider = Text(agent, "provider", ownerField + ".agent.provider");
                model = OptionalText(agent, "model", ownerField + ".agent.model");
                permission = OptionalText(agent, "permission", ownerField + ".agent.permission");
                prompt = Text(owner, "prompt", ownerField + ".prompt");
                if (!source.Prompts.TryGetValue(prompt, out var content)) throw Error(ownerField + ".prompt", "missing supplied prompt content: " + prompt);
                prompts[prompt] = content;
            }
            else if (step.TryGetProperty("prompt", out _)) throw Error(field + ".prompt", "only agent steps have prompts");
            var result = OptionalText(step, "result", field + ".result");
            if (isAgent && (result is not ("build" or "spec" or "plan" or "recon" or "review" or "proof") || (result == "review") != (kind == "review")))
                throw Error(field + ".result", "required result must match step kind");
            if (!isAgent && result is not null) throw Error(field + ".result", "only agent/review steps have results");
            var commands = kind == "run" ? Strings(step, "run", field, true) : ImmutableArray<string>.Empty;
            var files = Strings(step, "expect_files", field);
            foreach (var pattern in files)
                if (pattern.Split('/', '\\').Any(p => p == "..")) throw Error(field + ".expect_files", "pattern escapes its root");
            var workspace = OptionalText(step, "workspace", field + ".workspace") ?? "new";
            if (workspace is not ("new" or "parent")) throw Error(field + ".workspace", "must be new or parent");
            WorkflowDisplay? display = null;
            if (step.TryGetProperty("display", out var labels))
            {
                Fields(labels, field + ".display", "label", "stage", "present", "past", "detail");
                display = new(OptionalText(labels, "label", field + ".display.label"),
                    DisplayText(labels, "stage", field, 40), DisplayText(labels, "present", field, 120),
                    DisplayText(labels, "past", field, 120), DisplayText(labels, "detail", field, 300));
            }
            steps.Add(id, new(id, kind == "run" ? "command" : kind, Strings(step, "needs", field), provider, model, permission, prompt, result,
                commands, Boolean(step, "repeat", field, false), target ?? OptionalText(step, "on_fail", field + ".on_fail"), files,
                OptionalText(step, "if", field + ".if"), kind == "gate" ? step.GetProperty("gate").Clone() : null,
                workspace, Boolean(step, "required", field, true), Positive(step, "max_attempts", field, 1), display, OptionalText(step, "on_fail", field + ".on_fail")));
            // Validate even an otherwise unused on_fail instead of ignoring malformed source.
            _ = OptionalText(step, "on_fail", field + ".on_fail");
        }
        ValidateGraph(steps, cancellationToken);
        var definition = new WorkflowDefinition(name, inputs.ToImmutable(), limits, policy, steps.ToImmutable());
        var snapshotPrompts = prompts.ToImmutable();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var parsed = JsonSerializer.SerializeToElement(definition, WorkflowJson.Default.WorkflowDefinition);
        using (var canonical = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(canonical)) WriteCanonical(writer, parsed);
            Hash(hash, Encoding.UTF8.GetString(canonical.GetBuffer(), 0, checked((int)canonical.Length)), cancellationToken);
        }
        foreach (var prompt in snapshotPrompts) { Hash(hash, prompt.Key, cancellationToken); Hash(hash, prompt.Value, cancellationToken); }
        return new(1, Convert.ToHexStringLower(hash.GetHashAndReset()), yaml, snapshotPrompts, definition);
    }

    public static ImmutableArray<WorkflowPromptReference> PromptPaths(string yaml, CancellationToken cancellationToken = default)
    {
        var value = WorkflowYaml.Parse(yaml, cancellationToken);
        Fields(value, "workflow", "name", "inputs", "limits", "policy", "steps");
        if (!value.TryGetProperty("steps", out var steps)) throw Error("steps", "required mapping");
        Object(steps, "steps");
        var paths = ImmutableArray.CreateBuilder<WorkflowPromptReference>();
        foreach (var entry in steps.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = entry.Value; var field = "steps." + entry.Name;
            Object(step, field);
            if (step.TryGetProperty("review", out var review)) { step = review; field += ".review"; Object(step, field); }
            else if (!step.TryGetProperty("agent", out _)) continue;
            paths.Add(new(Text(step, "prompt", field + ".prompt"), field + ".prompt"));
        }
        return paths.ToImmutable();
    }

    public static WorkflowInputs ResolveInputs(WorkflowDefinition definition, JsonElement supplied, JsonElement resolved)
    {
        Object(supplied, "inputs"); Object(resolved, "resolved");
        var inputs = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var pair in definition.Inputs)
            if (pair.Key is not ("task" or "cwd") && pair.Value.Default is { } fallback) inputs[pair.Key] = fallback;
        foreach (var pair in supplied.EnumerateObject()) if (pair.Name is not ("task" or "cwd")) inputs[pair.Name] = pair.Value.Clone();
        foreach (var pair in resolved.EnumerateObject()) inputs[pair.Name] = pair.Value.Clone();
        foreach (var pair in definition.Inputs)
            if (pair.Value.Required && (!inputs.TryGetValue(pair.Key, out var input) || input.ValueKind == JsonValueKind.Null))
                throw Error("inputs." + pair.Key, "required input is missing");
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach (var input in inputs) { writer.WritePropertyName(input.Key); WriteCanonical(writer, input.Value); }
            writer.WriteEndObject();
        }
        var bytes = output.ToArray();
        return new(JsonDocument.Parse(bytes).RootElement.Clone(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static GuardPolicy ReadPolicy(JsonElement root)
    {
        if (!root.TryGetProperty("policy", out var policy)) return GuardPolicy.Empty;
        Fields(policy, "policy", "commands", "default");
        var rules = CommandRules.Empty;
        if (policy.TryGetProperty("commands", out var commands))
        {
            Fields(commands, "policy.commands", "allow", "ask", "deny");
            rules = new(Strings(commands, "allow", "policy.commands"), Strings(commands, "ask", "policy.commands"), Strings(commands, "deny", "policy.commands"));
        }
        var decision = OptionalText(policy, "default", "policy.default") ?? "ask";
        return new(rules, decision switch { "allow" => PolicyDecision.Allow, "ask" => PolicyDecision.Ask, "deny" => PolicyDecision.Deny, _ => throw Error("policy.default", "must be allow, ask or deny") });
    }

    private static void ValidateGraph(ImmutableSortedDictionary<string, WorkflowStepSpec>.Builder steps, CancellationToken cancellationToken)
    {
        var ancestry = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var step in steps.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>(step.Needs);
            while (pending.TryPop(out var id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (id == step.Id) throw Error("steps." + step.Id + ".needs", "cyclic dependency");
                if (!steps.TryGetValue(id, out var dependency)) throw Error("steps." + step.Id + ".needs", "unknown dependency " + id);
                if (seen.Add(id)) foreach (var next in dependency.Needs) pending.Push(next);
            }
            ancestry.Add(step.Id, seen);
            if (step.OnFail is { } failure && !seen.Contains(failure)) throw Error("steps." + step.Id + ".on_fail", "must name a dependency");
            if (step.RedoTarget is { } target && !seen.Contains(target)) throw Error("steps." + step.Id + (step.Kind == "review" ? ".review.of" : ".on_fail"), "must name a dependency");
        }
        var agents = steps.Values.Where(s => s.Kind == "agent").ToArray();
        for (var i = 0; i < agents.Length; i++) for (var j = i + 1; j < agents.Length; j++)
            if (!ancestry[agents[i].Id].Contains(agents[j].Id) && !ancestry[agents[j].Id].Contains(agents[i].Id))
                throw Error("steps." + agents[i].Id + ".agent", "agent steps must be ordered: " + agents[j].Id);
    }

    private static void Object(JsonElement value, string field) { if (value.ValueKind != JsonValueKind.Object) throw Error(field, "expected mapping"); }
    private static void Fields(JsonElement value, string field, params string[] allowed)
    {
        Object(value, field);
        foreach (var property in value.EnumerateObject()) if (!allowed.Contains(property.Name)) throw Error(field == "workflow" ? property.Name : field + "." + property.Name, "unknown field");
    }
    private static string Text(JsonElement value, string key, string field)
    {
        var text = OptionalText(value, key, field);
        return string.IsNullOrWhiteSpace(text) ? throw Error(field, "required nonempty string") : text;
    }
    private static string? OptionalText(JsonElement value, string key, string field)
    {
        if (!value.TryGetProperty(key, out var property)) return null;
        if (property.ValueKind != JsonValueKind.String) throw Error(field, "expected string");
        return property.GetString();
    }
    private static string? DisplayText(JsonElement value, string key, string stepField, int maximum)
    {
        var field = stepField + ".display." + key;
        var text = OptionalText(value, key, field);
        if (text is not null && text.EnumerateRunes().Count() > maximum) throw Error(field, $"must be at most {maximum} characters");
        return text;
    }
    private static bool Boolean(JsonElement value, string key, string field, bool fallback)
    {
        if (!value.TryGetProperty(key, out var property)) return fallback;
        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Error(field + "." + key, "expected boolean");
        return property.GetBoolean();
    }
    private static long Positive(JsonElement value, string key, string field, long fallback)
    {
        if (!value.TryGetProperty(key, out var property)) return fallback;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var number) || number <= 0) throw Error(field + "." + key, "expected positive integer");
        return number;
    }
    private static ImmutableArray<string> Strings(JsonElement value, string key, string field, bool required = false)
    {
        if (!value.TryGetProperty(key, out var property)) return required ? throw Error(field + "." + key, "required list") : [];
        if (property.ValueKind != JsonValueKind.Array) throw Error(field + "." + key, "expected string list");
        var result = ImmutableArray.CreateBuilder<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw Error(field + "." + key, "expected strings");
            result.Add(item.GetString()!);
        }
        if (required && result.Count == 0) throw Error(field + "." + key, "expected nonempty list");
        return result.ToImmutable();
    }
    private static WorkflowFieldException Error(string field, string message) => new(field, message);
    private static void Hash(IncrementalHash hash, string value, CancellationToken cancellationToken)
    {
        // Length-prefix each complete component; encode incrementally without a second full byte copy.
        hash.AppendData(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder((long)Encoding.UTF8.GetByteCount(value))));
        var encoder = Encoding.UTF8.GetEncoder(); var bytes = new byte[8192]; var offset = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            encoder.Convert(value.AsSpan(offset, Math.Min(4096, value.Length - offset)), bytes, offset + 4096 >= value.Length, out var used, out var written, out _);
            hash.AppendData(bytes.AsSpan(0, written)); offset += used;
        } while (offset < value.Length);
    }
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array) { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray(); }
        else value.WriteTo(writer);
    }
}
