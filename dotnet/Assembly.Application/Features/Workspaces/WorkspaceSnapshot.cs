using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assembly.Application.Features.Workspaces;

public sealed record WorkspaceKey(string Machine, string SessionId, string CheckoutRoot)
{
    public string TopicKey => $"{Machine}/{SessionId}/{CheckoutRoot}";
}
public sealed record WorkspaceReadResult(JsonElement? Snapshot, long Revision);
public sealed record WorkspaceSaveResult(long Revision);
public sealed record WorkspaceChange(string Machine, string SessionId, string CheckoutRoot, long Revision)
{
    [JsonIgnore] public string Key => $"{Machine}/{SessionId}/{CheckoutRoot}";
}
internal sealed record WorkspaceReadArgs([property: JsonRequired] string Machine, [property: JsonRequired] string SessionId,
    [property: JsonRequired] string CheckoutRoot);
internal sealed record WorkspaceSaveArgs([property: JsonRequired] string Machine, [property: JsonRequired] string SessionId,
    [property: JsonRequired] string CheckoutRoot, [property: JsonRequired] long ExpectedRevision, [property: JsonRequired] JsonElement Snapshot);
