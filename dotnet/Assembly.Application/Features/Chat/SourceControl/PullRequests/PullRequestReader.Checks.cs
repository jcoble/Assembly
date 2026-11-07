using System.Globalization;
using System.Text.RegularExpressions;
using Assembly.Application.Common.Calls;
namespace Assembly.Application.Features.Chat.SourceControl.PullRequests;

/// <summary>What a failing check and a conflicting merge actually say, so neither needs the website.</summary>
public sealed partial class PullRequestReader
{
    private const int LogLines = 300;
    private const int ConflictFiles = 20;
    private const int RegionLines = 160;
    /// <summary>A failed Actions job's log, from the step that failed to its error. The job must be one of this
    /// pull request's current checks.</summary>
    public async Task<PrCheckLog> CheckLogAsync(string root, string? repository, int number, long jobId, CancellationToken ct)
    {
        var detail = await Read(root, repository, number, ct);
        if (jobId <= 0 || !detail.Checks.Any(check => check.JobId == jobId)) throw new CallFailed("That job isn't one of this pull request's checks now. Refresh it.");
        var job = await Json(root, ["api", $"repos/{detail.Repository}/actions/jobs/{jobId.ToString(CultureInfo.InvariantCulture)}"], null, ct);
        var steps = Array(job, "steps").Select(step => (Name: Text(step, "name"), Conclusion: Text(step, "conclusion"))).ToArray();
        var failed = steps.FirstOrDefault(step => step.Conclusion == "failure").Name ?? "";
        var run = await gh.RunAsync(root, ["api", "--allow-escape-sequences", $"repos/{detail.Repository}/actions/jobs/{jobId.ToString(CultureInfo.InvariantCulture)}/logs"], null, Limit, ct);
        if (run.ExitCode != 0) throw new CallFailed("GitHub couldn't supply this job's log.\n\n" + run.Error.Trim());
        var (lines, cut) = FailingPart(run.Output);
        return new(jobId, failed, steps.Select(step => step.Name).ToArray(), lines, cut);
    }
    /// <summary>The lines from the last step that began before the first error, through that error, without timestamps,
    /// colour codes or GitHub's grouping marks. With no error, the log's end.</summary>
    internal static (IReadOnlyList<string> Lines, bool Cut) FailingPart(string log)
    {
        var lines = log.Replace("\r\n", "\n").Split('\n').Select(line => Ansi().Replace(Stamp().Replace(line, ""), "")).ToList();
        var error = lines.FindIndex(line => line.StartsWith("##[error]", StringComparison.Ordinal));
        var end = error < 0 ? lines.Count : error + 1;
        var start = error < 0 ? 0 : lines.FindLastIndex(error, line => line.StartsWith("##[group]Run ", StringComparison.Ordinal));
        if (start < 0) start = 0;
        List<string> kept = [];
        var inGroup = false;
        foreach (var line in lines.Skip(start).Take(end - start))
        {
            if (line.StartsWith("##[group]", StringComparison.Ordinal)) { kept.Add("▸ " + line["##[group]".Length..]); inGroup = true; continue; }
            if (line.StartsWith("##[endgroup]", StringComparison.Ordinal)) { inGroup = false; continue; }
            if (inGroup) continue; // A step's opening group repeats its command and environment.
            kept.Add(line.StartsWith("##[error]", StringComparison.Ordinal) ? "Error: " + line["##[error]".Length..]
                : line.StartsWith("##[warning]", StringComparison.Ordinal) ? "Warning: " + line["##[warning]".Length..] : line);
        }
        while (kept.Count > 0 && string.IsNullOrWhiteSpace(kept[^1])) kept.RemoveAt(kept.Count - 1);
        return kept.Count > LogLines ? (kept[^LogLines..], true) : (kept, false);
    }
    /// <summary>The files that conflict when this head merges into its base, each with Git's conflict regions. Only
    /// the two exact commits are fetched; no branch, working tree or index changes.</summary>
    public async Task<PrConflicts> ConflictsAsync(string root, string? repository, int number, string expectedHead, CancellationToken ct)
    {
        var detail = await Read(root, repository, number, ct);
        if (!string.Equals(detail.HeadSha, expectedHead, StringComparison.OrdinalIgnoreCase)) throw new CallFailed("This pull request changed. Refresh it before checking conflicts.");
        var fetch = await git.RunAsync(root, ["fetch", "--no-tags", "--no-write-fetch-head", "--quiet", "origin", detail.BaseSha, detail.HeadSha], null, Limit, ct);
        if (fetch.ExitCode != 0) throw new CallFailed("Couldn't fetch this pull request's commits.\n\n" + fetch.Error.Trim());
        var merge = await git.RunAsync(root, ["merge-tree", "--write-tree", "--name-only", "--no-messages", detail.BaseSha, detail.HeadSha], null, Limit, ct);
        if (merge.ExitCode == 0) return new(detail.BaseSha, detail.HeadSha, []);
        if (merge.ExitCode != 1) throw new CallFailed("Git couldn't work out the merge.\n\n" + merge.Error.Trim());
        var names = merge.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var tree = names.FirstOrDefault() ?? "";
        List<PrConflictFile> files = [];
        foreach (var path in names.Skip(1).Distinct().Take(ConflictFiles))
        {
            var shown = await git.RunAsync(root, ["show", tree + ":" + path], null, Limit, ct);
            var (regions, cut) = shown.ExitCode == 0 ? Regions(shown.Output) : ([], false);
            files.Add(new(path, regions, cut));
        }
        return new(detail.BaseSha, detail.HeadSha, files);
    }
    /// <summary>Each run of conflict markers with three lines before it, as one text; at most a fixed number of lines in all.</summary>
    internal static (IReadOnlyList<string> Regions, bool Cut) Regions(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        List<string> regions = [];
        var used = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].StartsWith("<<<<<<<", StringComparison.Ordinal)) continue;
            var end = System.Array.FindIndex(lines, index, line => line.StartsWith(">>>>>>>", StringComparison.Ordinal));
            if (end < 0) end = lines.Length - 1;
            var from = Math.Max(0, index - 3);
            if (used + end - from + 1 > RegionLines) return (regions, true);
            regions.Add(string.Join('\n', lines[from..(end + 1)]));
            used += end - from + 1;
            index = end;
        }
        return (regions, false);
    }
    private Task<PullRequestDetail> Read(string root, string? repository, int number, CancellationToken ct) =>
        repository is null ? DetailAsync(root, number, ct) : ProjectDetailAsync(root, repository, number, ct);
    [GeneratedRegex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?Z ")]
    private static partial Regex Stamp();
    [GeneratedRegex(@"\x1b\[[0-9;]*[A-Za-z]")]
    private static partial Regex Ansi();
}
