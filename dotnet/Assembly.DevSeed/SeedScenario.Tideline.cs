namespace Assembly.DevSeed;

public static partial class SeedScenario
{
    private static SeedRepo TidelineCli => new("tideline-cli", "Tideline CLI",
    [
        Commit("Start the CLI", 40,
            ("README.md", "# Tideline\n\nA small command line for tide predictions.\n"),
            ("go.mod", "module example.invalid/tideline\n\ngo 1.25\n"),
            ("main.go", "package main\n\nimport \"fmt\"\n\nfunc main() {\n\tfmt.Println(next(\"Monterey\"))\n}\n"),
            ("tides.go", "package main\n\nfunc next(station string) string {\n\treturn station + \": high tide at 06:12\"\n}\n")),
        Commit("Add the audit workflow", 10,
            (".assembly/workflows/audit.yml", AuditYaml + "\n"),
            (".assembly/workflows/prompts/report.md", "Write up what the scan found: ${{ inputs.request }}\n")),
        Commit("Read the station from the arguments", 5,
            ("main.go", "package main\n\nimport (\n\t\"fmt\"\n\t\"os\"\n)\n\nfunc main() {\n\tfmt.Println(next(os.Args[1]))\n}\n")),
    ],
    [
        new("release/1.4", true, [Commit("Prepare 1.4", 1.5, ("CHANGELOG.md", "# 1.4\n\n- Station as an argument\n"))]),
        new("feature/json-output", false, [Commit("Add --json", 2, ("output.go", "package main\n\nimport \"encoding/json\"\n\nfunc asJSON(v any) string {\n\tb, _ := json.Marshal(v)\n\treturn string(b)\n}\n"))]),
    ],
        Staged: Files(("CONTRIBUTING.md", "# Contributing\n\nRun `go test ./...` first.\n")),
        Modified: Files(("tides.go", "package main\n\n// next returns the next high tide at a station.\nfunc next(station string) string {\n\treturn station + \": high tide at 06:12\"\n}\n")));

    private static IEnumerable<SeedChat> TidelineChats()
    {
        yield return new SeedChat("tideline-release", "codex", "tideline-cli", "Cut the 1.4 release").On("release/1.4").Ago(1480).Is(SeedStatus.Settled).Costing("0.42")
            .Turn("Get 1.4 ready: changelog and a version bump.")
            .Run("git log --oneline v1.3..HEAD", "2f7a1c0 Read the station from the arguments\n81bd4e2 Add the audit workflow\n")
            .Edit("CHANGELOG.md", """
                --- /dev/null
                +++ b/CHANGELOG.md
                @@ -0,0 +1,3 @@
                +# 1.4
                +
                +- Station as an argument
                """)
            .Approved("Tag the release?", "git tag v1.4.0", "")
            .Say("Tagged `v1.4.0`, not pushed.")
            .End(inputTokens: 14_200, outputTokens: 880, costUsd: 0.42m);

        yield return new SeedChat("tideline-panic", "claude", "tideline-cli", "Panic with no arguments").Ago(70).Is(SeedStatus.Done).MarkUnread()
            .Turn("`tideline` with no station panics with an index out of range.")
            .Run("go run .", "panic: runtime error: index out of range [1] with length 1\n\ngoroutine 1 [running]:\nmain.main()\n\t/work/main.go:9 +0x9c\nexit status 2\n", failed: true)
            .Edit("main.go", """
                --- a/main.go
                +++ b/main.go
                @@ -6,5 +6,9 @@
                 func main() {
                +	if len(os.Args) < 2 {
                +		fmt.Fprintln(os.Stderr, "usage: tideline <station>")
                +		os.Exit(2)
                +	}
                 	fmt.Println(next(os.Args[1]))
                 }
                """)
            .Run("go run .", "usage: tideline <station>\nexit status 2\n")
            .Say("Fixed. It prints usage and exits 2.")
            .End();

        yield return new SeedChat("tideline-json", "codex", "tideline-cli", "JSON output").Ago(2900).Is(SeedStatus.Done)
            .Turn("Add a `--json` flag for scripts.")
            .Edit("output.go", """
                --- /dev/null
                +++ b/output.go
                @@ -0,0 +1,8 @@
                +package main
                +
                +import "encoding/json"
                +
                +func asJSON(v any) string {
                +	b, _ := json.Marshal(v)
                +	return string(b)
                +}
                """)
            .Run("go vet ./...", "")
            .Say("Added `asJSON`, on `feature/json-output`. The flag itself isn't wired up yet because `next` returns a string; it should return a struct first.")
            .End();

        yield return new SeedChat("tideline-timezones", "claude", "tideline-cli", "Tide times are in UTC").Ago(320).Is(SeedStatus.Done)
            .Turn("Users read 06:12 as local time and it's UTC.")
            .Think("Stations have fixed locations, so the right zone is the station's rather than the machine's. A user checking a trip wants the time at the place.")
            .Say("I'd show **the station's** local time and name the zone: `06:12 PDT`. Each station's zone would ship in the data file. Shall I?")
            .End();

        yield return new SeedChat("tideline-homebrew", "codex", "tideline-cli", "Homebrew formula").Ago(7200).Is(SeedStatus.Ended)
            .Turn("Draft a Homebrew formula for it.")
            .Say("```ruby\nclass Tideline < Formula\n  desc \"Tide predictions on the command line\"\n  url \"https://example.invalid/tideline-1.4.0.tar.gz\"\n  depends_on \"go\" => :build\n\n  def install\n    system \"go\", \"build\", *std_go_args\n  end\nend\n```\n\nThe `sha256` line needs the real tarball.")
            .End(inputTokens: 5_100, outputTokens: 340, costUsd: 0.03m);

        yield return new SeedChat("tideline-benchmark", "claude", "tideline-cli", "Is the station lookup slow?").Ago(1120).Is(SeedStatus.Settled)
            .Turn("Someone says lookups feel slow with the full station list.")
            .Run("go test -bench=Lookup -benchtime=2s", "BenchmarkLookup-8   41234567   51.2 ns/op\nPASS\n")
            .Say("51 ns per lookup: it isn't the lookup. If it feels slow, look at startup, which parses the whole file.")
            .End();

        yield return new SeedChat("tideline-docs", "codex", "tideline-cli", "Document the data format").Ago(160).Is(SeedStatus.Working)
            .Turn("Write down the station file format before I forget it.")
            .Read("README.md", "# Tideline\n\nA small command line for tide predictions.\n")
            .Say("One question first: is the third column feet or metres? The code doesn't say, and I'd rather not guess in a doc.")
            .End();

        yield return new SeedChat("tideline-ci", "claude", "tideline-cli", "CI on pull requests").Ago(25).Is(SeedStatus.Working).MarkUnread()
            .Turn("There's no CI at all. Add the smallest thing that runs the tests.")
            .Edit(".github/workflows/test.yml", """
                --- /dev/null
                +++ b/.github/workflows/test.yml
                @@ -0,0 +1,8 @@
                +on: pull_request
                +jobs:
                +  test:
                +    runs-on: ubuntu-latest
                +    steps:
                +      - uses: actions/checkout@v4
                +      - uses: actions/setup-go@v5
                +      - run: go test ./...
                """)
            .Waiting("Commit and open a pull request?", "git commit -am 'Run the tests on pull requests' && gh pr create --fill")
            .End();
    }
}
