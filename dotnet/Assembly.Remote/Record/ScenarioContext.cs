using Assembly.Engine.Agents;
using Assembly.Remote.Smoke;

namespace Assembly.Remote.Record;

/// <summary>What a scenario can use: the live connection, its session and folder, and the stream of updates.</summary>
public sealed record ScenarioContext(AgentConnection Agent, string SessionId, string Cwd, UpdateWatcher Updates, CancellationToken Deadline);
