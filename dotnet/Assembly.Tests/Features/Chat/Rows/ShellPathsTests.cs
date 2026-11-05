using Assembly.Application.Features.Chat.Rows.Context;

namespace Assembly.Tests.Features.Chat.Rows;

/// <summary>Candidate paths in shell commands, most of them the commands real agents sent in the recordings.</summary>
public sealed class ShellPathsTests
{
    private static IReadOnlyList<string> Of(string command, string? cwd = null) => ShellPaths.Candidates(command, cwd);

    [Fact]
    public void A_home_path_stays_as_written() =>
        Assert.Equal(["~/.codex/AGENTS.md"], Of("cat ~/.codex/AGENTS.md"));

    [Fact]
    public void A_home_path_stays_as_written_with_a_cwd() =>
        Assert.Equal(["~/.codex/AGENTS.md"], Of("cat ~/.codex/AGENTS.md", "/repo"));

    [Fact]
    public void An_if_block_names_the_file_it_reads_not_the_test() =>
        Assert.Equal(["AGENTS.md"], Of("if [ -f AGENTS.md ]; then cat AGENTS.md; fi"));

    [Fact]
    public void A_cwd_resolves_a_relative_word() =>
        Assert.Equal(["/repo/AGENTS.md"], Of("if [ -f AGENTS.md ]; then cat AGENTS.md; fi", "/repo"));

    [Fact]
    public void A_flag_is_not_a_path() =>
        Assert.Equal(["notes.txt"], Of("wc -l notes.txt"));

    [Fact]
    public void A_pipeline_and_a_second_command_name_one_file_once() =>
        Assert.Equal(["notes.txt"], Of("tail -c1 notes.txt | od -c; wc -c notes.txt"));

    [Fact]
    public void Rg_files_names_nothing() =>
        Assert.Empty(Of("pwd; rg --files -g AGENTS.md -g notes.txt"));

    [Fact]
    public void Unknown_commands_name_nothing() =>
        Assert.Empty(Of("sleep 15 && echo finished"));

    [Fact]
    public void A_loop_with_a_substitution_names_nothing() =>
        Assert.Empty(Of("for i in $(seq 1 30); do echo step $i; sleep 1; done"));

    [Fact]
    public void A_substitution_stays_one_word_and_is_skipped() =>
        Assert.Equal(["b.md"], Of("cat $(echo a.md c.md) b.md"));

    [Theory]
    [InlineData("/bin/zsh -lc 'cat a.md'")]
    [InlineData("bash -lc 'cat a.md'")]
    [InlineData("sh -c \"cat a.md\"")]
    [InlineData("zsh -c 'cat a.md'")]
    public void A_shell_wrapper_is_unwrapped(string command) =>
        Assert.Equal(["a.md"], Of(command));

    [Fact]
    public void A_wrapped_command_with_no_file_names_nothing() =>
        Assert.Empty(Of("/bin/zsh -lc pwd"));

    [Fact]
    public void A_wrapped_script_keeps_the_cwd() =>
        Assert.Equal(["/repo/a.md"], Of("bash -lc 'cat a.md'", "/repo"));

    [Fact]
    public void Cd_sets_the_base_for_later_commands() =>
        Assert.Equal(["docs/a.md"], Of("cd docs && cat a.md"));

    [Fact]
    public void Cd_builds_on_the_cwd() =>
        Assert.Equal(["/repo/docs/a.md"], Of("cd docs && cat a.md", "/repo"));

    [Fact]
    public void Dots_are_cleaned_up() =>
        Assert.Equal(["/repo/b/c.md", "/repo/d.md"], Of("cat ./b/c.md ./b/../d.md", "/repo"));

    [Fact]
    public void A_redirect_target_counts_on_any_command_and_a_descriptor_target_does_not() =>
        Assert.Equal(["out.md"], Of("echo hi > out.md 2>&1"));

    [Fact]
    public void An_appending_and_an_input_redirect_count() =>
        Assert.Equal(["log.txt", "in.txt"], Of("sort >> log.txt < in.txt"));

    [Fact]
    public void A_device_is_not_a_path() =>
        Assert.Empty(Of("echo hi > /dev/null; cat /dev/urandom"));

    [Fact]
    public void A_heredocs_body_is_ignored_and_what_follows_it_is_read()
    {
        const string command = "cat > out.md <<'EOF'\ncat ignored.md\n> ignored-too.md\nEOF\nwc -l after.md";
        Assert.Equal(["out.md", "after.md"], Of(command));
    }

    [Fact]
    public void A_sed_script_is_a_candidate_for_the_read_to_drop() =>
        Assert.Contains("a.rs", Of("sed -n 1,5p a.rs"));

    [Fact]
    public void A_quoted_path_with_a_space_is_one_word() =>
        Assert.Equal(["/my docs/a.md"], Of("cat \"/my docs/a.md\""));

    [Fact]
    public void A_flags_value_is_not_a_candidate() =>
        Assert.Equal(["src"], Of("rg -g '.md' -t rs -e pattern src"));

    [Fact]
    public void Words_a_shell_would_expand_are_skipped() =>
        Assert.Equal(["c.md"], Of("cat $HOME/a.md *.md b?.md `pwd`/d.md c.md"));

    [Fact]
    public void Leading_prefixes_and_assignments_are_skipped() =>
        Assert.Equal(["a.md"], Of("FOO=1 sudo time cat a.md"));

    [Fact]
    public void An_unknown_command_names_no_arguments() =>
        Assert.Empty(Of("python x.py data.csv"));

    [Fact]
    public void The_list_stops_at_fifty_without_repeats()
    {
        var words = string.Join(' ', Enumerable.Range(0, 60).SelectMany(i => new[] { $"f{i}.md", $"f{i}.md" }));
        var found = Of($"cat {words}");
        Assert.Equal(50, found.Count);
        Assert.Equal(50, found.Distinct().Count());
        Assert.Equal("f49.md", found[^1]);
    }
}
