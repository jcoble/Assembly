using System.Text.Json;
using Assembly.Domain.Events;
using static Assembly.Tests.Features.Chat.Rows.Converted;

namespace Assembly.Tests.Features.Chat.Rows;

/// <summary>Tool and prompt rows carry the files they mention beyond their own path, and nothing when there are none.</summary>
public sealed class NamedRowsTests
{
    private static readonly TurnStarted Start = new(0, "run", "go");

    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Converted Tool(ToolCallFields fields) =>
        From([Start, new ToolCallChanged(1, "t", true, fields)]);

    private static string[] Named(Converted turn) =>
        Get(turn.OfItem("t")[^1], "named").EnumerateArray().Select(path => path.GetString()!).ToArray();

    [Fact]
    public void A_second_location_is_named_and_the_first_stays_the_path()
    {
        var turn = Tool(new ToolCallFields(Name: "Read", Kind: "read", Paths: ["/a.md", "/b.md"]));
        Assert.Equal("/a.md", Text(turn.OfItem("t")[^1], "path"));
        Assert.Equal(["/b.md"], Named(turn));
    }

    [Fact]
    public void A_move_names_its_other_location()
    {
        var turn = Tool(new ToolCallFields(Name: "Move", Kind: "move", Paths: ["/from.md", "/to.md"]));
        Assert.Equal("/from.md", Text(turn.OfItem("t")[^1], "path"));
        Assert.Equal(["/to.md"], Named(turn));
    }

    [Fact]
    public void Paths_a_row_already_lists_in_files_are_not_named_again()
    {
        var turn = Tool(new ToolCallFields(Name: "Move", Kind: "move", Diffs: [new FileDiff(["/from.md", "/to.md"], null)]));
        Assert.Equal(2, Get(turn.OfItem("t")[^1], "files").GetArrayLength());
        Assert.False(turn.OfItem("t")[^1].Payload.TryGetProperty("named", out _));
    }

    [Fact]
    public void A_location_beyond_a_diffs_files_is_named()
    {
        var turn = Tool(new ToolCallFields(Name: "Edit", Kind: "edit", Diffs: [new FileDiff(["/a.md", "/b.md"], null)], Paths: ["/a.md", "/c.md"]));
        Assert.Equal(["/c.md"], Named(turn));
    }

    [Fact]
    public void A_one_file_diff_at_another_path_than_the_input_is_named()
    {
        var turn = Tool(new ToolCallFields(Name: "Edit", Kind: "edit", Input: Input("""{"file_path":"a.md"}"""), Diffs: [new FileDiff(["/repo/a.md"], null)]));
        Assert.Equal("a.md", Text(turn.OfItem("t")[^1], "path"));
        Assert.Equal(["/repo/a.md"], Named(turn));
    }

    [Fact]
    public void A_shell_call_names_its_commands_file() =>
        Assert.Equal(["a.md"], Named(Tool(new ToolCallFields(Name: "Bash", Kind: "execute", Input: Input("""{"command":"cat a.md"}""")))));

    [Fact]
    public void A_shell_call_resolves_against_its_cwd() =>
        Assert.Equal(["/repo/a.md"], Named(Tool(new ToolCallFields(Name: "Bash", Kind: "execute", Input: Input("""{"command":"cat a.md","cwd":"/repo"}""")))));

    [Fact]
    public void A_shell_calls_argv_keeps_its_script_whole() =>
        Assert.Equal(["my docs/a.md"], Named(Tool(new ToolCallFields(Name: "Bash", Kind: "execute", Input: Input("""{"command":["bash","-lc","cat 'my docs/a.md'"]}""")))));

    [Fact]
    public void A_shell_calls_description_is_not_read_as_a_command()
    {
        var turn = Tool(new ToolCallFields(Name: "Bash", Kind: "execute", Input: Input("""{"description":"cat notes.md"}""")));
        Assert.False(turn.OfItem("t")[^1].Payload.TryGetProperty("named", out _));
    }

    [Fact]
    public void A_one_file_read_writes_no_named()
    {
        var turn = Tool(new ToolCallFields(Name: "Read", Kind: "read", Input: Input("""{"file_path":"/a.md"}"""), Paths: ["/a.md"]));
        Assert.False(turn.OfItem("t")[^1].Payload.TryGetProperty("named", out _));
    }

    [Theory]
    [InlineData("search")]
    [InlineData("fetch")]
    [InlineData("think")]
    [InlineData("other")]
    public void Other_kinds_name_nothing(string kind)
    {
        var turn = Tool(new ToolCallFields(Name: "Tool", Kind: kind, Paths: ["/a", "/b"]));
        Assert.False(turn.OfItem("t")[^1].Payload.TryGetProperty("named", out _));
    }

    [Fact]
    public void Both_prompt_rows_carry_their_lists()
    {
        const string text = "see /a.md\n\nAttached image: /s/a.png (a.png)";
        var turn = From([new TurnStarted(0, "run", text), new TurnSteered(1, "run", text)]);
        var users = turn.OfKind("user");
        Assert.Equal(2, users.Count);
        foreach (var user in users)
        {
            Assert.Equal("/a.md", Assert.Single(Get(user, "named").EnumerateArray()).GetString());
            var image = Assert.Single(Get(user, "attached").EnumerateArray());
            Assert.Equal("/s/a.png", image.GetProperty("path").GetString());
            Assert.Equal("a.png", image.GetProperty("name").GetString());
        }
    }

    [Fact]
    public void A_prompt_with_no_paths_writes_no_lists()
    {
        var user = From([Start]).Last("user");
        Assert.False(user.Payload.TryGetProperty("named", out _));
        Assert.False(user.Payload.TryGetProperty("attached", out _));
    }

    [Fact]
    public void The_same_events_give_identical_rows_twice()
    {
        AgentEvent[] events =
        [
            new TurnStarted(0, "run", "see /a.md\n\nAttached image: /s/a.png (a.png)"),
            new ToolCallChanged(1, "t", true, new ToolCallFields(Name: "Bash", Kind: "execute", Input: Input("""{"command":"cat a.md","cwd":"/repo"}"""))),
            new TurnSteered(2, "run", "and /b.md"),
        ];
        Assert.Equal(From(events).Rows.Select(row => row.Payload.GetRawText()), From(events).Rows.Select(row => row.Payload.GetRawText()));
    }
}
