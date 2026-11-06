namespace Assembly.Application.Features.Chat.Rows.Context;

/// <summary>What's on this machine's disk at a path, for <c>rows/files</c>: the core opens no files itself.</summary>
public interface IPathProbe
{
    bool IsFile(string path);
    bool IsFolder(string path);

    /// <summary>The whole path with every symlink in it followed, or the path as given when it doesn't exist.</summary>
    string Resolve(string path);
}
