using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Rows.Context;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Infrastructure.Chat;
using Assembly.Tests.Engine;

namespace Assembly.Tests.Features.Chat.Rows;

/// <summary>What <c>rows/files</c> lists: which calls and prompts name a file, how they count, and the disk check after.</summary>
public sealed class FilesTouchedTests : IDisposable
{
    private readonly EngineFixture _fixture = new();
    private readonly RowStore _store;
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("files-touched-");

    public FilesTouchedTests() => _store = RowStore.Open(_fixture.Db);

    public void Dispose()
    {
        _folder.Delete(recursive: true);
        _fixture.Dispose();
    }

    private string Folder => _folder.FullName;

    private void Save(params (string? Item, string Kind, string Payload)[] rows)
    {
        var batch = new RowBatch();
        foreach (var (item, kind, payload) in rows)
            batch.Rows.Add(new NewRow("run-1", item, kind, JsonDocument.Parse(payload).RootElement.Clone(), 1));
        _store.Commit("s", batch);
    }

    private static (string?, string, string) Call(string item, string kind, string state = "completed", string? path = null, string more = "") =>
        (item, RowKind.Tool, $$"""{"name":"{{kind}}","state":"{{state}}","path":{{JsonSerializer.Serialize(path)}},"hasDetail":false,"kind":"{{kind}}"{{more}}}""");

    private static string Named(params string[] paths) => $$""","named":{{JsonSerializer.Serialize(paths)}}""";

    private static (string?, string, string) Prompt(string more) => (null, RowKind.User, $$"""{"text":"go"{{more}}}""");

    private IReadOnlyList<FileTouched> Read() => _store.FilesTouched("s", CancellationToken.None, Folder, probe: new DiskPathProbe());

    private IReadOnlyList<FileTouched> ReadNoFolder() => _store.FilesTouched("s", CancellationToken.None);

    private string Real(string name)
    {
        var path = Path.Combine(Folder, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private static string[] Paths(IEnumerable<FileTouched> files) => files.Select(file => file.Path).ToArray();

    // Pins: g0-shape#13
    [Fact]
    public void A_calls_start_and_end_count_once()
    {
        Save(Call("t1", "edit", "started", "a.rs"), Call("t1", "edit", "completed", "a.rs", ""","added":3,"removed":1"""));

        Assert.Equal(new FileTouched("a.rs", 3, 1, 1, 2), Assert.Single(Read()));
    }

    // Pins: g0-shape#13
    [Fact]
    public void Two_calls_on_a_file_count_two_and_add_their_lines()
    {
        Save(Call("t1", "edit", path: "a.rs", more: ""","added":3"""), Call("t2", "edit", path: "a.rs", more: ""","added":2,"removed":4"""));

        Assert.Equal(new FileTouched("a.rs", 5, 4, 2, 2), Assert.Single(Read()));
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_call_naming_a_file_twice_counts_once()
    {
        Save(Call("t1", "execute", more: Named(Real("a.md"), "a.md")));

        Assert.Equal(1, Assert.Single(Read()).Count);
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_file_reached_through_the_real_path_of_a_symlinked_folder_is_inside()
    {
        var real = Directory.CreateDirectory(Path.Combine(Folder, "real")).FullName;
        var link = Path.Combine(Folder, "link");
        Directory.CreateSymbolicLink(link, real);
        File.WriteAllText(Path.Combine(real, "a.rs"), "x");
        Save(Call("t1", "edit", path: Path.Combine(real, "a.rs"), more: ""","added":2"""), Call("t2", "read", path: Path.Combine(link, "a.rs")));

        var files = _store.FilesTouched("s", CancellationToken.None, link, probe: new DiskPathProbe());

        Assert.Equal(new FileTouched("a.rs", 2, 0, 2, 2), Assert.Single(files));
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_missing_file_outside_a_symlinked_folder_keeps_its_plain_path()
    {
        var real = Directory.CreateDirectory(Path.Combine(Folder, "real")).FullName;
        var link = Path.Combine(Folder, "link");
        Directory.CreateSymbolicLink(link, real);
        Save(Call("t1", "edit", path: "/elsewhere/gone.rs"));

        Assert.Equal(["/elsewhere/gone.rs"], Paths(_store.FilesTouched("s", CancellationToken.None, link, probe: new DiskPathProbe())));
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_failed_calls_lines_dont_add_but_it_still_counts()
    {
        Save(Call("t1", "edit", path: "a.rs", more: ""","added":3"""), Call("t2", "edit", "failed", "a.rs", ""","added":9,"removed":9"""));

        Assert.Equal(new FileTouched("a.rs", 3, 0, 2, 2), Assert.Single(Read()));
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_failed_calls_files_lines_dont_add_either()
    {
        Save(Call("t1", "edit", "failed", "a.rs", ""","files":[{"path":"a.rs","added":2,"removed":1},{"path":"b.rs","added":3,"removed":0}]"""));

        Assert.Equal([new FileTouched("a.rs", 0, 0, 1, 1), new FileTouched("b.rs", 0, 0, 1, 1)], Read());
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_calls_path_outside_its_files_still_lists_with_no_lines()
    {
        Save(Call("t1", "edit", path: "input.rs", more: ""","added":5,"removed":1,"files":[{"path":"a.rs","added":2,"removed":1},{"path":"b.rs","added":3,"removed":0}]"""));

        Assert.Equal([new FileTouched("a.rs", 2, 1, 1, 1), new FileTouched("b.rs", 3, 0, 1, 1), new FileTouched("input.rs", 0, 0, 1, 1)], Read());
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_path_also_in_its_files_doesnt_double_its_lines_or_count()
    {
        Save(Call("t1", "edit", path: "a.rs", more: ""","added":5,"removed":1,"files":[{"path":"a.rs","added":2,"removed":1},{"path":"b.rs","added":3,"removed":0}]"""));

        Assert.Equal([new FileTouched("a.rs", 2, 1, 1, 1), new FileTouched("b.rs", 3, 0, 1, 1)], Read());
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_read_and_a_failed_call_list()
    {
        Save(Call("t1", "read", path: "r.md"), Call("t2", "edit", "failed", "f.rs"), Call("t3", "edit", "cancelled", "c.rs"), Call("t4", "edit", "started", "s.rs"));

        Assert.Equal(["s.rs", "c.rs", "f.rs", "r.md"], Paths(Read()));
    }

    // Pins: g0-shape#13
    [Theory]
    [InlineData("search")]
    [InlineData("fetch")]
    [InlineData("think")]
    [InlineData("other")]
    public void Other_kinds_never_name_a_file(string kind)
    {
        Save(Call("t1", kind, path: "a.rs", more: Named("b.rs")));

        Assert.Empty(ReadNoFolder());
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_shell_calls_own_path_is_ignored()
    {
        Save(Call("t1", "execute", path: "summary-path.rs"));

        Assert.Empty(ReadNoFolder());
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_shell_candidate_that_exists_lists_with_its_count()
    {
        Real("a.md");
        Save(Call("t1", "execute", more: Named("a.md")), Call("t2", "execute", more: Named("a.md")));

        Assert.Equal(new FileTouched("a.md", 0, 0, 2, 2), Assert.Single(Read()));
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_shell_candidate_that_doesnt_exist_is_dropped()
    {
        Save(Call("t1", "execute", more: Named("gone.md")));

        Assert.Empty(Read());
    }

    // Pins: g0-shape#13
    [Fact]
    public void An_existing_folder_is_dropped_whatever_named_it()
    {
        Directory.CreateDirectory(Path.Combine(Folder, "src"));
        Save(Call("t1", "execute", more: Named("src")), Call("t2", "read", path: "src"), Prompt(Named($"{Folder}/src")));

        Assert.Empty(Read());
    }

    // Pins: g0-shape#13
    [Fact]
    public void An_edit_path_that_no_longer_exists_stays()
    {
        Save(Call("t1", "edit", path: "gone.rs", more: ""","added":1"""));

        Assert.Equal(["gone.rs"], Paths(Read()));
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_path_both_edited_and_shell_named_stays_when_gone()
    {
        Save(Call("t1", "edit", path: "gone.rs"), Call("t2", "execute", more: Named("gone.rs")));

        Assert.Equal(new FileTouched("gone.rs", 0, 0, 2, 2), Assert.Single(Read()));
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_second_location_on_a_read_isnt_checked_for_existence()
    {
        Save(Call("t1", "read", path: "one.md", more: Named("two.md")));

        Assert.Equal(["one.md", "two.md"], Paths(Read()));
    }

    // Pins: g0-shape#13
    [Fact]
    public void An_absolute_and_a_relative_path_are_one_row()
    {
        Save(Call("t1", "edit", path: $"{Folder}/a.rs", more: ""","added":1"""), Call("t2", "edit", path: "a.rs", more: ""","added":1"""));

        Assert.Equal(new FileTouched("a.rs", 2, 0, 2, 2), Assert.Single(Read()));
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_relative_path_that_climbs_out_of_the_folder_lists_absolute()
    {
        Save(Call("t1", "read", path: "../lib/helper.rs"));

        Assert.Equal([$"{Path.GetDirectoryName(Folder)}/lib/helper.rs"], Paths(Read()));
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_climbing_and_a_whole_spelling_of_one_outside_file_are_one_row()
    {
        var outside = $"{Path.GetDirectoryName(Folder)}/lib/helper.rs";
        Save(Call("t1", "edit", path: "../lib/helper.rs", more: ""","added":2,"removed":1"""), Call("t2", "edit", path: outside, more: ""","added":3"""));

        Assert.Equal(new FileTouched(outside, 5, 1, 2, 2), Assert.Single(Read()));
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_dotted_and_a_plain_spelling_inside_the_folder_are_one_relative_row()
    {
        Save(Call("t1", "edit", path: "./x", more: ""","added":1"""), Call("t2", "edit", path: "x", more: ""","added":2"""));

        Assert.Equal(new FileTouched("x", 3, 0, 2, 2), Assert.Single(Read()));
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_home_path_and_its_tilde_are_one_row()
    {
        Save(Call("t1", "read", path: "~/a.md"), Call("t2", "read", path: "/home/x/a.md"));

        var file = Assert.Single(_store.FilesTouched("s", CancellationToken.None, Folder, "/home/x"));

        Assert.Equal(new FileTouched("/home/x/a.md", 0, 0, 2, 2), file);
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_home_path_inside_the_folder_is_relative_too()
    {
        Save(Call("t1", "read", path: "~/repo/a.md"), Call("t2", "read", path: "a.md"));

        var file = Assert.Single(_store.FilesTouched("s", CancellationToken.None, "/home/x/repo", "/home/x"));

        Assert.Equal(new FileTouched("a.md", 0, 0, 2, 2), file);
    }

    // Pins: g0-shape#13
    [Theory]
    [InlineData("/work/a_b")]
    [InlineData("/work/100%")]
    public void A_folder_with_a_pattern_character_relativizes(string folder)
    {
        // These match as a LIKE pattern and mustn't: only the folder itself is stripped.
        Save(Call("t1", "read", path: $"{folder}/a.md"), Call("t2", "read", path: "/work/axb/c.md"), Call("t3", "read", path: "/work/100x/d.md"));

        var files = _store.FilesTouched("s", CancellationToken.None, folder);

        Assert.Equal(["/work/100x/d.md", "/work/axb/c.md", "a.md"], Paths(files));
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_folder_name_that_only_starts_the_same_isnt_stripped()
    {
        Save(Call("t1", "read", path: $"{Folder}-other/a.md"));

        Assert.Equal([$"{Folder}-other/a.md"], Paths(Read()));
    }

    // Pins: g0-shape#13
    [Fact]
    public void An_attached_image_lists_with_its_name_unchecked()
    {
        Save(Prompt(""","attached":[{"path":"/gone/one.png","name":"shot.png"}]"""));

        Assert.Equal(new FileTouched("/gone/one.png", 0, 0, 1, 1, "shot.png"), Assert.Single(Read()));
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_prompt_path_that_exists_lists_and_one_that_doesnt_is_dropped()
    {
        var real = Real("a.md");
        Save(Prompt(Named(real, "/gone/b.md")));

        Assert.Equal(new FileTouched("a.md", 0, 0, 1, 1), Assert.Single(Read()));
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_prompt_naming_a_file_twice_counts_once_and_two_prompts_count_two()
    {
        var real = Real("a.png");
        Save(Prompt(Named(real) + $$""","attached":[{"path":"{{real}}","name":"a.png"}]"""), Prompt(Named(real)));

        Assert.Equal(new FileTouched("a.png", 0, 0, 2, 2, "a.png"), Assert.Single(Read()));
    }

    // Pins: g0-shape#13
    [Fact]
    public void The_sessions_own_folder_doesnt_list()
    {
        Save(Call("t1", "read", path: Folder), Prompt(Named(Folder)));

        Assert.Empty(_store.FilesTouched("s", CancellationToken.None, Folder));
    }

    // Pins: g0-shape#13
    [Fact]
    public void Sixty_real_files_give_the_newest_fifty()
    {
        Save(Enumerable.Range(0, 60).Select(i => Call($"t{i}", "edit", path: $"f{i:00}.rs")).ToArray());

        var files = Read();

        Assert.Equal(TouchedFiles.MostFiles, files.Count);
        Assert.Equal(Enumerable.Range(10, 50).Reverse().Select(i => $"f{i:00}.rs"), Paths(files));
    }

    // Pins: g0-shape#13
    [Fact]
    public void Ties_on_seq_sort_by_path()
    {
        Save(Call("t1", "read", path: "b.md", more: Named("a.md", "c.md")));

        Assert.Equal(["a.md", "b.md", "c.md"], Paths(Read()));
    }

    // Pins: g0-shape#13
    [Fact]
    public void Three_hundred_candidates_newer_than_ten_real_files_still_return_the_ten()
    {
        Save(Enumerable.Range(0, 10).Select(i => Call($"e{i}", "edit", path: $"f{i}.rs")).ToArray());
        Save(Enumerable.Range(0, 300).Select(i => Call($"x{i}", "execute", more: Named($"not-a-file-{i}"))).ToArray());

        Assert.Equal(Enumerable.Range(0, 10).Reverse().Select(i => $"f{i}.rs"), Paths(Read()));
    }

    // Pins: g0-shape#13
    [Fact]
    public void Reading_stops_at_the_budget_of_paths()
    {
        Save(Enumerable.Range(0, 10).Select(i => Call($"e{i}", "edit", path: $"f{i}.rs")).ToArray());
        Save(Enumerable.Range(0, TouchedFiles.Budget).Select(i => Call($"x{i}", "execute", more: Named($"not-a-file-{i}"))).ToArray());

        Assert.Empty(Read());
    }

    // Pins: g0-shape#13
    [Fact]
    public void Paging_stops_once_fifty_are_kept()
    {
        var offsets = new List<int>();
        var rows = Enumerable.Range(0, TouchedFiles.Page).Select(i => new TouchedRow($"f{i}", 0, 0, 1, 1, false, null)).ToArray();

        var files = TouchedFiles.Read((_, offset) => { offsets.Add(offset); return rows; }, null, null, CancellationToken.None);

        Assert.Equal(TouchedFiles.MostFiles, files.Count);
        Assert.Equal([0], offsets);
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_cancelled_read_stops_between_files()
    {
        using var cancel = new CancellationTokenSource();
        var probe = new CancellingProbe(cancel);
        var rows = new[] { new TouchedRow("a", 0, 0, 1, 1, false, null), new TouchedRow("b", 0, 0, 1, 1, false, null) };

        Assert.Throws<OperationCanceledException>(() => TouchedFiles.Read((_, _) => rows, "/repo", probe, cancel.Token));
        Assert.Equal(["/repo/a"], probe.Asked);
    }

    // A call written between two page reads moves paths in the order; the pages read up to the seq taken at the start.
    // Pins: g0-shape#13
    [Fact]
    public void A_row_written_between_page_reads_neither_repeats_nor_skips_a_path()
    {
        var names = Enumerable.Range(0, 250).Select(i => $"p{i:000}").ToArray();
        Save(names.Select((name, i) => Call($"x{i}", "execute", more: Named(name))).ToArray());
        var upto = _store.LastSeq("s");
        var read = new List<string>();

        TouchedFiles.Read((limit, offset) =>
        {
            var page = _store.FilesTouchedPage("s", Folder, null, upto, limit, offset, CancellationToken.None);
            read.AddRange(page.Select(row => row.Path));
            // The oldest path, on the second page, is named again, and a new one comes first.
            if (offset == 0) Save(Call("y1", "execute", more: Named("p000")), Call("y2", "execute", more: Named("new")));
            return page;
        }, Folder, new AskedProbe(), CancellationToken.None);

        Assert.Equal(names.OrderBy(name => name, StringComparer.Ordinal), read.Order(StringComparer.Ordinal));
    }

    private sealed class CancellingProbe(CancellationTokenSource cancel) : IPathProbe
    {
        public readonly List<string> Asked = [];
        public bool IsFile(string path) => Ask(path);
        public bool IsFolder(string path) => Ask(path);
        public string Resolve(string path) => path;
        private bool Ask(string path) { Asked.Add(path); cancel.Cancel(); return false; }
    }

    // Pins: g0-shape#13
    [Fact]
    public void Older_rows_with_only_a_path_list()
    {
        Save(("t1", RowKind.Tool, """{"name":"Edit","state":"completed","path":"old.rs","added":1,"removed":2,"hasDetail":false,"kind":"edit"}"""));

        Assert.Equal(new FileTouched("old.rs", 1, 2, 1, 1), Assert.Single(Read()));
    }

    // Pins: g0-shape#13
    [Fact]
    public void No_folder_checks_nothing()
    {
        Directory.CreateDirectory(Path.Combine(Folder, "src"));
        Save(Call("t1", "execute", more: Named("gone.md", Path.Combine(Folder, "src"), "~/kept.md")));

        Assert.Equal([Path.Combine(Folder, "src"), "gone.md", "~/kept.md"], Paths(ReadNoFolder()));
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_folder_and_no_probe_checks_nothing()
    {
        Save(Call("t1", "execute", more: Named("gone.md")));

        Assert.Equal(["gone.md"], Paths(_store.FilesTouched("s", CancellationToken.None, Folder)));
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_probe_with_no_folder_asks_nothing()
    {
        var probe = new AskedProbe();
        Save(Call("t1", "execute", more: Named("gone.md")));

        Assert.Equal(["gone.md"], Paths(_store.FilesTouched("s", CancellationToken.None, probe: probe)));
        Assert.Empty(probe.Asked);
    }

    // Pins: g0-shape#13
    [Fact]
    public void An_attached_image_is_never_asked_about_and_a_relative_path_is_asked_inside_the_folder()
    {
        var probe = new AskedProbe();
        Save(Prompt(""","attached":[{"path":"/gone/one.png","name":"one.png"}]"""), Call("t1", "read", path: "a.md"));

        Assert.Equal(["a.md", "/gone/one.png"], Paths(_store.FilesTouched("s", CancellationToken.None, "/repo", probe: probe)));
        Assert.Equal(["/repo/a.md"], probe.Asked);
    }

    private sealed class AskedProbe : IPathProbe
    {
        public readonly List<string> Asked = [];
        public bool IsFile(string path) { Asked.Add(path); return false; }
        public bool IsFolder(string path) { Asked.Add(path); return false; }
        public string Resolve(string path) => path;
    }

    // Pins: g0-shape#13
    [Fact]
    public void Rows_written_by_the_converter_list_what_the_prompt_and_shell_named()
    {
        Real("notes.md");
        Real("dropped.md");
        var input = JsonDocument.Parse($$"""{"command":"cat notes.md","cwd":"{{Folder}}"}""").RootElement.Clone();
        var converter = new RowConverter();
        AgentEvent[] events =
        [
            new TurnStarted(0, "run", $"see \"{Folder}/dropped.md\"\nAttached image: /pics/one.png (shot.png)"),
            new ToolCallChanged(1, "t", true, new ToolCallFields(Name: "sh", Kind: "execute", Status: "started", Input: input)),
            new ToolCallChanged(2, "t", false, new ToolCallFields(Status: "completed")),
        ];
        foreach (var change in events)
            if (converter.Apply(change) is { Rows.Count: > 0 } batch) _store.Commit("s", batch);

        var files = Read();

        Assert.Equal(1, Assert.Single(files, file => file.Path == "notes.md").Count);
        Assert.Equal("shot.png", Assert.Single(files, file => file.Path == "/pics/one.png").Name);
        Assert.Contains("dropped.md", Paths(files));
        Assert.Equal(3, files.Count);
    }
}
