using System.Runtime.InteropServices;

namespace Assembly.Mac.Window;

/// <summary>
/// The paths of the files and folders in the last drag, read from the drag pasteboard. WKWebView hands the page a
/// dropped file's contents but never its path; the pasteboard still holds the file URLs after the drop, so the page
/// asks for them (<c>drop.paths</c>) when it sees one.
/// </summary>
internal static class DropPaths
{
    public static IReadOnlyList<string> Read()
    {
        // NSPasteboardNameDrag.
        var pasteboard = Send(Class("NSPasteboard"), Selector("pasteboardWithName:"), String("Apple CFPasteboard drag"));
        var items = Send(pasteboard, Selector("pasteboardItems"));
        var fileUrl = String("public.file-url");
        var paths = new List<string>();
        for (nuint i = 0, count = SendCount(items, Selector("count")); i < count; i++)
        {
            var url = Send(Send(items, Selector("objectAtIndex:"), (IntPtr)i), Selector("stringForType:"), fileUrl);
            if (url != IntPtr.Zero && PathOf(url) is { } path)
                paths.Add(path);
        }
        return paths;
    }

    /// <summary>
    /// The path a <c>public.file-url</c> string names. Finder's are file reference URLs (<c>file:///.file/id=…</c>),
    /// which only NSURL's <c>filePathURL</c> turns into a path.
    /// </summary>
    internal static string? PathOf(IntPtr urlString)
    {
        var url = Send(Class("NSURL"), Selector("URLWithString:"), urlString);
        var pathUrl = url == IntPtr.Zero ? IntPtr.Zero : Send(url, Selector("filePathURL"));
        var text = pathUrl != IntPtr.Zero
            ? Marshal.PtrToStringUTF8(Send(Send(pathUrl, Selector("absoluteString")), Selector("UTF8String")))
            : Marshal.PtrToStringUTF8(Send(urlString, Selector("UTF8String")));
        return text is not null && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.IsFile
            ? uri.LocalPath.TrimEnd('/') is { Length: > 0 } path ? path : "/"
            : null;
    }

    private static IntPtr String(string text) => Send(Class("NSString"), Selector("stringWithUTF8String:"), text);
    private static IntPtr Class(string name) => objc_getClass(name);
    private static IntPtr Selector(string name) => sel_registerName(name);

    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjC)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nuint SendCount(IntPtr receiver, IntPtr selector);
}
