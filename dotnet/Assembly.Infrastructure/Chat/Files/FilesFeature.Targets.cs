using Assembly.Application.Common.Calls;

namespace Assembly.Infrastructure.Chat.Files;

/// <summary>A files call names its file by a session and a relative path, or by a path target that
/// <see cref="FileOwner.ResolvePath"/> turns into a real parent folder and file name. Both then run the same read and
/// save code.</summary>
internal sealed partial class FilesFeature
{
    private const string Moved = "the file moved; the draft is preserved";

    /// <summary>The trusted root and the path under it. Exactly one of session and target; a session call names its path,
    /// a target call doesn't, since the target holds it.</summary>
    private (string Root, string Path) LocalTarget(string? session, PathTarget? target, string? path, CancellationToken ct,
        FileOwnership? expected = null)
    {
        RequireOneTarget(session, target);
        if (target is null)
            return (LocalRoot(session!, ct, expected), path ?? throw new CallBadArguments("A file path is required."));
        if (path is not null) throw new CallBadArguments("A target names its own file; the call sends no path.");
        var owner = owners.ResolvePath(target, ct, expected);
        return (owner.CheckoutRoot, owner.File!);
    }

    /// <summary>A target call names no session and no path: the target holds its file.</summary>
    internal static void RequireTargetAlone(string? session, string? path)
    {
        if (session is not null) throw new CallBadArguments("A file call names a session or a target, one of them.");
        if (path is not null) throw new CallBadArguments("A target names its own file; the call sends no path.");
    }

    private static void RequireOneTarget(string? session, PathTarget? target)
    {
        if ((session is null) == (target is null)) throw new CallBadArguments("A file call names a session or a target, one of them.");
    }

    /// <summary>The session of a call that works on a session's whole folder; a path target can't name one.</summary>
    private static string SessionOnly(string? session, PathTarget? target) => target is not null
        ? throw new CallBadArguments("that call needs a session")
        : session ?? throw new CallBadArguments("A file session is required.");

    /// <summary>A path target can name any file this backend's user can read, so only the owner may send one.</summary>
    private static void RequireOwner(PathTarget? target, CallContext call)
    {
        if (target is not null && call.Peer is not OwnerPeer) throw new CallFailed("forbidden", "forbidden");
    }

    /// <summary>Refuses when a target's path now means another real file than the one this call began on.</summary>
    private void RequireSameFile(PathTarget target, string root, string path, CancellationToken ct)
    {
        if (LocalTarget(null, target, null, ct) != (root, path)) throw new CallFailed(Moved, "checkout_changed");
    }

    /// <summary>The save's owner still holds its root, rechecked before the save's disk work and again just before the
    /// replace. A target's file that is gone is the save's own conflict, not a move, so the conflict flow answers it.</summary>
    private void RequireSameOwner(BeginSave save, string root, CallContext call, CancellationToken ct)
    {
        RequireOwner(save.Target, call);
        if (save.Target is null)
        {
            if (LocalRoot(save.Session!, ct) != root) throw new CallFailed("the session's checkout changed; the draft is preserved");
            return;
        }
        if (File.Exists(Path.Combine(root, save.Path!))) RequireSameFile(save.Target, root, save.Path!, ct);
    }

    /// <summary>The write queue key: the file's full path, for either target. A target that the path rule refuses gets no
    /// key here, so the handler, which checks the peer first, gives the refusal.</summary>
    private string? OrderOf(BeginSave args)
    {
        try
        {
            var (root, path) = LocalTarget(args.Session, args.Target, args.Path, CancellationToken.None, args.ExpectedOwner);
            return "files:" + SessionPaths.ResolveFile(root, path);
        }
        catch (CallFailed) when (args.Target is not null) { return null; }
    }
}
