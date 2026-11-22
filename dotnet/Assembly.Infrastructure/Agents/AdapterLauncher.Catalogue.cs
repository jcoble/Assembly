using System.Security.Cryptography;
using System.Text;
using Assembly.Application.Common.Agents;

namespace Assembly.Infrastructure.Agents;

public sealed partial class AdapterLauncher
{
    public string? CatalogueRevision(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> environment, string cwd)
    {
        var context = NativeLaunchContext.Capture(descriptor, environment, "", "", cwd);
        if (context is null) return null;
        var home = context.ProviderHome;
        var user = environment.GetValueOrDefault(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
        var entry = descriptor.Args.FirstOrDefault();
        using var bytes = new MemoryStream();
        using var revision = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
        revision.Write("composer-catalogue-v1");
        revision.Write(home);
        revision.Write(descriptor.Cli ?? "");
        revision.Write(environment.GetValueOrDefault("PATH") ?? "");

        try
        {
            Stamp(descriptor.Command);
            Stamp(descriptor.Cli);
            Stamp(entry);
            if (entry is not null)
                Stamp(Path.Combine(Path.GetDirectoryName(entry) ?? "", "..", "package.json"));

            // Only stat the provider files. Credential contents never enter the cache key or database.
            var files = AccountContext.CatalogueFiles(context.VendorFamily);
            foreach (var file in files.Profile)
                Stamp(Path.Combine(home, file));
            if (user is not null && files.User is not null)
                Stamp(Path.Combine(user, files.User));
            for (var folder = new DirectoryInfo(cwd); folder is not null; folder = folder.Parent)
                foreach (var file in files.Project)
                    Stamp(Path.Combine(folder.FullName, file));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        revision.Flush();
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));

        void Stamp(string? path)
        {
            if (path is null || !Path.IsPathFullyQualified(path)) return;
            var file = new FileInfo(path);
            revision.Write(file.FullName);
            revision.Write(file.Exists);
            if (!file.Exists) return;
            revision.Write(file.LastWriteTimeUtc.Ticks);
            revision.Write(file.CreationTimeUtc.Ticks);
            revision.Write(file.Length);
        }
    }
}
