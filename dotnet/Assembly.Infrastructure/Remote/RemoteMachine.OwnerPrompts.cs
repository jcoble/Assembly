using System.Text.Json;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Infrastructure.Remote;

internal sealed partial class RemoteMachine
{
    internal async Task<OwnerPromptAccepted> SendOwnerPromptAsync(OwnerPromptRequest request, CancellationToken cancellationToken)
    {
        var result = await CallAsync("send", "send", new SendFeature.SendArgs(request.Session, request.Text,
            request.ClientId, request.AttachmentIds, request.Epoch), cancellationToken);
        var accepted = result.Deserialize(SessionsJson.Default.OwnerPromptAccepted)
            ?? throw new IOException("Invalid owner prompt acknowledgement");
        _follower.Watch(request.Session);
        return accepted;
    }
}
