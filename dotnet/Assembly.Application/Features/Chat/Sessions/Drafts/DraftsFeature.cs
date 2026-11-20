using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;

namespace Assembly.Application.Features.Chat.Sessions.Drafts;

/// <summary>
/// <c>drafts/set</c> saves a local session's unsent draft, which <c>sessions/open_target</c> hands back with the open.
/// Ordered with the session's sends, so a draft saved before a send can't land after its accept clears it. A remote
/// session's draft is the remote machine's to keep (not ported), so it is refused here.
/// </summary>
internal sealed class DraftsFeature(SessionStore store, TimeProvider time) : IFeature
{
    public void Register(ICallRegistry calls) =>
        calls.Write("drafts", "set", DraftsJson.Default.SaveDraftArgs, DraftsJson.Default.Saved, (args, _) =>
        {
            var session = store.Get(args.Session) ?? throw new CallFailed("unknown session");
            if (session.Machine is not null) throw new CallFailed("only a local session's draft is saved here");
            var draft = new SessionDraft(args.Text, args.Attachments ?? []);
            return Task.FromResult<Func<IWriteScope, Saved>>(scope =>
            {
                DraftStore.Set(scope, args.Session, draft, time.GetUtcNow().ToUnixTimeMilliseconds());
                return new(true);
            });
        }, args => SendFeature.OrderOf(args.Session));

    internal sealed record SaveDraftArgs([property: JsonRequired] string Session, [property: JsonRequired] string Text,
        IReadOnlyList<DraftAttachment>? Attachments = null);

    internal sealed record Saved(bool Ok);
}
