using System.Text.Json;

namespace Assembly.Engine.Acp.Messages;

/// <summary>The new session, with its config options (model, effort, mode…), raw: <see cref="Sessions.ConfigOptions"/> reads them.</summary>
public sealed record NewSessionResponse(string SessionId, JsonElement ConfigOptions);
