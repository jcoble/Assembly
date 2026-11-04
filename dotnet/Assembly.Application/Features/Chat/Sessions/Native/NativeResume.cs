using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Engine;

namespace Assembly.Application.Features.Chat.Sessions.Native;

/// <summary>The one native resume path: a checkout move, an adopted handoff start and Step 13's resume_native all run it.</summary>
internal sealed class NativeResume(SessionStore sessionStore, IAgentLauncher launcher, Func<string, SharedAdapter> adapters)
{
    internal INativeSessionResolver? Resolver { get; set; }

    /// <summary>Restores a verified native transcript in a folder, then disposes its adapter borrower, without changing the conversation.</summary>
    internal async Task<NativeResumeEvidence> ResumeAsync(string sessionId, string expectedPublicId, string folder,
        Action validate, CancellationToken ct)
    {
        var resolver = Resolver ?? throw new CallFailed("Native sessions are unavailable.", "unsupported");
        var session = sessionStore.Get(sessionId) ?? throw new CallFailed("The session no longer exists.", "missing");
        if (session.AcpSessionId != expectedPublicId) throw new CallFailed("Native identity changed.", "writer_elsewhere");
        var captured = ConfigTargetIdentity.Capture(launcher, session.Agent, sessionStore.Config(sessionId).ConfigTarget);
        var saved = sessionStore.NativeLaunch(sessionId);
        var launchFolder = saved?.Folder ?? sessionStore.Location(session)?.Folder ?? throw new CallFailed("This session has no working folder.", "no_folder");
        var launch = NativeLaunchContext.Capture(captured.Descriptor, captured.Environment, captured.TargetId, expectedPublicId, launchFolder)
            ?? throw new CallFailed("This provider has no native session files.", "unsupported");
        if (saved is null) sessionStore.RetainNativeLaunch(sessionId, launch);
        else if (saved != launch) throw new CallFailed("Native launch changed.", "writer_elsewhere");
        launcher.ValidateFolder(folder);
        validate();
        var source = resolver.Resolve(launch, sessionId, folder);
        var prepared = await resolver.PrepareFolderAsync(source, folder, ct);
        try
        {
            validate();
            await ResumeAndCloseAsync(launch.ProviderId, captured, source.Current.ResumeId, folder, ct);
            validate();
        }
        catch when (prepared)
        {
            await resolver.UndoFolderAsync(source, folder, CancellationToken.None);
            throw;
        }
        return new(Guid.NewGuid().ToString("D"), source, folder);
    }

    private async Task ResumeAndCloseAsync(string provider, (AgentDescriptor Descriptor, IReadOnlyDictionary<string, string> Environment, string TargetId) captured, string nativeId, string folder, CancellationToken ct)
    {
        var pool = adapters(provider);
        var running = await pool.GetForSessionAsync(nativeId, new StageTimes(pool.Time), ct, captured.TargetId,
            suppliedDescriptor: captured.Descriptor with { Env = captured.Environment });
        string? stillOpen = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            // Cancellation never abandons an unobserved open. Join its result before closing this exact borrower.
            await running.Link.Client.ResumeSessionAsync(new(nativeId, folder, []), CancellationToken.None);
            stillOpen = nativeId;
            await pool.CloseAsync(running, nativeId);
            stillOpen = null;
            ct.ThrowIfCancellationRequested();
        }
        finally { await pool.ReleaseWarmAsync(running, stillOpen); }
    }
}
