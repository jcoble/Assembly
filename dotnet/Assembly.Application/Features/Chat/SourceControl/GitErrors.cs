using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;

namespace Assembly.Application.Features.Chat.SourceControl;

/// <summary>Bounded runner failures with the existing English Git messages retained as details.</summary>
public static class GitErrors
{
    public static CallFailed From(string verb, GitRun run)
    {
        var text = (string.IsNullOrWhiteSpace(run.Error) ? run.Output : run.Error).Trim();
        var sentence = text switch
        {
            _ when text.Contains("not a git repository", StringComparison.Ordinal) => "This folder isn't a git repository yet.",
            _ when Has(text, "has no upstream branch", "no tracking information") => "This branch has no upstream. Publish it first.",
            _ when text.Contains("[rejected]", StringComparison.Ordinal) && Has(text, "fetch first", "non-fast-forward") => "The remote has commits you don't have. Pull first.",
            _ when Has(text, "Not possible to fast-forward", "diverging") => "Your branch and the remote have both moved. Merge or rebase in a terminal.",
            _ when text.Contains("CONFLICT", StringComparison.Ordinal) => "Git stopped on a conflict. Resolve the files marked ! and commit.",
            _ when text.Contains("index.lock", StringComparison.Ordinal) => "Another git command is running in this folder. Try again when it finishes.",
            _ when Has(text, "Authentication failed", "could not read Username", "Permission denied (publickey)") => "Git couldn't sign in to the remote from this machine.",
            _ when Has(text, "is already checked out at", "is already used by worktree at") => "That branch is checked out in another worktree: " + text[(text.IndexOf(" at ", StringComparison.Ordinal) + 4)..].Trim().Trim('\'', '\"') + ".",
            _ when text.Contains("nothing to commit", StringComparison.Ordinal) => "There's nothing staged to commit.",
            _ when text.Contains("No local changes to save", StringComparison.Ordinal) => "Nothing to stash; the folder is clean.",
            _ => $"git {verb} failed."
        };
        return new(sentence + "\n\n" + text);
    }
    private static bool Has(string text, params string[] values) => values.Any(value => text.Contains(value, StringComparison.Ordinal));
}
