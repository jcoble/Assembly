using System.Text.Json;
using Assembly.Application.Common.Acp.V1;
using Assembly.Application.Common.Database;

using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Sessions;
namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>
/// The sessions list, in the same SQLite file as the rows. Writes here are rare (create, title, send) and go through
/// the file's one writer, like the row commits.
/// </summary>
public sealed class SessionStore
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS session_info(session_id TEXT PRIMARY KEY, agent TEXT NOT NULL, repo_url TEXT NOT NULL,
          title TEXT, acp_session_id TEXT, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, machine TEXT,
          session_cost TEXT, completed_at INTEGER, settled_at INTEGER);
        CREATE INDEX IF NOT EXISTS session_info_updated ON session_info(updated_at);
        CREATE TABLE IF NOT EXISTS session_config(session_id TEXT PRIMARY KEY, options TEXT, choices TEXT NOT NULL DEFAULT '{}');
        CREATE TABLE IF NOT EXISTS agent_config(agent TEXT PRIMARY KEY, options TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS session_commands(session_id TEXT PRIMARY KEY, commands TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS agent_commands(agent TEXT PRIMARY KEY, commands TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS helper_messages(message_id TEXT PRIMARY KEY, from_session TEXT NOT NULL, to_session TEXT NOT NULL,
          text TEXT NOT NULL, run_id TEXT, created_at INTEGER NOT NULL, error TEXT, state TEXT, reply TEXT);
        CREATE INDEX IF NOT EXISTS helper_messages_pair ON helper_messages(from_session, to_session);
        """;

    private const string Columns = "session_id, agent, repo_url, title, acp_session_id, updated_at, machine, session_cost, parent_session_id, helper, read_seq, completed_at, settled_at, title_source";

    // A database made before sessions had a machine gets the column; its sessions all ran where it lives.
    private const string HasMachine = "SELECT COUNT(*) FROM pragma_table_info('session_info') WHERE name = 'machine';";

    // And one made before sessions were closed after each turn gets the saved cost, empty.
    private const string HasSessionCost = "SELECT COUNT(*) FROM pragma_table_info('session_info') WHERE name = 'session_cost';";

    // And one made before subagents gets the parent link, empty: none of its sessions is a child.
    private const string HasParent = "SELECT COUNT(*) FROM pragma_table_info('session_info') WHERE name = 'parent_session_id';";

    // And one made before helper sessions marks none of its sessions a helper.
    private const string HasHelper = "SELECT COUNT(*) FROM pragma_table_info('session_info') WHERE name = 'helper';";

    // And one made before sessions were marked read starts with each read up to its last row, so none is unread.
    private const string HasReadSeq = "SELECT COUNT(*) FROM pragma_table_info('session_info') WHERE name = 'read_seq';";

    private const string HasCompletedAt = "SELECT COUNT(*) FROM pragma_table_info('session_info') WHERE name = 'completed_at';";
    private const string HasSettledAt = "SELECT COUNT(*) FROM pragma_table_info('session_info') WHERE name = 'settled_at';";
    // And one made before titles had a source: each title it has came from its session's first message.
    private const string HasTitleSource = "SELECT COUNT(*) FROM pragma_table_info('session_info') WHERE name = 'title_source';";

    private const string HasRows = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'rows';";

    // And one made before a message could fail gets the error, empty.
    private const string HasMessageError = "SELECT COUNT(*) FROM pragma_table_info('helper_messages') WHERE name = 'error';";

    // And one made before messages crossed machines gets their answer, empty.
    private const string HasMessageAnswer = "SELECT COUNT(*) FROM pragma_table_info('helper_messages') WHERE name = 'state';";

    private const string MessageColumns = "message_id, from_session, to_session, text, run_id, created_at, error, state, reply";

    private readonly IAppDatabase _db;
    private readonly IAtomicWriter _writer;
    private readonly TimeProvider _time;
    private readonly SessionSummaries? _summaries;
    private readonly string _checkouts;

    private SessionStore(IAppDatabase db, IAtomicWriter writer, TimeProvider time, SessionSummaries? summaries, string checkouts)
    {
        _summaries = summaries;
        _checkouts = checkouts;
        _db = db;
        _writer = writer;
        _time = time;
    }

    /// <summary><paramref name="checkouts"/> is the folder holding this machine's sessions' checkouts.</summary>
    public static SessionStore Open(IAppDatabase db, TimeProvider time, string checkouts, SessionSummaries? summaries = null)
    {
        db.Writer.Write("sessions.schema", scope =>
        {
            bool Missing(string sql) => scope.QueryFirst(sql, row => row.Long(0)) == 0;
            Chat.Schema.RenameOldNames(scope);
            scope.Execute(Schema);
            if (Missing(HasMachine))
                scope.Execute("ALTER TABLE session_info ADD COLUMN machine TEXT;");
            if (Missing(HasSessionCost))
                scope.Execute("ALTER TABLE session_info ADD COLUMN session_cost TEXT;");
            if (Missing(HasParent))
                scope.Execute("ALTER TABLE session_info ADD COLUMN parent_session_id TEXT;");
            if (Missing(HasHelper))
                scope.Execute("ALTER TABLE session_info ADD COLUMN helper INTEGER NOT NULL DEFAULT 0;");
            // The column and its values in one write: a start that stops between them would leave every session unread.
            if (Missing(HasReadSeq))
            {
                scope.Execute("ALTER TABLE session_info ADD COLUMN read_seq INTEGER NOT NULL DEFAULT 0;");
                if (!Missing(HasRows))
                    scope.Execute("UPDATE session_info SET read_seq = COALESCE((SELECT MAX(seq) FROM rows WHERE rows.session_id = session_info.session_id), 0);");
            }
            if (Missing(HasCompletedAt))
                scope.Execute("ALTER TABLE session_info ADD COLUMN completed_at INTEGER;");
            if (Missing(HasSettledAt))
                scope.Execute("ALTER TABLE session_info ADD COLUMN settled_at INTEGER;");
            if (Missing(HasTitleSource))
                scope.Execute("ALTER TABLE session_info ADD COLUMN title_source TEXT;");
            if (Missing(HasMessageError))
                scope.Execute("ALTER TABLE helper_messages ADD COLUMN error TEXT;");
            if (Missing(HasMessageAnswer))
                scope.Execute("ALTER TABLE helper_messages ADD COLUMN state TEXT; ALTER TABLE helper_messages ADD COLUMN reply TEXT;");
            return 0;
        });
        return new SessionStore(db, db.Writer, time, summaries, checkouts);
    }

    /// <summary>A config's chosen values, from its JSON object; anything not a string is left out.</summary>
    internal static IReadOnlyDictionary<string, string> ReadChoices(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject()
            .Where(choice => choice.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(choice => choice.Name, choice => choice.Value.GetString()!);
    }

    public SessionInfo Create(string agent, string repoUrl)
    {
        var session = new SessionInfo(Guid.NewGuid().ToString(), agent, repoUrl, null, null, Now());
        Change(session.SessionId,
            "INSERT INTO session_info(session_id, agent, repo_url, created_at, updated_at) VALUES ($id, $agent, $repo, $now, $now);",
            ("$id", session.SessionId), ("$agent", agent), ("$repo", repoUrl), ("$now", session.UpdatedAt));
        return session;
    }

    /// <summary>Records a session another machine created and runs, under that machine's id for it.</summary>
    public SessionInfo Add(string sessionId, string agent, string repoUrl, string machine, string? parentSessionId = null, bool helper = false)
    {
        var session = new SessionInfo(sessionId, agent, repoUrl, null, null, Now(), machine, ParentSessionId: parentSessionId, Helper: helper);
        Change(sessionId, """
            INSERT INTO session_info(session_id, agent, repo_url, created_at, updated_at, machine, parent_session_id, helper)
            VALUES ($id, $agent, $repo, $now, $now, $machine, $parent, $helper);
            """, ("$id", sessionId), ("$agent", agent), ("$repo", repoUrl), ("$now", session.UpdatedAt), ("$machine", machine),
            ("$parent", parentSessionId), ("$helper", helper ? 1 : 0));
        return session;
    }

    /// <summary>
    /// The session of the parent's subagent with that child session id, made the first time it's asked for. A child
    /// keeps its session for every delegation to it, so each one goes on in the same session.
    /// </summary>
    public SessionInfo Child(SessionInfo parent, string childSessionId, string title) =>
        // The look-up and the insert are one write, so two asks for the same child can't both make one.
        _writer.Write("sessions.child", scope =>
        {
            if (scope.QueryFirst($"SELECT {Columns} FROM session_info WHERE parent_session_id = $parent AND acp_session_id = $session;",
                    ReadSession, new SqlArg("$parent", parent.SessionId), new SqlArg("$session", childSessionId)) is { } known)
                return known;
            var child = new SessionInfo(Guid.NewGuid().ToString(), parent.Agent, parent.RepoUrl, title, childSessionId, Now(),
                parent.Machine, ParentSessionId: parent.SessionId);
            scope.Execute("""
                INSERT INTO session_info(session_id, agent, repo_url, title, acp_session_id, created_at, updated_at, machine, parent_session_id)
                VALUES ($id, $agent, $repo, $title, $session, $now, $now, $machine, $parent);
                """, new SqlArg("$id", child.SessionId), new SqlArg("$agent", child.Agent), new SqlArg("$repo", child.RepoUrl), new SqlArg("$title", title),
                new SqlArg("$session", childSessionId), new SqlArg("$now", child.UpdatedAt), new SqlArg("$machine", child.Machine), new SqlArg("$parent", parent.SessionId));
            Publish(scope, child.SessionId);
            return child;
        });

    /// <summary>A helper session, which <paramref name="parent"/>'s agent started, on the parent's machine and repo.</summary>
    public SessionInfo Helper(SessionInfo parent, string agent, string title)
    {
        var helper = new SessionInfo(Guid.NewGuid().ToString(), agent, parent.RepoUrl, title, null, Now(), parent.Machine,
            ParentSessionId: parent.SessionId, Helper: true);
        Change(helper.SessionId, """
            INSERT INTO session_info(session_id, agent, repo_url, title, created_at, updated_at, machine, parent_session_id, helper)
            VALUES ($id, $agent, $repo, $title, $now, $now, $machine, $parent, 1);
            """, ("$id", helper.SessionId), ("$agent", agent), ("$repo", helper.RepoUrl), ("$title", title), ("$now", helper.UpdatedAt),
            ("$machine", helper.Machine), ("$parent", parent.SessionId));
        return helper;
    }

    /// <summary>The helper sessions <paramref name="parentSessionId"/>'s agent started, oldest first.</summary>
    public IReadOnlyList<SessionInfo> Helpers(string parentSessionId) =>
        Query($"SELECT {Columns} FROM session_info WHERE parent_session_id = $parent AND helper = 1 ORDER BY created_at;",
            ("$parent", parentSessionId));

    /// <summary>
    /// Records a message one session's agent sent another's, before it's delivered; with <paramref name="messageId"/>,
    /// one sent from another machine, under its id there.
    /// </summary>
    public HelperMessage AddMessage(string from, string to, string text, string? messageId = null)
    {
        var message = new HelperMessage(messageId ?? Guid.NewGuid().ToString(), from, to, text, null, Now());
        Execute("INSERT INTO helper_messages(message_id, from_session, to_session, text, created_at) VALUES ($id, $from, $to, $text, $now);",
            ("$id", message.MessageId), ("$from", from), ("$to", to), ("$text", text), ("$now", message.CreatedAt));
        return message;
    }

    /// <summary>The run the message went into, once it's delivered.</summary>
    public void SetMessageRun(string messageId, string runId) =>
        Execute("UPDATE helper_messages SET run_id = $run WHERE message_id = $id;", ("$id", messageId), ("$run", runId));

    /// <summary>The message couldn't be delivered, for the reason given.</summary>
    public void SetMessageFailed(string messageId, string error) =>
        Execute("UPDATE helper_messages SET error = $error WHERE message_id = $id;", ("$id", messageId), ("$error", error));

    /// <summary>The answer from the other machine a message to one of its sessions went to.</summary>
    public void SetMessageAnswer(string messageId, string state, string reply, string? error) =>
        Execute("UPDATE helper_messages SET state = $state, reply = $reply, error = COALESCE($error, error) WHERE message_id = $id;",
            ("$id", messageId), ("$state", state), ("$reply", reply), ("$error", error));

    public HelperMessage? Message(string messageId) =>
        Messages($"SELECT {MessageColumns} FROM helper_messages WHERE message_id = $id;", ("$id", messageId)).FirstOrDefault();

    /// <summary>Messages waiting to go to sessions that aren't on this machine, which their machine picks up, oldest first.</summary>
    public IReadOnlyList<HelperMessage> Outbox() =>
        Messages($"""
            SELECT {MessageColumns} FROM helper_messages
            WHERE run_id IS NULL AND error IS NULL AND state IS NULL AND to_session NOT IN (SELECT session_id FROM session_info)
            ORDER BY created_at;
            """);

    /// <summary>Messages from sessions on <paramref name="machine"/>, delivered here, whose answer hasn't gone back yet.</summary>
    public IReadOnlyList<HelperMessage> UnansweredFrom(string machine) =>
        Messages($"""
            SELECT {MessageColumns} FROM helper_messages
            WHERE state IS NULL AND from_session IN (SELECT session_id FROM session_info WHERE machine = $machine)
            ORDER BY created_at;
            """, ("$machine", machine));

    /// <summary>The helper sessions on <paramref name="machine"/>.</summary>
    public IReadOnlyList<SessionInfo> HelpersOn(string machine) =>
        Query($"SELECT {Columns} FROM session_info WHERE machine = $machine AND helper = 1;", ("$machine", machine));

    private List<HelperMessage> Messages(string sql, params (string Name, object? Value)[] parameters) =>
        _db.Query(sql, row =>
        {
            string? Text(int column) => row.IsNull(column) ? null : row.Text(column);
            return new HelperMessage(row.Text(0), row.Text(1), row.Text(2), row.Text(3), Text(4), row.Long(5), Text(6), Text(7), Text(8));
        }, CancellationToken.None, Args(parameters));

    /// <summary>How many messages <paramref name="from"/> has sent <paramref name="to"/>.</summary>
    public long MessageCount(string from, string to) =>
        _db.QueryFirst("SELECT COUNT(*) FROM helper_messages WHERE from_session = $from AND to_session = $to;", row => row.Long(0),
            CancellationToken.None, new SqlArg("$from", from), new SqlArg("$to", to));

    public SessionInfo? Get(string sessionId) =>
        Query($"SELECT {Columns} FROM session_info WHERE session_id = $id;", ("$id", sessionId)).FirstOrDefault();

    /// <summary>
    /// The <paramref name="limit"/> most recently used sessions after the cursor (<paramref name="before"/>, then
    /// <paramref name="beforeId"/> among sessions used at that time; no id means none of them); subagents' sessions are
    /// opened from their parent.
    /// </summary>
    /// <param name="settled">Only the settled sessions (true), only the open ones (false), or all (null).</param>
    public SessionPage List(long? before, int limit, CancellationToken cancel = default, string? beforeId = null, bool? settled = null)
    {
        var sessions = _db.Query(
            $"""
            SELECT {Columns} FROM session_info
            WHERE (updated_at < $before OR (updated_at = $before AND session_id < $id)) AND parent_session_id IS NULL
              AND ($settled IS NULL OR (settled_at IS NOT NULL) = $settled)
            ORDER BY updated_at DESC, session_id DESC LIMIT $limit;
            """,
            ReadSession, cancel, Args([("$before", before ?? long.MaxValue), ("$id", beforeId ?? ""), ("$limit", limit),
                ("$settled", settled is { } wanted ? (wanted ? 1 : 0) : null)]));
        return sessions.Count == limit ? new SessionPage(sessions, sessions[^1].UpdatedAt, sessions[^1].SessionId) : new SessionPage(sessions, null, null);
    }

    /// <summary>
    /// The rail's read (see <see cref="SessionsFeature.RailArgs"/>), in one statement: the totals, the providers, the
    /// parent page and the expanded parent's helper page, each a part of <see cref="Chat.Schema.Rail"/>'s rows.
    /// </summary>
    internal SessionsFeature.Rail Rail(SessionsFeature.RailArgs args, CancellationToken cancel = default)
    {
        string? Json(IReadOnlyList<string>? values) =>
            values is { Count: > 0 } ? JsonSerializer.Serialize(values, SessionsJson.Default.IReadOnlyListString) : null;
        var parts = _db.Query(Chat.Schema.Rail, row => (Part: row.Long(0), Row: ReadRail(row)), cancel,
            new SqlArg("$tab", args.Tab), new SqlArg("$providers", Json(args.Providers)), new SqlArg("$locations", Json(args.Locations)),
            new SqlArg("$filter", string.IsNullOrWhiteSpace(args.Filter) ? null : args.Filter.Trim()), new SqlArg("$limit", args.Limit),
            new SqlArg("$group", args.After?.Group), new SqlArg("$repo", args.After?.RepoUrl), new SqlArg("$updated", args.After?.UpdatedAt),
            new SqlArg("$id", args.After?.SessionId), new SqlArg("$expanded", args.Expanded),
            new SqlArg("$helperUpdated", args.HelpersAfter?.UpdatedAt), new SqlArg("$helperId", args.HelpersAfter?.SessionId),
            new SqlArg("$helperLimit", args.HelperLimit));
        var sessions = parts.Where(part => part.Part == 2).Select(part => (SessionsFeature.RailEntry)part.Row).ToList();
        var helpers = parts.Where(part => part.Part == 3).Select(part => (SessionsFeature.RailEntry)part.Row).ToList();
        return new SessionsFeature.Rail((SessionsFeature.RailTotals)parts[0].Row, [.. parts.Where(part => part.Part == 1).Select(part => (string)part.Row)],
            sessions, sessions.Count == args.Limit ? new(sessions[^1].Group, sessions[^1].RepoUrl, sessions[^1].UpdatedAt, sessions[^1].SessionId) : null,
            helpers, helpers.Count == args.HelperLimit ? new(helpers[^1].UpdatedAt, helpers[^1].SessionId) : null);
    }

    // One of Schema.Rail's rows: the totals (part 0), a provider (1), or a parent (2) or helper (3).
    private static object ReadRail(IRow row)
    {
        string? Text(int column) => row.IsNull(column) ? null : row.Text(column);
        long? Long(int column) => row.IsNull(column) ? null : row.Long(column);
        return row.Long(0) switch
        {
            0 => new SessionsFeature.RailTotals((int)row.Long(15), (int)row.Long(11), (int)row.Long(12)),
            1 => row.Text(3),
            _ => new SessionsFeature.RailEntry(row.Text(2), row.Text(3), row.Text(4), row.Text(5), row.Long(6) == 1, Text(7), Text(8),
                row.Long(9), Long(10), row.Long(11) == 1, row.Long(12) == 1, row.Long(13) == 1, row.Long(14) == 1, (int)row.Long(15),
                Text(16), Long(17), Text(18), Long(19), ProviderGlyph.Of(row.Text(3))),
        };
    }

    /// <summary>
    /// Sets the title and where it came from: <c>prompt</c> (the first message or the agent), <c>helper</c> (the utility
    /// model) or <c>user</c>. A helper title is kept over a later prompt one, and a user title over both; the check is in
    /// the write, so a title arriving meanwhile can't slip past it.
    /// </summary>
    public void SetTitle(string sessionId, string title, string source) =>
        Change(sessionId, $"UPDATE session_info SET title = $title, title_source = $source WHERE session_id = $id AND {Rank("$source")} >= {Rank("title_source")};",
            ("$id", sessionId), ("$title", title), ("$source", source));

    private static string Rank(string source) => $"(CASE {source} WHEN 'user' THEN 2 WHEN 'helper' THEN 1 ELSE 0 END)";

    public void SetAcpSession(string sessionId, string acpSessionId) =>
        Change(sessionId, "UPDATE session_info SET acp_session_id = $session WHERE session_id = $id;", ("$id", sessionId), ("$session", acpSessionId));

    /// <summary>Saves the session's reported total, kept as text so the decimal comes back exactly.</summary>
    public void SetSessionCost(string sessionId, decimal total) =>
        Execute("UPDATE session_info SET session_cost = $cost WHERE session_id = $id;",
            ("$id", sessionId), ("$cost", total.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>
    /// The config options the session's agent last reported for its ACP session (raw JSON), the values chosen for it, and
    /// the slash commands its agent last sent; with no list of its own, the last one that agent sent here.
    /// </summary>
    public SessionConfig Config(string sessionId) =>
        _db.QueryFirst("""
            SELECT c.options, c.choices, COALESCE(tc.commands, ac.commands)
            FROM (SELECT $id AS session_id) t
            LEFT JOIN session_config c ON c.session_id = t.session_id
            LEFT JOIN session_commands tc ON tc.session_id = t.session_id
            LEFT JOIN session_info i ON i.session_id = t.session_id
            LEFT JOIN agent_commands ac ON ac.agent = i.agent;
            """, row => new SessionConfig(
                row.IsNull(0) ? null : row.Text(0),
                row.IsNull(1) ? new Dictionary<string, string>() : ReadChoices(row.Text(1)),
                row.IsNull(2) ? null : row.Text(2)), CancellationToken.None, new SqlArg("$id", sessionId))!;

    /// <summary>Saves the slash commands the session's agent sent, as the session's and as that agent's latest.</summary>
    public void SetCommands(string sessionId, string commands) =>
        Execute("""
            INSERT INTO session_commands(session_id, commands) VALUES ($id, $commands)
            ON CONFLICT(session_id) DO UPDATE SET commands = $commands;
            INSERT INTO agent_commands(agent, commands) SELECT agent, $commands FROM session_info WHERE session_id = $id
            ON CONFLICT(agent) DO UPDATE SET commands = $commands;
            """, ("$id", sessionId), ("$commands", commands));

    public void SetConfigOptions(string sessionId, string options) =>
        Execute("""
            INSERT INTO session_config(session_id, options) VALUES ($id, $options)
            ON CONFLICT(session_id) DO UPDATE SET options = $options;
            """, ("$id", sessionId), ("$options", options));

    /// <summary>Chooses <paramref name="value"/> for the option: it's applied to every ACP session the session opens from now on.</summary>
    public void SetConfigChoice(string sessionId, string configId, string value) =>
        Execute("""
            INSERT INTO session_config(session_id, choices) VALUES ($id, json_object($config, $value))
            ON CONFLICT(session_id) DO UPDATE SET choices = json_set(choices, '$."' || $config || '"', $value);
            """, ("$id", sessionId), ("$config", configId.Replace("\"", "")), ("$value", value));

    /// <summary>The config options of the agent's latest new session: what a session that hasn't opened one yet starts with.</summary>
    public string? AgentConfig(string agent) =>
        _db.QueryFirst("SELECT options FROM agent_config WHERE agent = $agent;", row => row.Text(0), CancellationToken.None,
            new SqlArg("$agent", agent));

    public void SetAgentConfig(string agent, string options) =>
        Execute("""
            INSERT INTO agent_config(agent, options) VALUES ($agent, $options)
            ON CONFLICT(agent) DO UPDATE SET options = $options;
            """, ("$agent", agent), ("$options", options));

    /// <summary>Publishes the session's list item again, for a change to its summary. It goes through the writer, like
    /// every other change to the item, so the items go out in the order they were read.</summary>
    public void PublishSummary(string sessionId) =>
        _writer.Write("sessions.summary", scope =>
        {
            Publish(scope, sessionId);
            return 0;
        });

    /// <summary>The session's list item: its stored columns and its summary.</summary>
    public SessionItem Item(SessionInfo session) =>
        SessionItem.From(session, _summaries?.Get(session.SessionId) ?? SessionSummary.None, Root(session));

    /// <summary>
    /// The session's folder on this machine, made on its first send, so it may not exist yet; null for a session that runs
    /// on another machine, or has no folder.
    /// </summary>
    public string? Root(SessionInfo session) => session.Machine is null ? Folder(_checkouts, Top(session)) : null;

    /// <summary>
    /// The session at the top of <paramref name="session"/>'s parents, whose folder its children all work in: itself when it
    /// has no parent; the walk stops at a parent that's missing or already passed.
    /// </summary>
    public SessionInfo Top(SessionInfo session)
    {
        var passed = new HashSet<string>(StringComparer.Ordinal) { session.SessionId };
        while (session.ParentSessionId is { } parentId && passed.Add(parentId) && Get(parentId) is { } parent)
            session = parent;
        return session;
    }

    /// <summary>
    /// A session's folder under <paramref name="checkouts"/>: its own, or for a child (a subagent, or a helper) its
    /// parent's, which its agent works in. Callers pass the <see cref="Top"/> session, so a grandchild gets the top folder.
    /// </summary>
    /// Null when that id isn't one plain name: a parent id given as a path, or "..", would put the folder outside.
    public static string? Folder(string checkouts, SessionInfo session) =>
        (session.ParentSessionId ?? session.SessionId) is var id && id is not ("" or "." or "..") && Path.GetFileName(id) == id
            ? Path.Combine(checkouts, id)
            : null;

    /// <summary>
    /// Marks the session read up to <paramref name="seq"/>, or its last row if that's lower, publishing its item; a seq
    /// at or below what's read changes nothing.
    /// </summary>
    internal void MarkRead(IWriteScope scope, string sessionId, long seq)
    {
        // Never past the session's last row, so a turn that ends later is still unread.
        if (scope.Execute(
                """
                UPDATE session_info SET read_seq = mark.seq FROM (SELECT MIN($seq, MAX(seq)) AS seq FROM rows WHERE session_id = $id) mark
                WHERE session_id = $id AND read_seq < mark.seq;
                """,
                new SqlArg("$id", sessionId), new SqlArg("$seq", seq)) > 0)
            Publish(scope, sessionId);
    }

    /// <summary>Sets the user's mark without changing the session's place in the list; publishes only a changed mark.</summary>
    internal void Mark(IWriteScope scope, string sessionId, string state)
    {
        var change = state switch
        {
            "working" => "SET completed_at = NULL, settled_at = NULL WHERE session_id = $id AND (completed_at IS NOT NULL OR settled_at IS NOT NULL)",
            "done" => "SET completed_at = COALESCE(completed_at, $now), settled_at = NULL WHERE session_id = $id AND (completed_at IS NULL OR settled_at IS NOT NULL)",
            _ => "SET completed_at = COALESCE(completed_at, $now), settled_at = COALESCE(settled_at, $now) WHERE session_id = $id AND (completed_at IS NULL OR settled_at IS NULL)"
        };
        if (scope.Execute($"UPDATE session_info {change};", new SqlArg("$id", sessionId), new SqlArg("$now", Now())) > 0)
            Publish(scope, sessionId);
    }

    /// <summary>Marks the session as just used, which puts it at the top of the list.</summary>
    public void Touch(string sessionId) =>
        Change(sessionId, "UPDATE session_info SET updated_at = $now WHERE session_id = $id;", ("$id", sessionId), ("$now", Now()));

    private long Now() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private void Execute(string sql, params (string Name, object? Value)[] parameters) =>
        _writer.Write("sessions", scope => scope.Execute(sql, Args(parameters)));

    // A write to a session's list item, which publishes the item as it now is on the sessions topic.
    private void Change(string sessionId, string sql, params (string Name, object? Value)[] parameters) =>
        _writer.Write("sessions", scope =>
        {
            var changed = scope.Execute(sql, Args(parameters));
            Publish(scope, sessionId);
            return changed;
        });

    private void Publish(IWriteScope scope, string sessionId)
    {
        if (scope.QueryFirst($"SELECT {Columns} FROM session_info WHERE session_id = $id;", ReadSession, new SqlArg("$id", sessionId)) is { } session)
        {
            // The list holds top-level sessions only; a subagent's is opened from its parent.
            scope.Publish(SessionsFeature.Topic, sessionId, Item(session), SessionsJson.Default.SessionItem,
                keyedOnly: session.ParentSessionId is not null);
            // The rail draws helpers under their parent, so it hears every session's change.
            scope.Publish(SessionsFeature.RailTopic, sessionId, new SessionsFeature.RailChange(sessionId), SessionsJson.Default.RailChange);
        }
    }

    private List<SessionInfo> Query(string sql, params (string Name, object? Value)[] parameters) =>
        _db.Query(sql, ReadSession, CancellationToken.None, Args(parameters));

    private static SessionInfo ReadSession(IRow row) =>
        new(row.Text(0), row.Text(1), row.Text(2), row.IsNull(3) ? null : row.Text(3), row.IsNull(4) ? null : row.Text(4), row.Long(5),
            row.IsNull(6) ? null : row.Text(6),
            row.IsNull(7) ? null : decimal.Parse(row.Text(7), System.Globalization.CultureInfo.InvariantCulture),
            row.IsNull(8) ? null : row.Text(8), row.Long(9) == 1, row.Long(10),
            row.IsNull(11) ? null : row.Long(11), row.IsNull(12) ? null : row.Long(12), row.IsNull(13) ? null : row.Text(13));

    private static SqlArg[] Args((string Name, object? Value)[] parameters) =>
        Array.ConvertAll(parameters, parameter => new SqlArg(parameter.Name, parameter.Value));
}
