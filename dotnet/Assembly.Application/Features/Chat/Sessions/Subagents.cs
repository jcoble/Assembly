using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>Provider-native children are display rows within their parent, not additional sessions.</summary>
internal sealed class Subagents
{
    private readonly Dictionary<string, SubagentPayload> _children = [];

    public bool Has(string id) => _children.ContainsKey(id);
    public string? Title(string id) => _children.GetValueOrDefault(id)?.Title;
    public bool AnyRunning => _children.Values.Any(child => child.State is "running" or "requires_action");
    public IEnumerable<string> Stoppable => _children.Where(pair => pair.Value.CanStop).Select(pair => pair.Key);

    public SubagentShown Changed(SessionInfo parent, SubagentChanged change) =>
        Show(change.At, change.AcpSessionId, new SubagentPayload(parent.SessionId,
            change.Title ?? "", change.Description, change.State ?? "unknown", change.StopReason,
            change.Error, change.CanCancel == true && change.State is "running" or "requires_action", null, CanCancel: change.CanCancel ?? false));

    private SubagentShown Show(long at, string id, SubagentPayload payload)
    {
        _children[id] = payload;
        return new SubagentShown(at, id, payload);
    }

    public List<SubagentShown> StopAll(long at) =>
        _children.Where(pair => pair.Value.State is "running" or "requires_action").ToList()
            .Select(pair => Show(at, pair.Key, pair.Value with { State = "idle", StopReason = "cancelled", Error = null, CanStop = false }))
            .ToList();
}
