using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Mirror;
using Assembly.Engine.Persistence;
using Assembly.Engine.Providers;
using Assembly.Engine.Reading;

namespace Assembly.Tests.Engine.AccountIdentity;

internal sealed class AccountIdentityFixture : IDisposable
{
    internal const string Provider = "vendor/with/slash";
    internal const string Target = Provider + "/";
    internal const string RawCanary = "/synthetic-private/unique-account-home-segment-canary/";
    internal static string Opaque => "acct-v1:" + new string('a', 64);
    internal global::Assembly.Infrastructure.Topics.Topics Topics { get; } = new();
    internal EngineFixture Core { get; }
    internal AccountIdentityFixture(bool seed = true)
    {
        Core = new(topics: Topics);
        Core.Db.Writer.Write("test.frozen-account-eight", scope => scope.Execute("UPDATE engine_meta SET schema_version=8"));
        if (seed) Seed();
    }
    private void Seed()
    {
        Core.SeedSession("s", provider: Provider);
        Core.Db.Writer.Write("test.default-account-history", scope =>
        {
            scope.Execute("""
                INSERT INTO engine_provider(id,version,machine,created_ms,updated_ms,provider,account_context,installation,auth_state)
                VALUES($target,3,$machine,1,2,$provider,'','installed','signed_in');
                INSERT INTO engine_sign_in(id,version,machine,created_ms,updated_ms,provider,account_context,state,deadline_ms)
                VALUES('operation',4,$machine,1,2,$provider,'','signed_in',5000);
                """, new SqlArg("$target", Target), new("$machine", Core.Machine), new("$provider", Provider));
            var context = new CommandContext(Guid.NewGuid(), Core.Machine, new(ActorKind.System, "test"), "default-history", 0, 0, 1000);
            EngineJournal.Commit(scope, context, 0, 0,
                [new("provider.changed", "provider", Target, 3, false, new(null, null, null, null, "signed_in", 0)),
                 new("sign_in.changed", "sign_in", "operation", 4, false, new(null, null, null, null, "signed_in", 0))], []);
            scope.Execute("""INSERT INTO engine_command_receipt(command_id,fingerprint,result_json) VALUES('historic-command','exact-original-fingerprint','{ "applied": true, "late":false, "error":null, "version":1, "seq":0, "epoch":0 }')""");
            var probe = JsonSerializer.Serialize(new ProbeProviderEffect(Target, 3), EngineJson.Default.ProbeProviderEffect);
            var probeReceipt = JsonSerializer.Serialize(new EffectReceipt(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(new ProviderEffectData(Target, 3), EngineJson.Default.ProviderEffectData), null), EngineJson.Default.EffectReceipt);
            var begin = JsonSerializer.Serialize(new BeginSignInEffect("operation", Provider, null), EngineJson.Default.BeginSignInEffect);
            var beginReceipt = JsonSerializer.Serialize(new EffectReceipt(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(new SignInEffectData("operation", "started"), EngineJson.Default.SignInEffectData), null), EngineJson.Default.EffectReceipt);
            foreach (var effect in new[] { (Id: "probe_provider/" + Target + "/3", Kind: "probe_provider", Payload: probe, Receipt: probeReceipt), (Id: "begin_sign_in/operation", Kind: "begin_sign_in", Payload: begin, Receipt: beginReceipt) })
                scope.Execute("""
                    INSERT INTO engine_outbox(id,version,machine,created_ms,updated_ms,event_seq,kind,payload_json,state,attempt,receipt_json,order_key)
                    VALUES($id,2,$machine,1,2,2,$kind,$payload,'done',1,$receipt,$order)
                    """, new("$id", effect.Id), new("$machine", Core.Machine), new("$kind", effect.Kind), new("$payload", effect.Payload), new("$receipt", effect.Receipt), new("$order", "provider/" + Target));
            MirrorApply.Open(scope, "peer", "Workbox Test", 1000); MirrorApply.BeginSnapshot(scope, "peer", 1000);
            var provider = JsonSerializer.SerializeToElement(new { entityKind = "provider", id = Target, version = 3, machine = "peer", createdMs = 1, updatedMs = 2, provider = Provider, accountContext = "", installation = "installed", authState = "signed_in", factsJson = (string?)null, checkedMs = (long?)null, reason = (string?)null });
            var signIn = JsonSerializer.SerializeToElement(new { entityKind = "sign_in", id = "remote-operation", version = 4, machine = "peer", createdMs = 1, updatedMs = 2, provider = Provider, accountContext = "", methodId = (string?)null, state = "signed_in", deadlineMs = 5000, originNeedId = (string?)null, reason = (string?)null });
            MirrorApply.SnapshotPage(scope, "peer", [provider, signIn]); MirrorApply.EndSnapshot(scope, "peer", 0, null, 1000);
            MirrorApply.Events(scope, "peer", 0, 1, new CursorAt("provider", Target, 3),
                [new("peer", 1, "provider.changed", "provider", Target, 3, 1000, false, new(null, null, null, null, "signed_in", 0))], [], 1000);
            return 0;
        });
    }
    internal void Context(string entity, string account)
    {
        var table = entity switch { "session" => "engine_session", "provider" => "engine_provider", _ => "engine_sign_in" };
        Core.Db.Writer.Write("test.account-context", scope => scope.Execute($"UPDATE {table} SET account_context=$account", new SqlArg("$account", account)));
    }
    internal void Version(int version) => Core.Db.Writer.Write("test.account-schema-version", scope => scope.Execute("UPDATE engine_meta SET schema_version=$version", new SqlArg("$version", version)));
    internal EngineSchemaInitialization Initialize() => Core.Db.Writer.Write("test.account-initialize", scope => EngineSchema.Initialize(scope, 1001));
    internal string Digest() => Core.Db.Query("SELECT name FROM sqlite_master WHERE type='table' AND substr(name,1,7)='engine_' ORDER BY name", r => r.Text(0), default)
        .Aggregate(new StringBuilder(), (body, table) =>
        {
            var columns = Core.Db.Query($"PRAGMA table_info({table})", r => r.Text(1), default).Where(name => table != "engine_meta" || name != "schema_version").OrderBy(name => name, StringComparer.Ordinal);
            foreach (var value in Core.Db.Query($"SELECT json_array({string.Join(',', columns.Select(name => '"' + name + '"'))}) AS body FROM {table} ORDER BY body", r => r.Text(0), default)) body.Append(table).Append(':').Append(value).Append('\n');
            return body;
        }) is { } text ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))) : throw new InvalidOperationException();
    public void Dispose() => Core.Dispose();
}
