using Assembly.Application.Common.Engine;

namespace Assembly.Application.Common.Agents;

public static class NativeLaunchContext
{
    public static NativeSessionLaunch? Capture(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> environment,
        string binding, string acp, string folder)
    {
        var variable = AccountContext.ConfigHomeVariable(descriptor.VendorFamily);
        if (variable is null) return null;
        var home = environment.GetValueOrDefault(variable);
        if (string.IsNullOrEmpty(home))
        {
            var user = environment.GetValueOrDefault(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
            if (string.IsNullOrEmpty(user) || !Path.IsPathFullyQualified(user)) return null;
            home = Path.Combine(user, AccountContext.DefaultHomeFolder(descriptor.VendorFamily));
        }
        if (!Path.IsPathFullyQualified(home)) return null;
        return new(descriptor.Id, descriptor.VendorFamily!, AccountContext.Of(descriptor, environment.GetValueOrDefault),
            binding, Path.GetFullPath(home), acp, folder);
    }
}
