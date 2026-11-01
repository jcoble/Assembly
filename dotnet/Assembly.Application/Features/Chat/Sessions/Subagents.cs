using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>Provider Task/Agent tools are display rows within their parent, not additional sessions.</summary>
internal sealed class Subagents
{
    private readonly Dictionary<string, SubagentPayload> _children = [];

    public bool Has(string id) => _children.ContainsKey(id);
    public string? Title(string id) => _children.GetValueOrDefault(id)?.Title;
    public bool AnyRunning => _children.Values.Any(child => child.State != "idle");
    public IEnumerable<string> Stoppable => _children.Where(pair => pair.Value.CanStop).Select(pair => pair.Key);

    public SubagentShown Changed(SessionInfo parent, SubagentChanged change)
    {
        if (!_children.ContainsKey(change.AcpSessionId))
            _children[change.AcpSessionId] = new SubagentPayload(parent.SessionId,
                change.Title ?? change.Description ?? "Subagent", change.Description,
                "running", null, null, false, change.AcpSessionId);
        return Update(change);
    }

    private SubagentShown Update(SubagentChanged change)
    {
        var previous = _children[change.AcpSessionId];
        var shown = previous with
        {
            Title = change.Title ?? previous.Title,
            Description = change.Description ?? previous.Description,
            State = change.State ?? previous.State,
            StopReason = change.StopReason,
            Error = change.Error,
            CanStop = false
        };
        _children[change.AcpSessionId] = shown;
        return new SubagentShown(change.At, change.AcpSessionId, shown);
    }

    public List<SubagentShown> StopAll(long at) =>
        _children.Where(pair => pair.Value.State != "idle").ToList()
            .Select(pair => Update(new SubagentChanged(at, pair.Key, null, null, "idle", "cancelled", null, false)))
            .ToList();
}
