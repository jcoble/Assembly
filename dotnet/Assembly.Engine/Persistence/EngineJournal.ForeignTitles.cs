using System.Buffers;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Relays;
using Assembly.Engine.Sessions;

namespace Assembly.Engine.Persistence;

public static partial class EngineJournal
{
    internal static CommandResult? ReadForeignRegisterReceipt(IReadScope scope, string target, ForeignRegisterReceiptQuery query)
    {
        if (query.CommandId == Guid.Empty || string.IsNullOrWhiteSpace(query.FromMachine) || query.FromMachine == target ||
            string.IsNullOrWhiteSpace(query.EntityId) || query.ExpectedVersion < 0 || query.Epoch < 0 || query.Canonical is null)
            throw Refusal("bad_args");
        var canonical = ForeignRegisterCanonicalJson(query);
        var context = new CommandContext(query.CommandId, target, new(ActorKind.System, "machine:" + query.FromMachine),
            query.EntityId, query.ExpectedVersion, query.Epoch, 0);
        var row = scope.QueryFirst("SELECT fingerprint,result_json FROM engine_command_receipt WHERE command_id=$id",
            r => new ForeignRegisterReceipt(r.Text(0), r.Text(1)), new SqlArg("$id", query.CommandId.ToString("N")));
        if (row is null) return null;
        if (row.Fingerprint != Fingerprint(context, canonical)) return new(false, false, "command_id_conflict", 0, 0, query.Epoch);
        try
        {
            var result = JsonSerializer.Deserialize(row.Result, EngineJson.Default.CommandResult);
            if (result is not null && JsonSerializer.Serialize(result, EngineJson.Default.CommandResult) == row.Result) return result;
        }
        catch (JsonException) { }
        throw Refusal("unknown");
    }

    internal static string ForeignRegisterCanonicalJson(ForeignRegisterReceiptQuery query)
    {
        var value = query.Canonical;
        if (value.SmallCanonical is { } small)
        {
            if (value.CommandSha256 is not null || value.CommandUtf8Bytes is not null || value.TitleUtf8Bytes is not null ||
                Encoding.UTF8.GetByteCount(small) > 32 * 1024) throw Refusal("bad_args");
            try
            {
                if (JsonSerializer.Deserialize(small, EngineJson.Default.EngineCommand) is RegisterForeignSession foreign &&
                    foreign.Machine == query.FromMachine && JsonSerializer.Serialize<EngineCommand>(foreign, EngineJson.Default.EngineCommand) == small)
                    return small;
            }
            catch (JsonException) { }
            throw Refusal("bad_args");
        }
        if (!RelayHash.IsSha(value.CommandSha256) || value.CommandUtf8Bytes is not > 32768 || value.TitleUtf8Bytes is not >= 0)
            throw Refusal("bad_args");
        var bytes = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(bytes))
        {
            json.WriteStartObject(); json.WriteString("kind", "register_foreign_session"); json.WriteNumber("titleJournalVersion", 1);
            json.WriteString("commandSha256", value.CommandSha256); json.WriteNumber("commandUtf8Bytes", value.CommandUtf8Bytes.Value);
            json.WriteNumber("titleUtf8Bytes", value.TitleUtf8Bytes.Value); json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(bytes.WrittenSpan);
    }
    private sealed record ForeignRegisterReceipt(string Fingerprint, string Result);
}
