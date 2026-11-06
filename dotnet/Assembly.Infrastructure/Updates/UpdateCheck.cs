using Assembly.Application.Common.Machines;

namespace Assembly.Infrastructure.Updates;

/// <summary>
/// What each piece should be, from an <see cref="IUpdateSource"/>. A null value is unknown: the check says so and never
/// calls it behind. <paramref name="Backend"/> is the one version number a remote's backend is measured against.
/// </summary>
internal sealed record UpdateWanted(string? App, string? Backend,
    IReadOnlyDictionary<string, string?> Adapters, string? Packaging, IReadOnlyDictionary<string, string?> Clis);

/// <summary>
/// The one check, the same in development and in production: only its wanted values differ. Whichever side has the lower
/// version updates, and nothing older ever goes over something newer: what's ahead of its wanted value says so.
/// </summary>
internal static class UpdateCheck
{
    internal static IReadOnlyList<UpdateItem> Local(string? appVersion, InstalledTools? installed, UpdateWanted wanted) =>
        [new("desktop", "Desktop", appVersion, wanted.App, wanted.App is null ? UpdateItem.Unknown : Newer(appVersion, wanted.App)),
         .. Tools(installed, wanted)];

    internal static IReadOnlyList<UpdateItem> Remote(RemoteBackend? backend, InstalledTools? installed, UpdateWanted wanted) =>
        [Backend(backend, wanted), .. Tools(installed, wanted)];

    // A host that answers with no number is from before there were numbers.
    private static UpdateItem Backend(RemoteBackend? backend, UpdateWanted wanted) =>
        new("backend", "Backend", backend?.Version, wanted.Backend,
            backend is null || wanted.Backend is null ? UpdateItem.Unknown : Newer(backend.Version, wanted.Backend));

    private static IEnumerable<UpdateItem> Tools(InstalledTools? installed, UpdateWanted wanted)
    {
        // Adapters install as one bundle, so one that's ahead holds the bundle back: the rest would come with an older one.
        var bundleAhead = wanted.Adapters.Any(pair => pair.Value is { } want && installed?.Adapters.GetValueOrDefault(pair.Key) is { } have && Newer(have, want) == UpdateItem.Newer);
        foreach (var (name, want) in wanted.Adapters)
        {
            var have = installed?.Adapters.GetValueOrDefault(name);
            var state = installed is null ? UpdateItem.Unknown
                : bundleAhead ? Held(have, want)
                : have is null ? UpdateItem.Behind
                : wanted.Packaging is not null && installed.Packaging != wanted.Packaging ? UpdateItem.Behind
                : want is null ? UpdateItem.Unknown : Newer(have, want);
            yield return new("adapter", name, have, want, state);
        }
        foreach (var cli in installed?.Clis ?? [])
        {
            var want = wanted.Clis.GetValueOrDefault(cli.Family);
            yield return new("cli", cli.Family, cli.Version, want, cli.Version is null || want is null ? UpdateItem.Unknown : Newer(cli.Version, want));
        }
    }

    private static string Held(string? have, string? want)
    {
        var state = have is null || want is null ? UpdateItem.Unknown : Newer(have, want);
        return state == UpdateItem.Behind ? UpdateItem.Unknown : state;
    }

    private static string Newer(string? have, string want) => have is null ? UpdateItem.Behind : have == want ? UpdateItem.UpToDate : CliVersion.Compare(have, want) switch
    {
        "update_available" => UpdateItem.Behind,
        "ahead" => UpdateItem.Newer,
        "unverified" => UpdateItem.Unknown,
        _ => UpdateItem.UpToDate
    };
}
