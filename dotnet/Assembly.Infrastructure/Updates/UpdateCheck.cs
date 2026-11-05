using Assembly.Application.Common.Machines;

namespace Assembly.Infrastructure.Updates;

/// <summary>
/// What each piece should be, from an <see cref="IUpdateSource"/>. A null value is unknown: the check says so and never
/// calls it behind. <paramref name="Backend"/> is the one version number a remote's backend must be on.
/// <paramref name="NeverOlder"/> is a release's rule: a remote newer than this app means the app is what's behind, and
/// putting an older build on a database a newer one may have migrated isn't safe. A checkout builds whichever number it has.
/// </summary>
internal sealed record UpdateWanted(string? App, string? Backend,
    IReadOnlyDictionary<string, string?> Adapters, string? Packaging, IReadOnlyDictionary<string, string?> Clis, bool NeverOlder = false);

/// <summary>The one check, the same in development and in production: only its wanted values differ.</summary>
internal static class UpdateCheck
{
    internal static IReadOnlyList<UpdateItem> Local(string? appVersion, InstalledTools? installed, UpdateWanted wanted) =>
        [new("desktop", "Desktop", appVersion, wanted.App, wanted.App is null ? UpdateItem.Unknown : Newer(appVersion, wanted.App)),
         .. Tools(installed, wanted)];

    internal static IReadOnlyList<UpdateItem> Remote(RemoteBackend? backend, InstalledTools? installed, UpdateWanted wanted) =>
        [Backend(backend, wanted), .. Tools(installed, wanted)];

    private static UpdateItem Backend(RemoteBackend? backend, UpdateWanted wanted)
    {
        if (wanted.NeverOlder && backend?.Version is { } have && wanted.Backend is { } want && CliVersion.Compare(have, want) == "ahead")
            return new("backend", "Backend", have, null, UpdateItem.Unknown);
        return new("backend", "Backend", backend?.Version, wanted.Backend,
            backend is null ? UpdateItem.Unknown : backend.Version != wanted.Backend ? UpdateItem.Behind : UpdateItem.UpToDate);
    }

    private static IEnumerable<UpdateItem> Tools(InstalledTools? installed, UpdateWanted wanted)
    {
        foreach (var (name, want) in wanted.Adapters)
        {
            var have = installed?.Adapters.GetValueOrDefault(name);
            var state = installed is null ? UpdateItem.Unknown
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

    private static string Newer(string? have, string want) => have is null ? UpdateItem.Behind : CliVersion.Compare(have, want) switch
    {
        "update_available" => UpdateItem.Behind,
        "unverified" => UpdateItem.Unknown,
        _ => UpdateItem.UpToDate
    };
}
