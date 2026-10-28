using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat.SourceControl;
using Assembly.Infrastructure.Git;

namespace Assembly.Infrastructure.Chat.SourceControl;

/// <summary>Reads a session's git status over the socket from its local folder.</summary>
internal sealed class SourceControlFeature(SessionStore sessions) : IFeature
{
    public void Register(ICallRegistry calls)
    {
        calls.Read("git", "status", SourceControlJson.Default.Args, SourceControlJson.Default.GitStatus, async (args, call) =>
            await new GitStatusReader(new GitCli()).ReadAsync(RootOf(args.Session), call.Cancellation));
    }

    private string RootOf(string sessionId)
    {
        var session = sessions.Get(sessionId) ?? throw new CallFailed("unknown session");
        var root = sessions.Root(session) ?? throw new CallFailed($"this session's files are on {session.Machine}, which this window can't show yet");
        return Directory.Exists(root) ? root : throw new CallFailed("this session's folder is made on its first message");
    }

    internal sealed record Args([property: JsonRequired] string Session);
}

/// <summary>The arguments and answer for the session's git/status read.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SourceControlFeature.Args))]
[JsonSerializable(typeof(GitStatus))]
internal sealed partial class SourceControlJson : JsonSerializerContext;
