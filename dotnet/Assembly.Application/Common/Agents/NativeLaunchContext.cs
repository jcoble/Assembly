using Assembly.Application.Common.Engine;

namespace Assembly.Application.Common.Agents;

public static class NativeLaunchContext
{
    public static NativeSessionLaunch? Capture(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> environment,
        string binding, string acp, string folder)
    {
        var home = AccountContext.ConfigHome(descriptor.VendorFamily, environment);
        if (home is null) return null;
        return new(descriptor.Id, descriptor.VendorFamily!, AccountContext.Of(descriptor, environment.GetValueOrDefault),
            binding, home, acp, folder);
    }
}
