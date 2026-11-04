using Assembly.Application.Common.Machines;

namespace Assembly.Infrastructure.Updates;

/// <summary>
/// What each piece should be, from an <see cref="IUpdateSource"/>. A null value is unknown: the check says so and never
/// calls it behind. <paramref name="BackendBehind"/> decides whether a remote's backend is behind.
/// </summary>
internal sealed record UpdateWanted(string? App, Func<RemoteBackend, bool> BackendBehind, string? Backend,
    IReadOnlyDictionary<string, string?> Adapters, string? Packaging, IReadOnlyDictionary<string, string?> Clis);

/// <summary>The one check, the same in development and in production: only its wanted values differ.</summary>
internal static class UpdateCheck
{
    internal static IReadOnlyList<UpdateItem> Local(string? appVersion, InstalledTools? installed, UpdateWanted wanted) =>
        [new("desktop", "Desktop", appVersion, wanted.App, wanted.App is null ? UpdateItem.Unknown : Newer(appVersion, wanted.App)),
         .. Tools(installed, wanted)];

    internal static IReadOnlyList<UpdateItem> Remote(RemoteBackend? backend, InstalledTools? installed, UpdateWanted wanted) =>
        [new("backend", "Backend", backend?.Version ?? backend?.Commit, wanted.Backend,
             backend is null ? UpdateItem.Unknown : wanted.BackendBehind(backend) ? UpdateItem.Behind : UpdateItem.UpToDate),
         .. Tools(installed, wanted)];

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
