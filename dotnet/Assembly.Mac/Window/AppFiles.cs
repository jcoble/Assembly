namespace Assembly.Mac.Window;

/// <summary>
/// The built web app (SvelteKit's static build), served to the window as <c>app://localhost/…</c>. Only files under
/// the build folder are served; any other path, a folder included, gets the app's <c>index.html</c>.
/// </summary>
internal sealed class AppFiles(string root)
{
    public const string Scheme = "app";
    public const string Origin = "app://localhost";

    private readonly string _root = Path.GetFullPath(root) + Path.DirectorySeparatorChar;

    public Stream Open(string url, out string contentType)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/') : "";
        var file = Path.GetFullPath(Path.Combine(_root, path));
        if (!file.StartsWith(_root, StringComparison.Ordinal) || !File.Exists(file))
            file = Path.Combine(_root, "index.html");
        contentType = TypeOf(file);
        return File.OpenRead(file);
    }

    private static string TypeOf(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" => "text/html",
        ".js" => "text/javascript",
        ".css" => "text/css",
        ".json" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream",
    };
}
