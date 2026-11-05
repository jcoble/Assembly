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

    [Theory]
    [InlineData("cat")]
    [InlineData("head")]
    [InlineData("tail")]
    [InlineData("less")]
    [InlineData("more")]
    [InlineData("wc")]
    [InlineData("nl")]
    [InlineData("bat")]
    [InlineData("tee")]
    [InlineData("touch")]
    [InlineData("cp")]
    [InlineData("mv")]
    [InlineData("sed")]
    [InlineData("awk")]
    [InlineData("grep")]
    [InlineData("rg")]
    [InlineData("ag")]
    public void Every_listed_command_names_its_arguments(string command) =>
        Assert.Equal(["a.md"], Of($"{command} a.md"));

    [Theory]
    [InlineData("if")]
    [InlineData("then")]
    [InlineData("do")]
    [InlineData("else")]
    [InlineData("elif")]
    [InlineData("!")]
    [InlineData("{")]
    [InlineData("sudo")]
    [InlineData("env")]
    [InlineData("time")]
    public void Every_listed_prefix_is_skipped(string prefix) =>
        Assert.Equal(["a.md"], Of($"{prefix} cat a.md"));

    [Theory]
    [InlineData("-g")]
    [InlineData("--glob")]
    [InlineData("-t")]
    [InlineData("--type")]
    [InlineData("-e")]
    [InlineData("-f")]
    public void Every_listed_flags_value_is_left_out(string flag) =>
        Assert.Equal(["a.md"], Of($"rg {flag} value a.md"));

    /// <summary><paramref name="expected"/> holds the paths joined by a bar, empty for none.</summary>
    [Theory]
    // Separators.
    [InlineData("false || cat a.md", "a.md")]
    [InlineData("cat a.md||cat b.md", "a.md|b.md")]
    [InlineData("sleep 1 & cat a.md", "a.md")]
    // Redirects.
    [InlineData("echo hi >out.md", "out.md")]
    [InlineData("echo hi >>out.md", "out.md")]
    [InlineData("echo hi &> out.md", "out.md")]
    [InlineData("echo hi &>> out.md", "out.md")]
    [InlineData("echo hi >| forced.md", "forced.md")]
    [InlineData("echo hi >& out.log", "out.log")]
    [InlineData("echo hi >&out.log", "out.log")]
    [InlineData("echo hi >&2", "")]
    [InlineData("echo hi 2>&1", "")]
    [InlineData("cat <&3", "")]
    [InlineData("cat <&in.txt", "in.txt")]
    // Heredocs and here-strings.
    [InlineData("cat <<-EOF\n\tcat ignored.md\n\tEOF\ncat after.md", "after.md")]
    [InlineData("cat <<< notes.md", "")]
    // Comments.
    [InlineData("# don't do this\ncat a.md", "a.md")]
    [InlineData("cat a.md # see b.md", "a.md")]
    [InlineData("echo hi # > note.md", "")]
    [InlineData("cat a#b.md", "a#b.md")]
    [InlineData("cat \"#a.md\"", "#a.md")]
    // Grouping.
    [InlineData("{ cat a.md; } > out.md", "a.md|out.md")]
    // cd back to the start, with no cwd.
    [InlineData("cd docs; cd ..; cat c.md", "c.md")]
    [InlineData("cd docs/.. && cat c.md", "c.md")]
    [InlineData("cd . && cat c.md", "c.md")]
    // cd to a place that can't be known.
    [InlineData("cd; cat a.md; cat /abs.md; cat ~/h.md", "/abs.md|~/h.md")]
    [InlineData("cd $X && cat a.md /abs.md", "/abs.md")]
    [InlineData("cd - && cat a.md", "")]
    [InlineData("cd $X; cd docs; cat a.md", "")]
    [InlineData("cd $X; echo hi > a.md", "")]
    [InlineData("cd $X; bash -lc 'cat a.md'", "")]
    [InlineData("cd $X; cd /known; cat a.md", "/known/a.md")]
    // cd inside parentheses.
    [InlineData("(cd docs && cat a.md); cat b.md", "docs/a.md|b.md")]
    [InlineData("(cd $X; cat a.md); cat b.md", "b.md")]
    public void A_rule_names_what_it_says(string command, string expected) =>
        Assert.Equal(expected.Split('|', StringSplitOptions.RemoveEmptyEntries), Of(command));

    [Theory]
    [InlineData("cd docs; cd ..; cat c.md", "/repo/c.md")]
    [InlineData("cd . && cat c.md", "/repo/c.md")]
    [InlineData("(cd docs && cat a.md); cat c.md", "/repo/docs/a.md|/repo/c.md")]
    public void With_a_cwd_cd_comes_back_to_it(string command, string expected) =>
        Assert.Equal(expected.Split('|'), Of(command, "/repo"));

    [Fact]
    public void A_cwd_does_not_survive_a_cd_to_an_unknown_place() =>
        Assert.Empty(Of("cd $X && cat README.md", "/repo"));
}
