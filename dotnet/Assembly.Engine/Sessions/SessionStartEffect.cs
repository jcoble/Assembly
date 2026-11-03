using System.Text.Json.Serialization;
using Assembly.Engine.Handoffs;

namespace Assembly.Engine.Sessions;

/// <summary>The sole start payload; ordinary starts retain the exact existing fields, order and omitted-null bytes.</summary>
public sealed record SessionStartEffect(string Provider, string? Model, string Folder, string Title,
    string? ParentId, string? RunId, string? TaskUrl,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RepoUrl = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ManagedCheckout = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProviderSessionId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Origin = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HandoffStartBinding? Handoff = null)
{
    public static SessionStartEffect From(StartSession command) => new(command.Provider, command.Model, command.Folder,
        command.Title, command.ParentId, command.RunId, command.TaskUrl, command.RepoUrl, command.ManagedCheckout);
}
