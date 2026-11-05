using Assembly.Application.Features.Chat.Rows.Context;

namespace Assembly.Infrastructure.Chat;

/// <summary>Answers <see cref="IPathProbe"/> from this machine's disk.</summary>
public sealed class DiskPathProbe : IPathProbe
{
    public bool IsFile(string path) => File.Exists(path);
    public bool IsFolder(string path) => Directory.Exists(path);
}
