using Assembly.Mac.Window;

namespace Assembly.Tests.Mac;

public sealed class ChosenImagesTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private readonly string _dir = Directory.CreateTempSubdirectory("assembly-chosen-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void A_chosen_image_is_read_with_its_type()
    {
        var chosen = new ChosenImages();
        var path = File("a.png", Png);
        chosen.Add([path]);

        var (bytes, mime) = chosen.Read(path);

        Assert.Equal(Png, bytes);
        Assert.Equal("image/png", mime);
    }

    [Fact]
    public void A_path_not_chosen_is_refused()
    {
        var path = File("a.png", Png);

        var error = Assert.Throws<InvalidOperationException>(() => new ChosenImages().Read(path));

        Assert.Equal("Only images you picked or dropped can be attached", error.Message);
    }

    [Fact]
    public void Discard_forgets_one_copy_of_a_path_chosen_twice()
    {
        var chosen = new ChosenImages();
        var path = File("a.png", Png);
        chosen.Add([path, path]);

        chosen.Forget(path);
        chosen.Read(path);
        chosen.Forget(path);

        Assert.Throws<InvalidOperationException>(() => chosen.Read(path));
    }

    [Theory]
    [InlineData(new byte[0], "Image must be between 1 byte and 20 MB")]
    [InlineData(new byte[] { 1, 2, 3, 4 }, "Only PNG, JPEG, GIF, and WebP images are supported")]
    public void A_chosen_file_that_is_not_an_image_is_refused(byte[] bytes, string message)
    {
        var chosen = new ChosenImages();
        var path = File("x.png", bytes);
        chosen.Add([path]);

        var error = Assert.Throws<InvalidOperationException>(() => chosen.Read(path));

        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void A_chosen_folder_is_refused()
    {
        var chosen = new ChosenImages();
        chosen.Add([_dir]);

        var error = Assert.Throws<InvalidOperationException>(() => chosen.Read(_dir));

        Assert.Equal("Only files can be attached", error.Message);
    }
}
