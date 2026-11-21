using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Engine;

namespace Assembly.Application.Features.Chat.Sessions.Drafts;

/// <summary>
/// <c>drafts/set</c> saves a session's unsent draft where the session runs, which <c>sessions/open_target</c> hands back
/// with the open. Bound to the session's source like a send and ordered with its sends, so a draft saved before a send
/// can't land after its accept clears it. A remote session's goes to the remote's own <c>drafts/set</c>.
/// </summary>
internal sealed class DraftsFeature(IEngineDoor door) : IFeature
{
    public void Register(ICallRegistry calls) =>
        calls.Write("drafts", "set", DraftsJson.Default.SaveDraftArgs, DraftsJson.Default.Saved, async (args, call) =>
        {
            var source = SendFeature.Source(args.ExpectedSource, call) ?? throw new CallBadArguments("A draft save names its session's source.");
            var apply = await door.PrepareSourceDraftAsync(args.Session, new(args.Text, args.Attachments ?? []), source, call);
            return scope => new Saved(apply(scope));
        }, args => SendFeature.OrderOf(args.Session));

    /// <summary>The arguments of a save of <paramref name="draft"/> bound to <paramref name="source"/>.</summary>
    public static JsonElement Args(string session, SessionDraft draft, ExpectedSource source) =>
        JsonSerializer.SerializeToElement(new SaveDraftArgs(session, draft.Text, draft.Attachments, source), DraftsJson.Default.SaveDraftArgs);

    internal sealed record SaveDraftArgs([property: JsonRequired] string Session, [property: JsonRequired] string Text,
        IReadOnlyList<DraftAttachment>? Attachments = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExpectedSource? ExpectedSource = null);

    internal sealed record Saved(bool Ok);
}
