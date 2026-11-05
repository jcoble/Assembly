namespace Assembly.DevSeed;

/// <summary>Everything the seed makes. All of it is invented: three made-up projects and the work done on them.</summary>
public static partial class SeedScenario
{
    public static IReadOnlyList<SeedRepo> Projects { get; } = [HarborApi, LumenUi, TidelineCli];

    public static IReadOnlyList<SeedChat> Sessions() => [.. HarborChats(), .. LumenChats(), .. TidelineChats()];

    public static IReadOnlyList<SeedRun> Runs { get; } =
    [
        new("retry-shipped", "harbor-retry", "harbor-api", ShipFeature, "Ship webhook retry backoff", "succeeded", 400,
            new Dictionary<string, string> { ["build"] = "succeeded", ["checks"] = "succeeded", ["review"] = "succeeded", ["approve"] = "succeeded" }),
        new("rate-limit-review", "harbor-rate-limit", "harbor-api", ShipFeature, "Ship per-tenant rate limits", "needs_me", 35,
            new Dictionary<string, string> { ["build"] = "succeeded", ["checks"] = "succeeded", ["review"] = "succeeded" }, OpenGate: true),
        new("signature-failed", "harbor-signature", "harbor-api", ShipFeature, "Ship signature rotation", "failed", 180,
            new Dictionary<string, string> { ["build"] = "succeeded", ["checks"] = "failed", ["review"] = "skipped", ["approve"] = "skipped" }, "npm test exited 1: 2 failures in webhook-signature.test.ts"),
        new("otel-cancelled", "harbor-otel", "harbor-api", ShipFeature, "Ship OpenTelemetry spike", "cancelled", 900,
            new Dictionary<string, string> { ["build"] = "cancelled", ["checks"] = "cancelled", ["review"] = "cancelled", ["approve"] = "cancelled" }),
        new("tokens-running", "lumen-tokens", "lumen-ui", Audit, "Audit theme token usage", "running", 12,
            new Dictionary<string, string> { ["scan"] = "succeeded" }),
        new("tideline-release", "tideline-release", "tideline-cli", Audit, "Audit release checklist", "succeeded", 1500,
            new Dictionary<string, string> { ["scan"] = "succeeded", ["report"] = "succeeded" }),
    ];

    internal const string ShipFeature = ".assembly/workflows/ship-feature.yml";
    internal const string Audit = ".assembly/workflows/audit.yml";

    internal static Dictionary<string, string> Files(params (string Path, string Text)[] files) =>
        files.ToDictionary(file => file.Path, file => file.Text);

    internal static SeedCommit Commit(string message, double daysAgo, params (string Path, string Text)[] files) => new(message, daysAgo, Files(files));
}
