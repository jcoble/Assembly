using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Images;
using Assembly.Application.Features.Chat.Attachments;
using Assembly.Infrastructure.Chat.Attachments;

namespace Assembly.Tests.Features.Chat.Attachments;

public sealed class PromptContentTests : IDisposable
{
    private const string Session = "session";
    private static readonly byte[] Png = [.. ImageType.All[0].Magic, 1, 2, 3, 4];
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"assembly-prompt-content-{Guid.NewGuid():N}");
    private readonly AttachmentStore _store;

    public PromptContentTests() => _store = new AttachmentStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static string Line(string path, string name = "red.png") => $"Attached image: {path} ({name})";

    [Fact]
    public async Task An_image_attachment_goes_as_an_image_block_and_its_line_is_dropped()
    {
        var path = await _store.SaveAsync(Session, ImageType.All[0], Png, default);

        var blocks = await PromptContent.BuildAsync(_store, Session, $"what is this?\n{Line(path)}", [path], acceptsImages: true, default);

        Assert.Equal([TextBlock.Of("what is this?"), new ContentBlock("image", Data: Convert.ToBase64String(Png), MimeType: "image/png")], blocks);
    }

    [Fact]
    public async Task An_image_alone_goes_as_just_its_block()
    {
        var path = await _store.SaveAsync(Session, ImageType.All[0], Png, default);

        var blocks = await PromptContent.BuildAsync(_store, Session, Line(path), [path], acceptsImages: true, default);

        Assert.Equal("image", Assert.Single(blocks).Type);
    }

    [Fact]
    public async Task An_agent_without_image_prompts_gets_the_path_to_read()
    {
        var path = await _store.SaveAsync(Session, ImageType.All[0], Png, default);
        var text = $"what is this?\n{Line(path)}";

        var blocks = await PromptContent.BuildAsync(_store, Session, text, [path], acceptsImages: false, default);

        Assert.Equal([TextBlock.Of(text)], blocks);
    }

    [Fact]
    public async Task A_file_that_is_not_an_image_or_not_this_sessions_keeps_its_line_unchanged()
    {
        var folder = Path.Combine(_root, Session);
        Directory.CreateDirectory(folder);
        var notImage = Path.Combine(folder, $"{Guid.NewGuid()}.png");
        await File.WriteAllTextAsync(notImage, "plain text, named like a picture");
        var other = await _store.SaveAsync("other-session", ImageType.All[0], Png, default);
        var text = $"look\n{Line(notImage, "notes.png")}\n{Line(other)}";

        var blocks = await PromptContent.BuildAsync(_store, Session, text, [notImage, other], acceptsImages: true, default);

        Assert.Equal([TextBlock.Of(text)], blocks);
    }

    [Fact]
    public async Task An_image_past_the_prompt_limit_keeps_its_path_and_the_rest_still_go()
    {
        var images = new SizedImages((int)(PromptContent.MostImageBytes * 3 / 4));
        var text = $"two\n{Line("/a/first.png")}\n{Line("/a/second.png")}";

        var blocks = await PromptContent.BuildAsync(images, Session, text, ["/a/first.png", "/a/second.png"], acceptsImages: true, default);

        Assert.Equal(TextBlock.Of($"two\n{Line("/a/second.png")}"), blocks[0]);
        Assert.Equal("image", Assert.Single(blocks.Skip(1)).Type);
    }

    [Fact]
    public async Task A_line_the_prompt_does_not_attach_is_never_read()
    {
        var images = new SizedImages(4);
        var text = $"look\n{Line("/elsewhere/picture.png")}";

        var blocks = await PromptContent.BuildAsync(images, Session, text, ["/a/attached.png"], acceptsImages: true, default);

        Assert.Equal([TextBlock.Of(text)], blocks);
        Assert.Equal(0, images.Reads);
    }

    private sealed class SizedImages(int size) : IPromptImages
    {
        public int Reads { get; private set; }

        public Task<PromptImage?> ReadAsync(string sessionId, string reference, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<PromptImage?>(new PromptImage(new byte[size], "image/png"));
        }
    }
}
