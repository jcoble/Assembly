using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;

namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>Saved machine profiles, written atomically before their runtime resources change.</summary>
public sealed class MachineStore
{
    internal const string Changes = "saved-machines";
    private readonly IAppDatabase _db;
    internal MachineStatus? Status { get; set; }
    public MachineStore(IAppDatabase db)
    {
        _db = db;
        db.Writer.Write("machines.schema", scope => scope.Execute("CREATE TABLE IF NOT EXISTS saved_machines(name TEXT PRIMARY KEY, ssh TEXT NOT NULL, port INTEGER NOT NULL, token_file TEXT NOT NULL);"));
    }

    public void Import(SavedMachine profile) => _db.Writer.Write("machines.import", scope => { Set(scope, profile); return 0; });

    public IReadOnlyList<SavedMachine> All(CancellationToken cancel) => _db.Query(
        "SELECT name, ssh, port, token_file FROM saved_machines ORDER BY name;", Read, cancel);
    public SavedMachine? Get(string name, CancellationToken cancel) => _db.QueryFirst(
        "SELECT name, ssh, port, token_file FROM saved_machines WHERE name = $name;", Read, cancel, new SqlArg("$name", name));
    private static SavedMachine Read(IRow row) => new(row.Text(0), row.Text(1), (int)row.Long(2), row.Text(3));

    public void Set(IWriteScope scope, SavedMachine profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name == "local" || string.IsNullOrWhiteSpace(profile.Ssh)
            || profile.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(profile.TokenFile))
            throw new CallFailed("expected a non-local name, SSH destination, port and token file");
        scope.Execute("INSERT INTO saved_machines(name, ssh, port, token_file) VALUES ($name, $ssh, $port, $file) ON CONFLICT(name) DO UPDATE SET ssh=excluded.ssh, port=excluded.port, token_file=excluded.token_file;",
            new SqlArg("$name", profile.Name), new SqlArg("$ssh", profile.Ssh), new SqlArg("$port", profile.Port), new SqlArg("$file", profile.TokenFile));
        Status?.Retire(profile.Name);
        scope.Publish(MachinesFeature.Topic, profile.Name, Item(profile), SessionsJson.Default.MachineItem);
        scope.Publish(Changes, profile.Name, profile, SessionsJson.Default.SavedMachine);
    }

    public void Remove(IWriteScope scope, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "local") throw new CallFailed("expected a remote name");
        scope.Execute("DELETE FROM saved_machines WHERE name=$name;", new SqlArg("$name", name));
        Status?.Retire(name);
        scope.Publish(MachinesFeature.Topic, name, (MachineItem)null!, SessionsJson.Default.MachineItem);
        scope.Publish(Changes, name, (SavedMachine)null!, SessionsJson.Default.SavedMachine);
    }

    public IReadOnlyList<string> RunningTurns(string name) => _db.Query("""
        SELECT DISTINCT r.session_id FROM rows r JOIN session_info s ON s.session_id=r.session_id
        WHERE s.machine=$name AND r.kind='turn' AND r.run_id IS NOT NULL AND json_extract(r.payload,'$.state')='started'
          AND NOT EXISTS (SELECT 1 FROM rows e WHERE e.session_id=r.session_id AND e.run_id=r.run_id AND e.kind='turn' AND e.seq>r.seq)
        ORDER BY r.session_id;
        """, row => row.Text(0), CancellationToken.None, new SqlArg("$name", name));

    public IReadOnlyList<string> RunningHelpers(string name) => _db.Query("""
        SELECT DISTINCT json_extract(r.payload,'$.sessionId') AS child FROM rows r
        JOIN session_info s ON s.session_id=json_extract(r.payload,'$.sessionId')
        WHERE s.machine=$name AND r.kind='subagent' AND json_extract(r.payload,'$.state') IN ('running','requires_action')
          AND NOT EXISTS (SELECT 1 FROM rows e WHERE e.session_id=r.session_id AND e.item_id=r.item_id AND e.kind='subagent' AND e.seq>r.seq)
        ORDER BY child;
        """, row => row.Text(0), CancellationToken.None, new SqlArg("$name", name));

    public static MachineItem Item(SavedMachine profile) => new(profile.Name, "remote", false, "not checked yet", false, profile.Ssh, profile.Port, profile.TokenFile);
}
