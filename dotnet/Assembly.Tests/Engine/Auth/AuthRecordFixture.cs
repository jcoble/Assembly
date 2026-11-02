using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Persistence;
using Assembly.Application.Common.Database;

namespace Assembly.Tests.Engine.Auth;

internal sealed class AuthRecordFixture : IDisposable
{
    internal EngineFixture Core { get; } = new();
    internal EngineCommands Commands { get; }
    internal AuthRecordFixture(Func<string, string, bool>? configured) =>
        Commands = new(Core.Machine, Core.Limits, isConfiguredProvider: configured);
    internal CommandContext Context(string id, long version = 0, EngineActor? actor = null, Guid? command = null) =>
        new(command ?? Guid.NewGuid(), Core.Machine, actor ?? new(ActorKind.Window, "owner"), id, version, 0, Core.Time.GetUtcNow().ToUnixTimeMilliseconds());
    internal CommandResult Apply(EngineCommand command, string id, long version = 0, EngineActor? actor = null, Guid? commandId = null) =>
        Apply(Context(id, version, actor, commandId), command);
    internal CommandResult Apply(CommandContext context, EngineCommand command) =>
        Core.Db.Writer.Write("test.auth.record", s => Commands.Apply(s, context, command));
    internal string? Text(string table, string id, string column) => Core.Text($"SELECT {column} FROM {table} WHERE id=$id", new SqlArg("$id", id));
    internal long Version(string table, string id) => Core.Number($"SELECT version FROM {table} WHERE id=$id", new SqlArg("$id", id));
    internal Effect Effect(string id) => EffectTransitions.Read(Core.Db, id, default)!;
    internal void SeedProvider(string provider = "codex", string account = "", string state = "needs_sign_in") =>
        Core.Db.Writer.Write("test.auth.provider", s => s.Execute("""
            INSERT INTO engine_provider(id,version,machine,created_ms,updated_ms,provider,account_context,installation,auth_state)
            VALUES($id,1,$machine,1000,1000,$provider,$account,'unknown',$state)
            """, new("$id", provider + "/" + account), new("$machine", Core.Machine), new("$provider", provider), new("$account", account), new("$state", state)));
    public void Dispose() => Core.Dispose();
}
