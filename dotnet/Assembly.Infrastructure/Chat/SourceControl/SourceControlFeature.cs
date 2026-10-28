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
            await new GitStatusReader(new GitCli()).ReadAsync(SessionPaths.RootOf(sessions, args.Session), call.Cancellation));
    }

    internal sealed record Args([property: JsonRequired] string Session);
}

/// <summary>The arguments and answer for the session's git/status read.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SourceControlFeature.Args))]
[JsonSerializable(typeof(GitStatus))]
internal sealed partial class SourceControlJson : JsonSerializerContext;
