using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Preferences;

namespace Assembly.Infrastructure.Agents;

/// <summary>The utility model, run through the signed-in <paramref name="claude"/> or <paramref name="codex"/> CLI.</summary>
internal sealed class CliUtilityModel : IUtilityModel
{
    public CliUtilityModel(string claude, string codex, PreferenceStore preferences, TimeProvider time, TextWriter log)
    {
    }

    public Task<string?> CompleteAsync(string prompt, CancellationToken cancellationToken) => throw new NotImplementedException();
}
