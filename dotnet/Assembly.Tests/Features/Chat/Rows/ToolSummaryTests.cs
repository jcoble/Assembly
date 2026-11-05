using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Events;

namespace Assembly.Tests.Features.Chat.Rows;

/// <summary>A tool call's file path carries the folder the call ran in, so its row links to the right file.</summary>
public sealed class ToolSummaryTests
{
    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string? DetailPath(ToolCallFields fields)
    {
        var tool = new OpenTool("t1");
        tool.Apply(fields);
        return tool.TakeChanges().Detail!.Path;
    }

    private static string? LinePath(ToolCallFields fields)
    {
        var tool = new OpenTool("t1");
        tool.Apply(fields);
        return tool.TakeChanges().Line!.Path;
    }

    [Fact]
    public void A_relative_tool_path_joins_the_call_s_workdir()
        => Assert.Equal("/repo/other/src/a.rs", ToolSummary.Path(Input("""{"file_path":"src/a.rs","workdir":"/repo/other","cwd":"/ignored"}""")));

    [Fact]
    public void A_relative_tool_path_joins_cwd_when_there_is_no_workdir()
        => Assert.Equal("/repo/src/a.rs", ToolSummary.Path(Input("""{"path":"src/a.rs","cwd":"/repo"}""")));

    [Fact]
    public void An_absolute_tool_path_stays_as_is()
    {
        Assert.Equal("/etc/hosts", ToolSummary.Path(Input("""{"file_path":"/etc/hosts","workdir":"/repo"}""")));
        Assert.Equal("~/a.txt", ToolSummary.Path(Input("""{"file_path":"~/a.txt","workdir":"/repo"}""")));
    }

    [Fact]
    public void A_call_with_no_folder_keeps_its_path()
    {
        Assert.Equal("src/a.rs", ToolSummary.Path(Input("""{"file_path":"src/a.rs"}""")));
        Assert.Equal("src/a.rs", ToolSummary.Path(Input("""{"file_path":"src/a.rs","workdir":""}""")));
        Assert.Equal("src/a.rs", ToolSummary.Path(Input("""{"file_path":"src/a.rs","workdir":7}""")));
    }

    [Fact]
    public void A_joined_path_drops_its_dot_parts_and_folds_its_dot_dot_parts()
    {
        Assert.Equal("/repo/src/a.rs", ToolSummary.Path(Input("""{"file_path":"./src/a.rs","workdir":"/repo"}""")));
        Assert.Equal("/repo/src/a.rs", ToolSummary.Path(Input("""{"file_path":"././src/a.rs","workdir":"/repo"}""")));
        Assert.Equal("/w/lib/a.rs", ToolSummary.Path(Input("""{"file_path":"../lib/a.rs","workdir":"/w/app"}""")));
        Assert.Equal("/w/lib/a.rs", ToolSummary.Path(Input("""{"file_path":"src/../../lib/./a.rs","workdir":"/w/app/"}""")));
        Assert.Equal("/a.rs", ToolSummary.Path(Input("""{"file_path":"../a.rs","workdir":"/repo"}""")));
        Assert.Equal("./src/a.rs", ToolSummary.Path(Input("""{"file_path":"./src/a.rs"}""")));
    }

    [Fact]
    public void A_joined_path_that_would_climb_above_the_top_folder_is_no_path()
    {
        Assert.Null(ToolSummary.Path(Input("""{"file_path":"../../a.rs","workdir":"/repo"}""")));
        Assert.Null(ToolSummary.Path(Input("""{"file_path":"../a.rs","workdir":"/"}""")));
        Assert.Null(LinePath(new ToolCallFields(Input: Input("""{"workdir":"/repo"}"""), Paths: ["../../b.rs"])));
    }

    [Fact]
    public void An_absolute_path_keeps_its_dot_dot_parts_since_only_the_join_is_folded()
        => Assert.Equal("/repo/../a.rs", ToolSummary.Path(Input("""{"file_path":"/repo/../a.rs","workdir":"/w"}""")));

    [Fact]
    public void A_relative_folder_leaves_the_path_unchanged()
    {
        Assert.Equal("src/a.rs", ToolSummary.Path(Input("""{"file_path":"src/a.rs","workdir":"sub"}""")));
        Assert.Equal("src/a.rs", ToolSummary.Path(Input("""{"file_path":"src/a.rs","workdir":"sub","cwd":"/repo"}""")));
    }

    [Fact]
    public void A_diff_path_joins_the_call_s_workdir_too()
    {
        var input = Input("""{"command":["apply_patch"],"workdir":"/repo/other"}""");
        Assert.Equal("/repo/other/a.rs", DetailPath(new ToolCallFields(Input: input, Diffs: [new FileDiff(["a.rs"], null)])));
        Assert.Equal("/repo/other/b.rs", LinePath(new ToolCallFields(Input: input, Paths: ["b.rs"])));
        Assert.Equal("/abs/c.rs", LinePath(new ToolCallFields(Input: input, Paths: ["/abs/c.rs"])));
        Assert.Equal("a.rs", LinePath(new ToolCallFields(Diffs: [new FileDiff(["a.rs"], null)])));
    }

    [Fact]
    public void The_summary_still_shows_the_path_the_call_gave()
    {
        var tool = new OpenTool("t1");
        tool.Apply(new ToolCallFields(Name: "Read", Input: Input("""{"file_path":"src/a.rs","workdir":"/repo"}""")));
        var line = tool.TakeChanges().Line!;
        Assert.Equal("src/a.rs", line.Summary);
        Assert.Equal("/repo/src/a.rs", line.Path);
    }

    // Replay reads the stored events through the same OpenTool, so it joins the same way.
    [Fact]
    public void Replay_joins_the_same_way()
    {
        var tool = new OpenTool("t1");
        tool.Apply(new ToolCallFields(Input: Input("""{"file_path":"x.rs"}""")));
        tool.Apply(new ToolCallFields(Input: Input("""{"file_path":"x.rs","cwd":"/w"}"""), Status: "completed"));
        Assert.Equal("/w/x.rs", tool.TakeChanges().Line!.Path);
    }
}
