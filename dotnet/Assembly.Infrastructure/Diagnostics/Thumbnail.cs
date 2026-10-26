using System.Runtime.InteropServices;

using Assembly.Application.Features.Diagnostics;
using Assembly.Infrastructure.Http;
namespace Assembly.Infrastructure.Diagnostics;

/// <summary>
/// Image thumbnails through macOS ImageIO, with the Tauri app's limits (from Assembly): a PNG, JPEG, GIF or WebP of
/// at most 12000 px a side and 50 million pixels, scaled to 360 px on its long edge. ImageIO can't write WebP, so the
/// thumbnail is a JPEG, or a PNG when the image has alpha.
/// </summary>
internal static class Thumbnail
{
    private const int MaxEdge = 12_000;
    private const long MaxPixels = 50_000_000;
    private const int ThumbnailEdge = 360;

    public static (byte[] Bytes, string Mime) Make(byte[] image)
    {
        if (ImageFile.MimeOf(image) is null)
            throw new InvalidOperationException("Only PNG, JPEG, GIF, and WebP images are supported");
        var cf = new List<IntPtr>();
        try
        {
            var data = Keep(cf, CFDataCreate(IntPtr.Zero, image, image.Length));
            var source = Keep(cf, CGImageSourceCreateWithData(data, IntPtr.Zero));
            if (source == IntPtr.Zero || CGImageSourceGetCount(source) == 0)
                throw new InvalidOperationException("Could not read the image");
            var properties = Keep(cf, CGImageSourceCopyPropertiesAtIndex(source, 0, IntPtr.Zero));
            var (width, height) = (Int(properties, "kCGImagePropertyPixelWidth"), Int(properties, "kCGImagePropertyPixelHeight"));
            if (width is <= 0 or > MaxEdge || height is <= 0 or > MaxEdge)
                throw new InvalidOperationException($"Image dimensions must be between 1x1 and {MaxEdge}x{MaxEdge}");
            if ((long)width * height > MaxPixels)
                throw new InvalidOperationException("Image is too large to thumbnail safely");

            var options = Keep(cf, Dictionary(
                ("kCGImageSourceCreateThumbnailFromImageAlways", Constant("kCFBooleanTrue", CoreFoundation)),
                ("kCGImageSourceCreateThumbnailWithTransform", Constant("kCFBooleanTrue", CoreFoundation)),
                ("kCGImageSourceThumbnailMaxPixelSize", Keep(cf, Number(ThumbnailEdge)))));
            var thumbnail = Keep(cf, CGImageSourceCreateThumbnailAtIndex(source, 0, options));
            if (thumbnail == IntPtr.Zero)
                throw new InvalidOperationException("Could not make the thumbnail");

            var alpha = CGImageGetAlphaInfo(thumbnail) is not (0 or 5 or 6); // None, NoneSkipLast, NoneSkipFirst
            var output = Keep(cf, CFDataCreateMutable(IntPtr.Zero, 0));
            var destination = Keep(cf, CGImageDestinationCreateWithData(output, Keep(cf, Text(alpha ? "public.png" : "public.jpeg")), 1, IntPtr.Zero));
            var quality = alpha ? IntPtr.Zero : Keep(cf, Dictionary(("kCGImageDestinationLossyCompressionQuality", Keep(cf, Number(0.8)))));
            CGImageDestinationAddImage(destination, thumbnail, quality);
            if (!CGImageDestinationFinalize(destination))
                throw new InvalidOperationException("Could not encode the thumbnail");
            var bytes = new byte[CFDataGetLength(output)];
            Marshal.Copy(CFDataGetBytePtr(output), bytes, 0, bytes.Length);
            return (bytes, alpha ? "image/png" : "image/jpeg");
        }
        finally
        {
            foreach (var handle in cf)
                if (handle != IntPtr.Zero)
                    CFRelease(handle);
        }
    }

    private static IntPtr Keep(List<IntPtr> cf, IntPtr handle)
    {
        cf.Add(handle);
        return handle;
    }

    private static int Int(IntPtr dictionary, string key)
    {
        var number = CFDictionaryGetValue(dictionary, Constant(key, ImageIO));
        return number != IntPtr.Zero && CFNumberGetValue(number, 9 /* kCFNumberIntType */, out int value) ? value : 0;
    }

    private static IntPtr Number(int value) => CFNumberCreate(IntPtr.Zero, 9 /* kCFNumberIntType */, ref value);

    private static IntPtr Number(double value) => CFNumberCreate(IntPtr.Zero, 13 /* kCFNumberDoubleType */, ref value);

    private static IntPtr Text(string text) => CFStringCreateWithCString(IntPtr.Zero, text, 0x08000100 /* UTF-8 */);

    private static IntPtr Dictionary(params (string Key, IntPtr Value)[] entries)
    {
        var keys = entries.Select(e => Constant(e.Key, ImageIO)).ToArray();
        var values = entries.Select(e => e.Value).ToArray();
        return CFDictionaryCreate(IntPtr.Zero, keys, values, entries.Length,
            Symbol("kCFTypeDictionaryKeyCallBacks", CoreFoundation), Symbol("kCFTypeDictionaryValueCallBacks", CoreFoundation));
    }

    // An exported CFStringRef or CFBooleanRef constant's value.
    private static IntPtr Constant(string name, string library) => Marshal.ReadIntPtr(Symbol(name, library));

    private static IntPtr Symbol(string name, string library) => NativeLibrary.GetExport(NativeLibrary.Load(library), name);

    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ImageIO = "/System/Library/Frameworks/ImageIO.framework/ImageIO";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDataCreateMutable(IntPtr allocator, nint capacity);

    [DllImport(CoreFoundation)]
    private static extern nint CFDataGetLength(IntPtr data);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDataGetBytePtr(IntPtr data);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallBacks, IntPtr valueCallBacks);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFNumberCreate(IntPtr allocator, nint type, ref int value);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFNumberCreate(IntPtr allocator, nint type, ref double value);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CFNumberGetValue(IntPtr number, nint type, out int value);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr handle);

    [DllImport(ImageIO)]
    private static extern IntPtr CGImageSourceCreateWithData(IntPtr data, IntPtr options);

    [DllImport(ImageIO)]
    private static extern nint CGImageSourceGetCount(IntPtr source);

    [DllImport(ImageIO)]
    private static extern IntPtr CGImageSourceCopyPropertiesAtIndex(IntPtr source, nint index, IntPtr options);

    [DllImport(ImageIO)]
    private static extern IntPtr CGImageSourceCreateThumbnailAtIndex(IntPtr source, nint index, IntPtr options);

    [DllImport(ImageIO)]
    private static extern IntPtr CGImageDestinationCreateWithData(IntPtr data, IntPtr type, nint count, IntPtr options);

    [DllImport(ImageIO)]
    private static extern void CGImageDestinationAddImage(IntPtr destination, IntPtr image, IntPtr properties);

    [DllImport(ImageIO)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CGImageDestinationFinalize(IntPtr destination);

    [DllImport(CoreGraphics)]
    private static extern uint CGImageGetAlphaInfo(IntPtr image);
}
