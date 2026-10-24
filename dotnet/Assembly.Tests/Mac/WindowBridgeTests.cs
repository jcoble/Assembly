using Assembly.Mac.Window;

namespace Assembly.Tests.Mac;

public sealed class WindowBridgeTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private readonly string _dir = Directory.CreateTempSubdirectory("assembly bridge ").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Attach_serves_a_chosen_image_by_its_escaped_path_and_nothing_else()
    {
        var bridge = new WindowBridge("t", _dir, new Uri("http://127.0.0.1:1"), () => []);
        var image = Path.Combine(_dir, "Screen Shot (2).png");
        File.WriteAllBytes(image, Png);
        var url = $"attach://localhost/{Uri.EscapeDataString(image)}";

        using var refused = bridge.OpenChosen(url, out var refusedType);
        bridge.Chosen.Add([image]);
        using var served = bridge.OpenChosen(url, out var servedType);

        Assert.Equal((0L, "text/plain"), (refused.Length, refusedType));
        Assert.Equal("image/png", servedType);
        Assert.Equal(Png, ((MemoryStream)served).ToArray());
    }
}
