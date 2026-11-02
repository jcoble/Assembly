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
            SELECT c.options, c.choices, COALESCE(tc.commands, ac.commands), {(hasPendingModel ? "c.pending_model" : "NULL")}
            FROM (SELECT $id AS session_id) t
            LEFT JOIN session_config c ON c.session_id = t.session_id
            LEFT JOIN session_commands tc ON tc.session_id = t.session_id
            LEFT JOIN session_info i ON i.session_id = t.session_id
            LEFT JOIN agent_commands ac ON ac.agent = i.agent;
            """, row => new SessionConfig(
                row.IsNull(0) ? null : row.Text(0),
                row.IsNull(1) ? new Dictionary<string, string>() : ReadChoices(row.Text(1)),
                row.IsNull(2) ? null : row.Text(2), row.IsNull(3) ? null : row.Text(3)), new SqlArg("$id", sessionId))!;

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

    public void ConfirmModelSelection(string sessionId, string optionId, string selectedValue, string optionsJson) =>
        _writer.Write("sessions.config", scope => { ConfirmModelSelection(scope, sessionId, optionId, selectedValue, optionsJson); return 0; });

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

    private static void SaveChoice(IWriteScope scope, string sessionId, string configId, string value) =>
        scope.Execute("""
            INSERT INTO session_config(session_id, choices) VALUES ($id, json_object($config, $value))
            ON CONFLICT(session_id) DO UPDATE SET choices = json_set(choices, '$."' || $config || '"', $value);
            """, new SqlArg("$id", sessionId), new SqlArg("$config", configId.Replace("\"", "")), new SqlArg("$value", value));

    public string? AgentConfig(string agent)
    {
        using var scope = _db.OpenSnapshot(CancellationToken.None);
        return AgentConfig(scope, agent);
    }

    public string? AgentConfig(IReadScope scope, string agentId) =>
        scope.QueryFirst("SELECT options FROM agent_config WHERE agent = $agent", r => r.Text(0), new SqlArg("$agent", agentId));

    /// <summary>Saves the slash commands the session's agent sent, as the session's and as that agent's latest.</summary>
    public void SetCommands(string sessionId, string commands) =>
        Execute("""
            INSERT INTO session_commands(session_id, commands) VALUES ($id, $commands)
            ON CONFLICT(session_id) DO UPDATE SET commands = $commands;
            INSERT INTO agent_commands(agent, commands) SELECT agent, $commands FROM session_info WHERE session_id = $id
            ON CONFLICT(agent) DO UPDATE SET commands = $commands;
            """, ("$id", sessionId), ("$commands", commands));

    public void SetAgentConfig(string agent, string options) =>
        Execute("""
            INSERT INTO agent_config(agent, options) VALUES ($agent, $options)
            ON CONFLICT(agent) DO UPDATE SET options = $options;
            """, ("$agent", agent), ("$options", options));

}
