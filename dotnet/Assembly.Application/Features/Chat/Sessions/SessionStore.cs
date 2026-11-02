using System.Text.Json;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;

using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Sessions;
namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>
/// The sessions list, in the same SQLite file as the rows. Writes here are rare (create, title, send) and go through
/// the file's one writer, like the row commits.
/// </summary>
public sealed partial class SessionStore
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

    private const string Columns = "session_id, agent, repo_url, CAST(title AS BLOB), acp_session_id, updated_at, machine, session_cost, parent_session_id, helper, read_seq, completed_at, settled_at, title_source, working_folder";

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
    private readonly bool _hasPendingModel;

    private SessionStore(IAppDatabase db, IAtomicWriter writer, TimeProvider time, SessionSummaries? summaries, string checkouts, bool hasPendingModel)
    {
        _hasPendingModel = hasPendingModel;
        _summaries = summaries;
        _checkouts = checkouts;
        _db = db;
        _writer = writer;
        _time = time;
    }

    /// <summary><paramref name="checkouts"/> is the folder holding this machine's sessions' checkouts.</summary>
    public static SessionStore Open(IAppDatabase db, TimeProvider time, string checkouts, SessionSummaries? summaries = null)
    {
        var hasPendingModel = db.Writer.Write("sessions.schema", scope =>
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
            if (Missing("SELECT COUNT(*) FROM pragma_table_info('session_info') WHERE name='working_folder'"))
                scope.Execute("ALTER TABLE session_info ADD COLUMN working_folder TEXT NULL;");
            if (Missing("SELECT COUNT(*) FROM pragma_table_info('session_config') WHERE name='config_target'"))
                scope.Execute("ALTER TABLE session_config ADD COLUMN config_target TEXT NULL;");
            return !Missing("SELECT COUNT(*) FROM pragma_table_info('session_config') WHERE name='pending_model'");
        });
        return new SessionStore(db, db.Writer, time, summaries, checkouts, hasPendingModel);
    }

    /// <summary>A config's chosen values, from its JSON object; anything not a string is left out.</summary>
    internal static IReadOnlyDictionary<string, string> ReadChoices(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject()
            .Where(choice => choice.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(choice => choice.Name, choice => choice.Value.GetString()!);
    }

    public SessionInfo Create(string agent, string repoUrl, InitialSessionConfig? initialConfig = null, string? workingFolder = null, Action? validate = null)
    {
        // The bound cutover currently starts managed checkouts only. Refuse before any row/config publication.
        if (workingFolder is not null && _engine is not null)
            throw new CallFailed("This backend cannot create a session in the selected folder.", "incompatible_backend");
        if (initialConfig is not null && (string.IsNullOrEmpty(initialConfig.TargetId) || initialConfig.Choices is null ||
            initialConfig.Choices.Count == 0 || initialConfig.Choices.Any(choice => string.IsNullOrEmpty(choice.Key) || choice.Value is null)))
            throw new CallFailed("Select the agent settings again.");
        var choices = initialConfig is null ? null : JsonSerializer.Serialize(initialConfig.Choices, SessionsJson.Default.IReadOnlyDictionaryStringString);
        var session = new SessionInfo(Guid.NewGuid().ToString(), agent, repoUrl, null, null, Now(), WorkingFolder: workingFolder);
        CreateOwned(session, null, scope =>
        {
            if (initialConfig is not null)
                scope.Execute("INSERT INTO session_config(session_id,choices,config_target) VALUES($id,$choices,$target)",
                    new SqlArg("$id", session.SessionId), new SqlArg("$choices", choices), new SqlArg("$target", initialConfig.TargetId));
        }, validate, "INSERT INTO session_info(session_id, agent, repo_url, created_at, updated_at, working_folder) VALUES ($id, $agent, $repo, $now, $now, $folder);",
            ("$id", session.SessionId), ("$agent", agent), ("$repo", repoUrl), ("$now", session.UpdatedAt), ("$folder", workingFolder));
        return session;
    }

    /// <summary>Records a session another machine created and runs, under that machine's id for it.</summary>
    public SessionInfo Add(string sessionId, string agent, string repoUrl, string machine, string? parentSessionId = null, bool helper = false, string? workingFolder = null)
    {
        var session = new SessionInfo(sessionId, agent, repoUrl, null, null, Now(), machine, ParentSessionId: parentSessionId, Helper: helper, WorkingFolder: workingFolder);
        Change(sessionId, """
            INSERT INTO session_info(session_id, agent, repo_url, created_at, updated_at, machine, parent_session_id, helper, working_folder)
            VALUES ($id, $agent, $repo, $now, $now, $machine, $parent, $helper, $folder);
            """, ("$id", sessionId), ("$agent", agent), ("$repo", repoUrl), ("$now", session.UpdatedAt), ("$machine", machine),
            ("$parent", parentSessionId), ("$helper", helper ? 1 : 0), ("$folder", workingFolder));
        return session;
    }

    /// <summary>
    /// The session of the parent's subagent with that child session id, made the first time it's asked for. A child
    /// keeps its session for every delegation to it, so each one goes on in the same session.
    /// </summary>
    public SessionInfo Child(SessionInfo parent, string childSessionId, string title)
    {
        var exactTitle = title is null ? null : TitleUtf8.GetBytes(title);
        // The look-up and the insert are one write, so two asks for the same child can't both make one.
        return _writer.Write("sessions.child", scope =>
        {
            if (scope.QueryFirst($"SELECT {Columns} FROM session_info WHERE parent_session_id = $parent AND acp_session_id = $session;",
                    ReadSession, new SqlArg("$parent", parent.SessionId), new SqlArg("$session", childSessionId)) is { } known)
                return known;
            var child = new SessionInfo(Guid.NewGuid().ToString(), parent.Agent, parent.RepoUrl, title, childSessionId, Now(),
                parent.Machine, ParentSessionId: parent.SessionId);
            scope.Execute("""
                INSERT INTO session_info(session_id, agent, repo_url, title, acp_session_id, created_at, updated_at, machine, parent_session_id)
                VALUES ($id, $agent, $repo, CAST($title AS TEXT), $session, $now, $now, $machine, $parent);
                """, new SqlArg("$id", child.SessionId), new SqlArg("$agent", child.Agent), new SqlArg("$repo", child.RepoUrl), new SqlArg("$title", exactTitle),
                new SqlArg("$session", childSessionId), new SqlArg("$now", child.UpdatedAt), new SqlArg("$machine", child.Machine), new SqlArg("$parent", parent.SessionId));
            Publish(scope, child.SessionId);
            return child;
        });
    }

    /// <summary>A helper session, which <paramref name="parent"/>'s agent started, on the parent's machine and repo.</summary>
    public SessionInfo Helper(SessionInfo parent, string agent, string title, string? initiatingSessionId = null)
    {
        var helper = new SessionInfo(Guid.NewGuid().ToString(), agent, parent.RepoUrl, title, null, Now(), parent.Machine,
            ParentSessionId: parent.SessionId, Helper: true);
        CreateOwned(helper, initiatingSessionId, """
            INSERT INTO session_info(session_id, agent, repo_url, title, created_at, updated_at, machine, parent_session_id, helper)
            VALUES ($id, $agent, $repo, CAST($title AS TEXT), $now, $now, $machine, $parent, 1);
            """, ("$id", helper.SessionId), ("$agent", agent), ("$repo", helper.RepoUrl), ("$title", title is null ? null : TitleUtf8.GetBytes(title)), ("$now", helper.UpdatedAt),
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
            WHERE (updated_at < $before OR (updated_at = $before AND session_id < $id)) AND (parent_session_id IS NULL OR helper = 1)
              AND ($settled IS NULL OR (settled_at IS NOT NULL) = $settled)
            ORDER BY updated_at DESC, session_id DESC LIMIT $limit;
            """,
            ReadSession, cancel, Args([("$before", before ?? long.MaxValue), ("$id", beforeId ?? ""), ("$limit", limit),
                ("$settled", settled is { } wanted ? (wanted ? 1 : 0) : null)]));
        return sessions.Count == limit ? new SessionPage(sessions, sessions[^1].UpdatedAt, sessions[^1].SessionId) : new SessionPage(sessions, null, null);
    }

    /// <summary>
    /// The rail's read (see <see cref="SessionsFeature.RailArgs"/>), in one statement: the totals, the providers, the
    /// session page, each a part of <see cref="Chat.Schema.Rail"/>'s rows.
    /// </summary>
    internal SessionsFeature.Rail Rail(SessionsFeature.RailArgs args, CancellationToken cancel = default)
    {
        string? Json(IReadOnlyList<string>? values) =>
            values is { Count: > 0 } ? JsonSerializer.Serialize(values, SessionsJson.Default.IReadOnlyListString) : null;
        var groupBy = args.GroupBy ?? new SessionsFeature.RailGroupBy(false, true);
        var parts = _db.Query(Chat.Schema.Rail, row => (Part: row.Long(0), N: (int)row.Long(1), Row: ReadRail(row)), cancel,
            new SqlArg("$providers", Json(args.Providers)), new SqlArg("$locations", Json(args.Locations)),
            new SqlArg("$statuses", Json(args.Statuses)), new SqlArg("$status_group", groupBy.Status ? 1 : 0),
            new SqlArg("$project_group", groupBy.Project ? 1 : 0), new SqlArg("$sort", args.Sort), new SqlArg("$dir", args.Dir),
            new SqlArg("$folded", Json(args.Folded)), new SqlArg("$filter", string.IsNullOrWhiteSpace(args.Filter) ? null : args.Filter.Trim()),
            new SqlArg("$limit", args.Limit), new SqlArg("$after_section", args.After?.Section),
            new SqlArg("$after_group", args.After?.Group), new SqlArg("$after_repo", args.After?.RepoUrl),
            new SqlArg("$after_header", args.After?.Header), new SqlArg("$after_ordering", args.After?.Ordering),
            new SqlArg("$anchor", args.Anchor), new SqlArg("$above", args.Above));
        // The entries before each page, from its first row's place in the whole list.
        int Start(long part) => parts.FirstOrDefault(row => row.Part == part) is { Row: not null } first ? first.N - 1 : 0;
        var sessions = parts.Where(part => part.Part == 2).Select(part => (SessionsFeature.RailEntry)part.Row).ToList();
        SessionsFeature.RailCursor Cursor(SessionsFeature.RailEntry entry) => new(
            groupBy.Status ? entry.SectionIndex : 0, groupBy.Project ? entry.Group : "", groupBy.Project ? entry.RepoUrl : "",
            entry.Header, entry.Ordering);
        return new SessionsFeature.Rail((SessionsFeature.RailTotals)parts[0].Row, [.. parts.Where(part => part.Part == 1).Select(part => (string)part.Row)],
            sessions, sessions.Count == args.Limit ? Cursor(sessions[^1]) : null, Start(2));
    }

    // One of Schema.Rail's rows: the totals (part 0), a provider (1), or a session (2).
    private static object ReadRail(IRow row)
    {
        string? Text(int column) => row.IsNull(column) ? null : row.Text(column);
        long? Long(int column) => row.IsNull(column) ? null : row.Long(column);
        return row.Long(0) switch
        {
            0 => new SessionsFeature.RailTotals((int)row.Long(8)),
            1 => row.Text(5),
            _ => new SessionsFeature.RailEntry(row.Text(2), row.Text(3), row.Text(4), row.Text(5), row.Text(6), row.Text(7),
                (int)row.Long(8), Text(9)?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [], row.IsNull(10) ? null : TitleUtf8.GetString(row.Blob(10)), Text(11), row.Long(12),
                Long(13), row.Long(14) == 1, row.Long(15) == 1, row.Long(16) == 1, row.Long(17) == 1,
                Text(18), Long(19), Text(20), Long(21), ProviderGlyph.Of(row.Text(5)), (int)row.Long(22), (int)row.Long(23),
                row.Text(24), Text(25)),
        };
    }

    /// <summary>
    /// Sets the title and where it came from: <c>prompt</c> (the first message or the agent), <c>helper</c> (the utility
    /// model) or <c>user</c>. A helper title is kept over a later prompt one, and a user title over both; the check is in
    /// the write, so a title arriving meanwhile can't slip past it.
    /// </summary>
    public void SetTitle(string sessionId, string title, string source)
    {
        var titleUtf8 = title is null ? null : TitleUtf8.GetBytes(title);
        _writer.Write("sessions", scope =>
        {
            var before = Get(scope, sessionId)?.Title;
            scope.Execute($"UPDATE session_info SET title = CAST($title AS TEXT), title_source = $source WHERE session_id = $id AND {Rank("$source")} >= {Rank("title_source")};",
                new SqlArg("$id", sessionId), new SqlArg("$title", titleUtf8), new SqlArg("$source", source));
            var actual = Get(scope, sessionId)?.Title;
            if (actual is not null && actual != before) _engine?.Titled(scope, sessionId, actual, TitleUtf8.GetBytes(actual));
            Publish(scope, sessionId);
            return 0;
        });
    }

    private static readonly System.Text.UTF8Encoding TitleUtf8 = new(false, true);

    private static string Rank(string source) => $"(CASE {source} WHEN 'user' THEN 2 WHEN 'helper' THEN 1 ELSE 0 END)";

    public void SetAcpSession(string sessionId, string acpSessionId) =>
        Change(sessionId, "UPDATE session_info SET acp_session_id = $session WHERE session_id = $id;", ("$id", sessionId), ("$session", acpSessionId));

    /// <summary>Saves the session's reported total, kept as text so the decimal comes back exactly.</summary>
    public void SetSessionCost(string sessionId, decimal total) =>
        Execute("UPDATE session_info SET session_cost = $cost WHERE session_id = $id;",
            ("$id", sessionId), ("$cost", total.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>Publishes the session's list item again, for a change to its summary. It goes through the writer, like
    /// every other change to the item, so the items go out in the order they were read.</summary>
    public void PublishSummary(string sessionId) =>
        _writer.Write("sessions.summary", scope =>
        {
            Publish(scope, sessionId);
            return 0;
        });

    /// <summary>Applies a remote push and publishes the desktop's item after the metadata write commits.</summary>
    public void ApplyRemote(SessionItem item)
    {
        var exactTitle = item.Title is null ? null : TitleUtf8.GetBytes(item.Title);
        _writer.Write("sessions.remote", scope =>
        {
            scope.Execute($"""
                UPDATE session_info SET
                  title = CASE WHEN COALESCE(title_source, 'prompt') != 'user' AND helper = 0 AND $title IS NOT NULL
                    AND {Rank("$source")} >= {Rank("title_source")} THEN CAST($title AS TEXT) ELSE title END,
                  title_source = CASE WHEN COALESCE(title_source, 'prompt') != 'user' AND helper = 0 AND $title IS NOT NULL
                    AND {Rank("$source")} >= {Rank("title_source")} THEN $source ELSE title_source END,
                  updated_at = $at
                WHERE session_id = $id;
                """, new SqlArg("$id", item.SessionId), new SqlArg("$title", exactTitle),
                new SqlArg("$source", item.TitleSource ?? "prompt"), new SqlArg("$at", item.UpdatedAt));
            _summaries?.ApplyRemote(item);
            Publish(scope, item.SessionId);
            return 0;
        });
    }

    /// <summary>The session's list item: its stored columns and its summary.</summary>
    public SessionItem Item(SessionInfo session) =>
        SessionItem.From(session, _summaries?.Get(session.SessionId) ?? SessionSummary.None, Root(session));

    public SessionItem Item(IReadScope scope, SessionInfo session) =>
        SessionItem.From(session, _summaries?.Get(session.SessionId) ?? SessionSummary.None, Root(scope, session));

    public string? Root(SessionInfo session) => Location(session)?.Folder;
    public string? Root(IReadScope scope, SessionInfo session) => Location(scope, session)?.Folder;

    public SessionLocation? Location(SessionInfo session)
    {
        using var scope = _db.OpenSnapshot(CancellationToken.None);
        return Location(scope, session);
    }

    public SessionLocation? Location(IReadScope scope, SessionInfo session)
    {
        if (!string.IsNullOrEmpty(session.Machine)) return null;
        var current = session;
        var passed = new HashSet<string>(StringComparer.Ordinal);
        while (passed.Add(current.SessionId))
        {
            if (_engine?.Location(scope, current.SessionId) is { } location) return location;
            if (current.ParentSessionId is not { } parent || Get(scope, parent) is not { } ancestor) break;
            current = ancestor;
        }
        var top = Top(scope, session);
        if (top.WorkingFolder is { } direct) return new(direct, false);
        return Folder(_checkouts, top) is { } folder ? new(folder, true) : null;
    }

    public string? ManagedFolder(SessionInfo session) => Folder(_checkouts, Top(session));

    public SessionInfo Top(SessionInfo session)
    {
        using var scope = _db.OpenSnapshot(CancellationToken.None);
        return Top(scope, session);
    }

    /// <summary>Resolves the owning ancestor using only the supplied transaction.</summary>
    public SessionInfo Top(IReadScope scope, SessionInfo session)
    {
        var passed = new HashSet<string>(StringComparer.Ordinal) { session.SessionId };
        while (session.ParentSessionId is { } parentId && passed.Add(parentId) && Get(scope, parentId) is { } parent)
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
        if (Get(scope, sessionId) is { } session)
            Publish(scope, session, Item(scope, session));
    }

    private static void Publish(IWriteScope scope, SessionInfo session, SessionItem item)
    {
        // Native subagents remain keyed-only; the rail hears every session's change.
        scope.Publish(SessionsFeature.Topic, session.SessionId, item, SessionsJson.Default.SessionItem,
            keyedOnly: session.ParentSessionId is not null && !session.Helper);
        scope.Publish(SessionsFeature.RailTopic, session.SessionId, new SessionsFeature.RailChange(session.SessionId), SessionsJson.Default.RailChange);
    }

    private List<SessionInfo> Query(string sql, params (string Name, object? Value)[] parameters) =>
        _db.Query(sql, ReadSession, CancellationToken.None, Args(parameters));

    private static SessionInfo ReadSession(IRow row) =>
        new(row.Text(0), row.Text(1), row.Text(2), row.IsNull(3) ? null : TitleUtf8.GetString(row.Blob(3)), row.IsNull(4) ? null : row.Text(4), row.Long(5),
            row.IsNull(6) ? null : row.Text(6),
            row.IsNull(7) ? null : decimal.Parse(row.Text(7), System.Globalization.CultureInfo.InvariantCulture),
            row.IsNull(8) ? null : row.Text(8), row.Long(9) == 1, row.Long(10),
            row.IsNull(11) ? null : row.Long(11), row.IsNull(12) ? null : row.Long(12), row.IsNull(13) ? null : row.Text(13), row.IsNull(14) ? null : row.Text(14));

    private static SqlArg[] Args((string Name, object? Value)[] parameters) =>
        Array.ConvertAll(parameters, parameter => new SqlArg(parameter.Name, parameter.Value));
}
