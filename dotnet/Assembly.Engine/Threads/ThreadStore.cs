using Microsoft.Data.Sqlite;

namespace Assembly.Engine.Threads;

/// <summary>
/// The sessions list, in the same SQLite file as the rows but on its own connection. Writes here are rare
/// (create, title, send), so they share the file without slowing the row commits.
/// </summary>
public sealed class ThreadStore : IDisposable
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS thread_info(thread_id TEXT PRIMARY KEY, agent TEXT NOT NULL, repo_url TEXT NOT NULL,
          title TEXT, acp_session_id TEXT, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, machine TEXT,
          session_cost TEXT);
        CREATE INDEX IF NOT EXISTS thread_info_updated ON thread_info(updated_at);
        CREATE TABLE IF NOT EXISTS thread_config(thread_id TEXT PRIMARY KEY, options TEXT, choices TEXT NOT NULL DEFAULT '{}');
        CREATE TABLE IF NOT EXISTS agent_config(agent TEXT PRIMARY KEY, options TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS thread_commands(thread_id TEXT PRIMARY KEY, commands TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS agent_commands(agent TEXT PRIMARY KEY, commands TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS helper_messages(message_id TEXT PRIMARY KEY, from_thread TEXT NOT NULL, to_thread TEXT NOT NULL,
          text TEXT NOT NULL, run_id TEXT, created_at INTEGER NOT NULL, error TEXT, state TEXT, reply TEXT);
        CREATE INDEX IF NOT EXISTS helper_messages_pair ON helper_messages(from_thread, to_thread);
        """;

    private const string Columns = "thread_id, agent, repo_url, title, acp_session_id, updated_at, machine, session_cost, parent_thread_id, helper";

    // A database made before threads had a machine gets the column; its threads all ran where it lives.
    private const string HasMachine = "SELECT COUNT(*) FROM pragma_table_info('thread_info') WHERE name = 'machine';";

    // And one made before sessions were closed after each turn gets the saved cost, empty.
    private const string HasSessionCost = "SELECT COUNT(*) FROM pragma_table_info('thread_info') WHERE name = 'session_cost';";

    // And one made before subagents gets the parent link, empty: none of its threads is a child.
    private const string HasParent = "SELECT COUNT(*) FROM pragma_table_info('thread_info') WHERE name = 'parent_thread_id';";

    // And one made before helper sessions marks none of its threads a helper.
    private const string HasHelper = "SELECT COUNT(*) FROM pragma_table_info('thread_info') WHERE name = 'helper';";

    // And one made before a message could fail gets the error, empty.
    private const string HasMessageError = "SELECT COUNT(*) FROM pragma_table_info('helper_messages') WHERE name = 'error';";

    // And one made before messages crossed machines gets their answer, empty.
    private const string HasMessageAnswer = "SELECT COUNT(*) FROM pragma_table_info('helper_messages') WHERE name = 'state';";

    private const string MessageColumns = "message_id, from_thread, to_thread, text, run_id, created_at, error, state, reply";

    private readonly SqliteConnection _db;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    private ThreadStore(SqliteConnection db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public static ThreadStore Open(string path, TimeProvider time)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        var store = new ThreadStore(db, time);
        store.Execute("PRAGMA journal_mode = WAL;");
        store.Execute(Schema);
        if (store.Scalar(HasMachine) == 0)
            store.Execute("ALTER TABLE thread_info ADD COLUMN machine TEXT;");
        if (store.Scalar(HasSessionCost) == 0)
            store.Execute("ALTER TABLE thread_info ADD COLUMN session_cost TEXT;");
        if (store.Scalar(HasParent) == 0)
            store.Execute("ALTER TABLE thread_info ADD COLUMN parent_thread_id TEXT;");
        if (store.Scalar(HasHelper) == 0)
            store.Execute("ALTER TABLE thread_info ADD COLUMN helper INTEGER NOT NULL DEFAULT 0;");
        if (store.Scalar(HasMessageError) == 0)
            store.Execute("ALTER TABLE helper_messages ADD COLUMN error TEXT;");
        if (store.Scalar(HasMessageAnswer) == 0)
            store.Execute("ALTER TABLE helper_messages ADD COLUMN state TEXT; ALTER TABLE helper_messages ADD COLUMN reply TEXT;");
        return store;
    }

    public ThreadInfo Create(string agent, string repoUrl)
    {
        var thread = new ThreadInfo(Guid.NewGuid().ToString(), agent, repoUrl, null, null, Now());
        Execute(
            "INSERT INTO thread_info(thread_id, agent, repo_url, created_at, updated_at) VALUES ($id, $agent, $repo, $now, $now);",
            ("$id", thread.ThreadId), ("$agent", agent), ("$repo", repoUrl), ("$now", thread.UpdatedAt));
        return thread;
    }

    /// <summary>Records a thread another machine created and runs, under that machine's id for it.</summary>
    public ThreadInfo Add(string threadId, string agent, string repoUrl, string machine, string? parentThreadId = null, bool helper = false)
    {
        var thread = new ThreadInfo(threadId, agent, repoUrl, null, null, Now(), machine, ParentThreadId: parentThreadId, Helper: helper);
        Execute("""
            INSERT INTO thread_info(thread_id, agent, repo_url, created_at, updated_at, machine, parent_thread_id, helper)
            VALUES ($id, $agent, $repo, $now, $now, $machine, $parent, $helper);
            """, ("$id", threadId), ("$agent", agent), ("$repo", repoUrl), ("$now", thread.UpdatedAt), ("$machine", machine),
            ("$parent", parentThreadId), ("$helper", helper ? 1 : 0));
        return thread;
    }

    /// <summary>
    /// The thread of the parent's subagent with that child session id, made the first time it's asked for. A child
    /// keeps its session for every delegation to it, so each one goes on in the same thread.
    /// </summary>
    public ThreadInfo Child(ThreadInfo parent, string childSessionId, string title)
    {
        if (Query($"SELECT {Columns} FROM thread_info WHERE parent_thread_id = $parent AND acp_session_id = $session;",
                ("$parent", parent.ThreadId), ("$session", childSessionId)).FirstOrDefault() is { } known)
            return known;
        var child = new ThreadInfo(Guid.NewGuid().ToString(), parent.Agent, parent.RepoUrl, title, childSessionId, Now(),
            parent.Machine, ParentThreadId: parent.ThreadId);
        Execute("""
            INSERT INTO thread_info(thread_id, agent, repo_url, title, acp_session_id, created_at, updated_at, machine, parent_thread_id)
            VALUES ($id, $agent, $repo, $title, $session, $now, $now, $machine, $parent);
            """, ("$id", child.ThreadId), ("$agent", child.Agent), ("$repo", child.RepoUrl), ("$title", title),
            ("$session", childSessionId), ("$now", child.UpdatedAt), ("$machine", child.Machine), ("$parent", parent.ThreadId));
        return child;
    }

    /// <summary>A helper session's thread, which <paramref name="parent"/>'s agent started, on the parent's machine and repo.</summary>
    public ThreadInfo Helper(ThreadInfo parent, string agent, string title)
    {
        var helper = new ThreadInfo(Guid.NewGuid().ToString(), agent, parent.RepoUrl, title, null, Now(), parent.Machine,
            ParentThreadId: parent.ThreadId, Helper: true);
        Execute("""
            INSERT INTO thread_info(thread_id, agent, repo_url, title, created_at, updated_at, machine, parent_thread_id, helper)
            VALUES ($id, $agent, $repo, $title, $now, $now, $machine, $parent, 1);
            """, ("$id", helper.ThreadId), ("$agent", agent), ("$repo", helper.RepoUrl), ("$title", title), ("$now", helper.UpdatedAt),
            ("$machine", helper.Machine), ("$parent", parent.ThreadId));
        return helper;
    }

    /// <summary>The helper sessions <paramref name="parentThreadId"/>'s agent started, oldest first.</summary>
    public IReadOnlyList<ThreadInfo> Helpers(string parentThreadId) =>
        Query($"SELECT {Columns} FROM thread_info WHERE parent_thread_id = $parent AND helper = 1 ORDER BY created_at;",
            ("$parent", parentThreadId));

    /// <summary>
    /// Records a message one thread's agent sent another's, before it's delivered; with <paramref name="messageId"/>,
    /// one sent from another machine, under its id there.
    /// </summary>
    public HelperMessage AddMessage(string from, string to, string text, string? messageId = null)
    {
        var message = new HelperMessage(messageId ?? Guid.NewGuid().ToString(), from, to, text, null, Now());
        Execute("INSERT INTO helper_messages(message_id, from_thread, to_thread, text, created_at) VALUES ($id, $from, $to, $text, $now);",
            ("$id", message.MessageId), ("$from", from), ("$to", to), ("$text", text), ("$now", message.CreatedAt));
        return message;
    }

    /// <summary>The run the message went into, once it's delivered.</summary>
    public void SetMessageRun(string messageId, string runId) =>
        Execute("UPDATE helper_messages SET run_id = $run WHERE message_id = $id;", ("$id", messageId), ("$run", runId));

    /// <summary>The message couldn't be delivered, for the reason given.</summary>
    public void SetMessageFailed(string messageId, string error) =>
        Execute("UPDATE helper_messages SET error = $error WHERE message_id = $id;", ("$id", messageId), ("$error", error));

    /// <summary>The answer from the other machine a message to one of its threads went to.</summary>
    public void SetMessageAnswer(string messageId, string state, string reply, string? error) =>
        Execute("UPDATE helper_messages SET state = $state, reply = $reply, error = COALESCE($error, error) WHERE message_id = $id;",
            ("$id", messageId), ("$state", state), ("$reply", reply), ("$error", error));

    public HelperMessage? Message(string messageId) =>
        Messages($"SELECT {MessageColumns} FROM helper_messages WHERE message_id = $id;", ("$id", messageId)).FirstOrDefault();

    /// <summary>Messages waiting to go to threads that aren't on this machine, which their machine picks up, oldest first.</summary>
    public IReadOnlyList<HelperMessage> Outbox() =>
        Messages($"""
            SELECT {MessageColumns} FROM helper_messages
            WHERE run_id IS NULL AND error IS NULL AND state IS NULL AND to_thread NOT IN (SELECT thread_id FROM thread_info)
            ORDER BY created_at;
            """);

    /// <summary>Messages from threads on <paramref name="machine"/>, delivered here, whose answer hasn't gone back yet.</summary>
    public IReadOnlyList<HelperMessage> UnansweredFrom(string machine) =>
        Messages($"""
            SELECT {MessageColumns} FROM helper_messages
            WHERE state IS NULL AND from_thread IN (SELECT thread_id FROM thread_info WHERE machine = $machine)
            ORDER BY created_at;
            """, ("$machine", machine));

    /// <summary>The helper sessions on <paramref name="machine"/>.</summary>
    public IReadOnlyList<ThreadInfo> HelpersOn(string machine) =>
        Query($"SELECT {Columns} FROM thread_info WHERE machine = $machine AND helper = 1;", ("$machine", machine));

    private List<HelperMessage> Messages(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (_gate)
        {
            using var command = Command(sql, parameters);
            using var reader = command.ExecuteReader();
            var messages = new List<HelperMessage>();
            string? Text(int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
            while (reader.Read())
                messages.Add(new HelperMessage(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    Text(4), reader.GetInt64(5), Text(6), Text(7), Text(8)));
            return messages;
        }
    }

    /// <summary>How many messages <paramref name="from"/> has sent <paramref name="to"/>.</summary>
    public long MessageCount(string from, string to)
    {
        lock (_gate)
        {
            using var command = Command("SELECT COUNT(*) FROM helper_messages WHERE from_thread = $from AND to_thread = $to;",
                [("$from", from), ("$to", to)]);
            return (long)command.ExecuteScalar()!;
        }
    }

    public ThreadInfo? Get(string threadId) =>
        Query($"SELECT {Columns} FROM thread_info WHERE thread_id = $id;", ("$id", threadId)).FirstOrDefault();

    /// <summary>The <paramref name="limit"/> most recently used threads below <paramref name="before"/>; subagents' threads are opened from their parent.</summary>
    public ThreadPage List(long? before, int limit)
    {
        var threads = Query(
            $"SELECT {Columns} FROM thread_info WHERE updated_at < $before AND parent_thread_id IS NULL ORDER BY updated_at DESC LIMIT $limit;",
            ("$before", before ?? long.MaxValue), ("$limit", limit));
        return new ThreadPage(threads, threads.Count == limit ? threads[^1].UpdatedAt : null);
    }

    public void SetTitle(string threadId, string title) =>
        Execute("UPDATE thread_info SET title = $title WHERE thread_id = $id;", ("$id", threadId), ("$title", title));

    public void SetAcpSession(string threadId, string acpSessionId) =>
        Execute("UPDATE thread_info SET acp_session_id = $session WHERE thread_id = $id;", ("$id", threadId), ("$session", acpSessionId));

    /// <summary>Saves the session's reported total, kept as text so the decimal comes back exactly.</summary>
    public void SetSessionCost(string threadId, decimal total) =>
        Execute("UPDATE thread_info SET session_cost = $cost WHERE thread_id = $id;",
            ("$id", threadId), ("$cost", total.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>
    /// The config options the thread's agent last reported for its session (raw JSON), the values chosen for it, and
    /// the slash commands its agent last sent; with no list of its own, the last one that agent sent here.
    /// </summary>
    public ThreadConfig Config(string threadId)
    {
        lock (_gate)
        {
            using var command = Command("""
                SELECT c.options, c.choices, COALESCE(tc.commands, ac.commands)
                FROM (SELECT $id AS thread_id) t
                LEFT JOIN thread_config c ON c.thread_id = t.thread_id
                LEFT JOIN thread_commands tc ON tc.thread_id = t.thread_id
                LEFT JOIN thread_info i ON i.thread_id = t.thread_id
                LEFT JOIN agent_commands ac ON ac.agent = i.agent;
                """, [("$id", threadId)]);
            using var reader = command.ExecuteReader();
            reader.Read();
            return new ThreadConfig(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? new Dictionary<string, string>() : ThreadConfig.ReadChoices(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2));
        }
    }

    /// <summary>Saves the slash commands the thread's agent sent, as the thread's and as that agent's latest.</summary>
    public void SetCommands(string threadId, string commands) =>
        Execute("""
            INSERT INTO thread_commands(thread_id, commands) VALUES ($id, $commands)
            ON CONFLICT(thread_id) DO UPDATE SET commands = $commands;
            INSERT INTO agent_commands(agent, commands) SELECT agent, $commands FROM thread_info WHERE thread_id = $id
            ON CONFLICT(agent) DO UPDATE SET commands = $commands;
            """, ("$id", threadId), ("$commands", commands));

    public void SetConfigOptions(string threadId, string options) =>
        Execute("""
            INSERT INTO thread_config(thread_id, options) VALUES ($id, $options)
            ON CONFLICT(thread_id) DO UPDATE SET options = $options;
            """, ("$id", threadId), ("$options", options));

    /// <summary>Chooses <paramref name="value"/> for the option: it's applied to every session the thread opens from now on.</summary>
    public void SetConfigChoice(string threadId, string configId, string value) =>
        Execute("""
            INSERT INTO thread_config(thread_id, choices) VALUES ($id, json_object($config, $value))
            ON CONFLICT(thread_id) DO UPDATE SET choices = json_set(choices, '$."' || $config || '"', $value);
            """, ("$id", threadId), ("$config", configId.Replace("\"", "")), ("$value", value));

    /// <summary>The config options of the agent's latest new session: what a thread that hasn't opened one yet starts with.</summary>
    public string? AgentConfig(string agent)
    {
        lock (_gate)
        {
            using var command = Command("SELECT options FROM agent_config WHERE agent = $agent;", [("$agent", agent)]);
            return command.ExecuteScalar() as string;
        }
    }

    public void SetAgentConfig(string agent, string options) =>
        Execute("""
            INSERT INTO agent_config(agent, options) VALUES ($agent, $options)
            ON CONFLICT(agent) DO UPDATE SET options = $options;
            """, ("$agent", agent), ("$options", options));

    /// <summary>Marks the thread as just used, which puts it at the top of the list.</summary>
    public void Touch(string threadId) =>
        Execute("UPDATE thread_info SET updated_at = $now WHERE thread_id = $id;", ("$id", threadId), ("$now", Now()));

    public void Dispose()
    {
        lock (_gate)
            _db.Dispose();
    }

    private long Now() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (_gate)
        {
            using var command = Command(sql, parameters);
            command.ExecuteNonQuery();
        }
    }

    private long Scalar(string sql)
    {
        lock (_gate)
        {
            using var command = Command(sql, []);
            return (long)command.ExecuteScalar()!;
        }
    }

    private List<ThreadInfo> Query(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (_gate)
        {
            using var command = Command(sql, parameters);
            using var reader = command.ExecuteReader();
            var threads = new List<ThreadInfo>();
            while (reader.Read())
                threads.Add(new ThreadInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : decimal.Parse(reader.GetString(7), System.Globalization.CultureInfo.InvariantCulture),
                    reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetInt64(9) == 1));
            return threads;
        }
    }

    private SqliteCommand Command(string sql, (string Name, object? Value)[] parameters)
    {
        var command = _db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}
