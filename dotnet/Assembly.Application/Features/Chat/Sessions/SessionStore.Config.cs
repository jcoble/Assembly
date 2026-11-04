using Assembly.Application.Common.Engine;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Acp;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Features.Chat.Sessions;

public sealed partial class SessionStore
{
    private bool HasPendingModel => _engine is not null || _hasPendingModel;

    /// <summary>The last reported options, chosen values and session/agent commands.</summary>
    public SessionConfig Config(string sessionId)
    {
        using var scope = _db.OpenSnapshot(CancellationToken.None);
        return Config(scope, sessionId);
    }

    public SessionConfig Config(IReadScope scope, string sessionId) => ReadConfig(scope, sessionId, HasPendingModel);

    // Only the initialization transaction, after EnsureEngineProjectionSchema, knows its uncommitted schema.
    internal SessionConfig ConfigForEngineInitialization(IReadScope scope, string sessionId) => ReadConfig(scope, sessionId, true);

    private static SessionConfig ReadConfig(IReadScope scope, string sessionId, bool hasPendingModel) =>
        scope.QueryFirst($"""
            SELECT c.options, c.choices, COALESCE(tc.commands, ac.commands), {(hasPendingModel ? "c.pending_model" : "NULL")}, c.config_target
            FROM (SELECT $id AS session_id) t
            LEFT JOIN session_config c ON c.session_id = t.session_id
            LEFT JOIN session_commands tc ON tc.session_id = t.session_id
            LEFT JOIN session_info i ON i.session_id = t.session_id
            LEFT JOIN agent_commands ac ON ac.agent = i.agent;
            """, row => new SessionConfig(
                row.IsNull(0) ? null : row.Text(0),
                row.IsNull(1) ? new Dictionary<string, string>() : ReadChoices(row.Text(1)),
                row.IsNull(2) ? null : row.Text(2), row.IsNull(3) ? null : row.Text(3), row.IsNull(4) ? null : row.Text(4)), new SqlArg("$id", sessionId))!;

    public void SetConfigOptions(string sessionId, string options) =>
        _writer.Write("sessions.config", scope =>
        {
            SaveOptions(scope, sessionId, options);
            ObserveModel(scope, sessionId, options);
            return 0;
        });

    private static void SaveOptions(IWriteScope scope, string sessionId, string options) =>
        scope.Execute("""
            INSERT INTO session_config(session_id, options) VALUES ($id, $options)
            ON CONFLICT(session_id) DO UPDATE SET options = $options;
            """, new SqlArg("$id", sessionId), new SqlArg("$options", options));

    public void ConfirmModelSelection(string sessionId, string optionId, string selectedValue, string optionsJson, Action? validate = null) =>
        _writer.Write("sessions.config", scope => { validate?.Invoke(); ConfirmModelSelection(scope, sessionId, optionId, selectedValue, optionsJson); return 0; });

    /// <summary>Persists one confirmed adapter result and consumes intent in the same transaction.</summary>
    public void ConfirmModelSelection(IWriteScope scope, string sessionId, string optionId, string selectedValue, string optionsJson)
    {
        SaveChoice(scope, sessionId, optionId, selectedValue);
        SaveOptions(scope, sessionId, optionsJson);
        using var returned = JsonDocument.Parse(optionsJson);
        var confirmsModel = returned.RootElement.ValueKind == JsonValueKind.Array && returned.RootElement.EnumerateArray().Any(option =>
            option.ValueKind == JsonValueKind.Object && option.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == optionId &&
            option.TryGetProperty("category", out var category) && category.ValueKind == JsonValueKind.String && category.GetString() == "model" && option.TryGetProperty("currentValue", out var current) &&
            current.ValueKind == JsonValueKind.String && current.GetString() == selectedValue);
        if (HasPendingModel && confirmsModel)
            scope.Execute("UPDATE session_config SET pending_model=NULL WHERE session_id=$id", new SqlArg("$id", sessionId));
        ObserveModel(scope, sessionId, optionsJson);
    }

    private void ObserveModel(IWriteScope scope, string sessionId, string options)
    {
        if (_engine is not { } engine) return;
        using var document = JsonDocument.Parse(options);
        engine.Configured(scope, sessionId, ConfigOptions.ModelValue(document.RootElement));
    }

    /// <summary>Chooses a value to apply on subsequent adapter sessions.</summary>
    public void SetConfigChoice(string sessionId, string configId, string value) => SaveConfigChoice(sessionId, configId, value, preserveIntent: false);

    internal void SetUnconfirmedConfigChoice(string sessionId, string configId, string value) => SaveConfigChoice(sessionId, configId, value, preserveIntent: true);

    private void SaveConfigChoice(string sessionId, string configId, string value, bool preserveIntent) =>
        _writer.Write("sessions.config", scope =>
        {
            SaveChoice(scope, sessionId, configId, value);
            if (!preserveIntent && HasPendingModel)
            {
                var config = Config(scope, sessionId);
                var raw = config.Options ?? (Get(scope, sessionId) is { } session ? AgentConfig(scope, session.Agent) : null);
                if (raw is not null)
                {
                    using var options = JsonDocument.Parse(raw);
                    var modelChoice = options.RootElement.ValueKind == JsonValueKind.Array && options.RootElement.EnumerateArray().Any(option =>
                        option.ValueKind == JsonValueKind.Object && option.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == configId &&
                        option.TryGetProperty("category", out var category) && category.ValueKind == JsonValueKind.String && category.GetString() == "model");
                    if (modelChoice)
                        scope.Execute("UPDATE session_config SET pending_model=NULL WHERE session_id=$id", new SqlArg("$id", sessionId));
                }
            }
            return 0;
        });

    private static void SaveChoice(IWriteScope scope, string sessionId, string configId, string value)
    {
        var raw = scope.QueryFirst("SELECT choices FROM session_config WHERE session_id=$id", row => row.Text(0), new SqlArg("$id", sessionId));
        var choices = raw is null ? new Dictionary<string, string>() : ReadChoices(raw).ToDictionary(pair => pair.Key, pair => pair.Value);
        choices[configId] = value;
        SaveChoices(scope, sessionId, choices);
    }

    private static void SaveChoices(IWriteScope scope, string sessionId, IReadOnlyDictionary<string, string> choices) =>
        scope.Execute("""
            INSERT INTO session_config(session_id, choices) VALUES ($id, $choices)
            ON CONFLICT(session_id) DO UPDATE SET choices = $choices;
            """, new SqlArg("$id", sessionId),
            new SqlArg("$choices", JsonSerializer.Serialize(choices, SessionsJson.Default.IReadOnlyDictionaryStringString)));

    internal void ObserveConfig(SessionInfo expected, SessionConfig expectedConfig, SessionLocation expectedLocation, string nativeId,
        string? options, string? commands, IReadOnlyDictionary<string, string> choices, KeyValuePair<string, string>? confirmedModel,
        Action validate, CancellationToken ct, string? agentOptions = null, NativeSessionLaunch? nativeLaunch = null)
    {
        _writer.Write("sessions.config.observation", scope =>
        {
            ct.ThrowIfCancellationRequested();
            validate();
            var current = Get(scope, expected.SessionId);
            var config = Config(scope, expected.SessionId);
            if (current is null || current.Agent != expected.Agent || current.Machine != expected.Machine ||
                current.AcpSessionId != expected.AcpSessionId || current.WorkingFolder != expected.WorkingFolder ||
                current.ParentSessionId != expected.ParentSessionId || current.RepoUrl != expected.RepoUrl ||
                Location(scope, current) != expectedLocation || config.ConfigTarget != expectedConfig.ConfigTarget ||
                config.PendingModel != expectedConfig.PendingModel || !config.Choices.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .SequenceEqual(expectedConfig.Choices.OrderBy(pair => pair.Key, StringComparer.Ordinal)))
                throw new CallFailed("The session changed while its settings were being read.");
            scope.Execute("UPDATE session_info SET acp_session_id=$native WHERE session_id=$id", new SqlArg("$native", nativeId), new SqlArg("$id", expected.SessionId));
            SaveOptions(scope, expected.SessionId, options ?? "null");
            if (nativeLaunch is not null)
                scope.Execute("UPDATE session_config SET native_launch_json=$launch,native_witness_json=NULL WHERE session_id=$id",
                    new SqlArg("$launch", JsonSerializer.Serialize(nativeLaunch, SessionsJson.Default.NativeSessionLaunch)), new("$id", expected.SessionId));
            SaveChoices(scope, expected.SessionId, choices);
            if (commands is not null) SaveCommands(scope, expected.SessionId, commands);
            else scope.Execute("INSERT INTO session_commands(session_id,commands) VALUES($id,'null') ON CONFLICT(session_id) DO NOTHING",
                new SqlArg("$id", expected.SessionId));
            if (agentOptions is not null) SaveAgentOptions(scope, expected.Agent, agentOptions);
            if (confirmedModel is { } selected && HasPendingModel)
            {
                using var document = JsonDocument.Parse(options ?? "null");
                if (ConfigOptions.ModelValue(document.RootElement) == selected.Value)
                    scope.Execute("UPDATE session_config SET pending_model=NULL WHERE session_id=$id", new SqlArg("$id", expected.SessionId));
            }
            ObserveModel(scope, expected.SessionId, options ?? "null");
            return 0;
        });
    }

    public string? AgentConfig(string agent)
    {
        using var scope = _db.OpenSnapshot(CancellationToken.None);
        return AgentConfig(scope, agent);
    }

    public string? AgentConfig(IReadScope scope, string agentId) =>
        scope.QueryFirst("SELECT options FROM agent_config WHERE agent = $agent", r => r.Text(0), new SqlArg("$agent", agentId));

    /// <summary>Saves the slash commands the session's agent sent, as the session's and as that agent's latest.</summary>
    public void SetCommands(string sessionId, string commands) =>
        _writer.Write("sessions.config", scope => { SaveCommands(scope, sessionId, commands); return 0; });

    private static void SaveCommands(IWriteScope scope, string sessionId, string? commands)
    {
        scope.Execute("""
            INSERT INTO session_commands(session_id, commands) VALUES ($id, $commands)
            ON CONFLICT(session_id) DO UPDATE SET commands = $commands;
            """, new SqlArg("$id", sessionId), new SqlArg("$commands", commands ?? "null"));
        if (commands is not null)
            scope.Execute("""
                INSERT INTO agent_commands(agent, commands) SELECT agent, $commands FROM session_info WHERE session_id=$id
                ON CONFLICT(agent) DO UPDATE SET commands=$commands;
                """, new SqlArg("$id", sessionId), new SqlArg("$commands", commands));
    }

    public void SetAgentConfig(string agent, string options) =>
        _writer.Write("sessions.config", scope => { SaveAgentOptions(scope, agent, options); return 0; });

    private static void SaveAgentOptions(IWriteScope scope, string agent, string options) =>
        scope.Execute("""
            INSERT INTO agent_config(agent, options) VALUES ($agent, $options)
            ON CONFLICT(agent) DO UPDATE SET options = $options;
            """, new SqlArg("$agent", agent), new SqlArg("$options", options));

}
