using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Engine.Contracts;

namespace Assembly.Engine.Sessions;

[JsonConverter(typeof(StartCanonicalConverter))]
public abstract record StartCanonical
{
    private protected StartCanonical() { }
}
public sealed record StartCanonicalSmall(string Canonical) : StartCanonical;
public sealed record StartCanonicalLarge(string CommandSha256, long CommandUtf8Bytes, long TitleUtf8Bytes) : StartCanonical;
[JsonConverter(typeof(MachineStartAckArgsConverter))]
public sealed record MachineStartAckArgs(Guid CommandId, string FromMachine, string EntityId, long ExpectedVersion, long Epoch, StartCanonical Canonical);
public enum MachineStartAckState { Accepted, Refused, Missing, Conflict, Unknown }
public sealed record OwningStartedChild(string Id, string Machine, string Holder, long Version, long Epoch, long? ReservedEpoch,
    string State, bool CancelRequested, string? ParentId, string? RunId);
public sealed record MachineStartAck(MachineStartAckState State, CommandResult? Recorded, OwningStartedChild? Child);

public sealed class StartCanonicalConverter : JsonConverter<StartCanonical>
{
    public override StartCanonical Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var json = JsonDocument.ParseValue(ref reader); return Parse(json.RootElement);
    }
    internal static StartCanonical Parse(JsonElement value)
    {
        var fields = Fields(value);
        if (fields.Count == 1 && fields.TryGetValue("canonical", out var small)) return new StartCanonicalSmall(Text(small));
        if (fields.Count == 3 && fields.ContainsKey("commandSha256") && fields.ContainsKey("commandUtf8Bytes") && fields.ContainsKey("titleUtf8Bytes"))
            return new StartCanonicalLarge(Text(fields["commandSha256"]), Number(fields["commandUtf8Bytes"]), Number(fields["titleUtf8Bytes"]));
        throw new JsonException("Invalid Start canonical variant.");
    }
    internal static Dictionary<string, JsonElement> Fields(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Expected object.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!fields.TryAdd(property.Name, property.Value)) throw new JsonException("Duplicate field.");
        return fields;
    }
    internal static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String && value.GetString() is { } text
        ? text : throw new JsonException("Expected string.");
    internal static long Number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
        ? number : throw new JsonException("Expected integer.");
    public override void Write(Utf8JsonWriter writer, StartCanonical value, JsonSerializerOptions options) => WriteValue(writer, value);
    internal static void WriteValue(Utf8JsonWriter writer, StartCanonical value)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case StartCanonicalSmall small: writer.WriteString("canonical", small.Canonical); break;
            case StartCanonicalLarge large:
                writer.WriteString("commandSha256", large.CommandSha256); writer.WriteNumber("commandUtf8Bytes", large.CommandUtf8Bytes);
                writer.WriteNumber("titleUtf8Bytes", large.TitleUtf8Bytes); break;
            default: throw new JsonException("Invalid Start canonical variant.");
        }
        writer.WriteEndObject();
    }
}
public sealed class MachineStartAckArgsConverter : JsonConverter<MachineStartAckArgs>
{
    public override MachineStartAckArgs Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var json = JsonDocument.ParseValue(ref reader); var fields = StartCanonicalConverter.Fields(json.RootElement);
        string[] names = ["commandId", "fromMachine", "entityId", "expectedVersion", "epoch", "canonical"];
        if (fields.Count != names.Length || names.Any(name => !fields.ContainsKey(name)) || !Guid.TryParse(StartCanonicalConverter.Text(fields["commandId"]), out var id))
            throw new JsonException("Invalid Start ACK fields.");
        return new(id, StartCanonicalConverter.Text(fields["fromMachine"]), StartCanonicalConverter.Text(fields["entityId"]),
            StartCanonicalConverter.Number(fields["expectedVersion"]), StartCanonicalConverter.Number(fields["epoch"]), StartCanonicalConverter.Parse(fields["canonical"]));
    }
    public override void Write(Utf8JsonWriter writer, MachineStartAckArgs value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("commandId", value.CommandId); writer.WriteString("fromMachine", value.FromMachine);
        writer.WriteString("entityId", value.EntityId); writer.WriteNumber("expectedVersion", value.ExpectedVersion); writer.WriteNumber("epoch", value.Epoch);
        writer.WritePropertyName("canonical"); StartCanonicalConverter.WriteValue(writer, value.Canonical); writer.WriteEndObject();
    }
}
