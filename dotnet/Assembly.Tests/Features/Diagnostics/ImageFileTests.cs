using Assembly.Infrastructure.Diagnostics;
using Assembly.Infrastructure.Http;

using Assembly.Tests.Infrastructure.Http;
using Assembly.Tests.Infrastructure.Remote;
namespace Assembly.Tests.Features.Diagnostics;

public sealed class ImageFileTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private readonly string _dir = Directory.CreateTempSubdirectory("assembly-image-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void An_image_is_read_with_its_type()
    {
        var path = File("a.png", Png);

        var (bytes, mime) = ImageFile.Read(path);

        Assert.Equal(Png, bytes);
        Assert.Equal("image/png", mime);
    }

    [Theory]
    [InlineData(new byte[0], "Image must be between 1 byte and 20 MB")]
    [InlineData(new byte[] { 1, 2, 3, 4 }, "Only PNG, JPEG, GIF, and WebP images are supported")]
    public void A_file_that_is_not_an_image_is_refused(byte[] bytes, string message)
    {
        var error = Assert.Throws<InvalidOperationException>(() => ImageFile.Read(File("x.png", bytes)));

        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void A_folder_is_refused()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ImageFile.Read(_dir));

        Assert.Equal("Only files can be attached", error.Message);
    }
}
