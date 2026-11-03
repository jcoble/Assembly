using System.Text.Json.Serialization;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;

namespace Assembly.Application.Common.Machines;

// Private profile authority only: none of these records is part of SavedMachine or its public topic.
internal sealed record MachineProfileSnapshot(SavedMachine Profile, string Generation);
internal sealed record RemoteCliTargetIdentity(string ProviderId, string VendorFamily, string AccountContext, string ConfigurationTargetId);
internal sealed record RemoteCliProfile(MachineProfileSnapshot Profile, RemoteCliLocator Locator);
internal sealed record RemoteCliLocator(string MachineId, string InstallationId, string CaptureFile,
    string HelperExecutable, IReadOnlyList<string> HelperArguments, string HelperWorkingDirectory,
    IReadOnlyList<RemoteCliTargetIdentity> Targets)
{
    internal void Validate()
    {
        if (!Identifier(MachineId) || !Guid.TryParseExact(InstallationId, "N", out _) ||
            !Absolute(CaptureFile) || !Absolute(HelperExecutable) || !Absolute(HelperWorkingDirectory) ||
            HelperArguments is null || HelperArguments.Count > 1 || HelperArguments.Any(argument =>
                !Absolute(argument) || !argument.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) ||
            Targets is null || Targets.Any(target => target is null || !Identifier(target.ProviderId) ||
                !Identifier(target.VendorFamily) || !AccountContext.IsValid(target.AccountContext) ||
                target.ConfigurationTargetId is null || target.ConfigurationTargetId.Length != 64 ||
                target.ConfigurationTargetId.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) ||
            Targets.Select(target => (target.ProviderId, target.AccountContext, target.ConfigurationTargetId)).Distinct().Count() != Targets.Count)
            throw new CallFailed("CLI configuration unavailable.", "configuration_unavailable");
    }

    private static bool Identifier(string value) => !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
    private static bool Absolute(string value) => !string.IsNullOrEmpty(value) && !value.Any(char.IsControl) && Path.IsPathFullyQualified(value);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(RemoteCliLocator))]
internal sealed partial class RemoteCliLocatorJson : JsonSerializerContext;
