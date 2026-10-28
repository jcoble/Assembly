namespace Assembly.Application.Features.Worktrees;

/// <summary>Runs git in a folder and answers stdout; a non-zero exit throws CallFailed with git's stderr.</summary>
public interface IGit
{
    Task<string> RunAsync(string folder, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}
