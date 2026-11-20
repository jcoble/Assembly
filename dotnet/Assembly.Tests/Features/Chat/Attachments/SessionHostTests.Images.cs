using Assembly.Application.Common.Images;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Chat.Attachments;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed partial class SessionHostTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_pasted_image_reaches_an_agent_that_takes_images_as_an_image_block_and_others_as_its_path(bool images)
    {
        var root = Path.Combine(_db.Folder, "attachments");
        var store = new AttachmentStore(root);
        byte[] png = [.. ImageType.All[0].Magic, 9, 9];
        var path = await store.SaveAsync(_session.SessionId, ImageType.All[0], png, default);
        var host = new SessionHost(_session.SessionId, _hub, _sessionStore, _opener, _time, _log, _work, images: store);
        var text = $"describe it\nAttached image: {path} (red.png)";

        var sending = host.SendAsync(text, "client-1", null, CancellationToken.None, attachmentIds: [path]);
        var agent = await _launcher.NextAsync();
        await agent.StartAsync(images: images);
        var request = await agent.ReadAsync("session/prompt");
        var prompt = request.GetProperty("params").GetProperty("prompt");

        if (images)
        {
            Assert.Equal(2, prompt.GetArrayLength());
            Assert.Equal("describe it", prompt[0].GetProperty("text").GetString());
            Assert.Equal("image", prompt[1].GetProperty("type").GetString());
            Assert.Equal(Convert.ToBase64String(png), prompt[1].GetProperty("data").GetString());
            Assert.Equal("image/png", prompt[1].GetProperty("mimeType").GetString());
        }
        else
        {
            Assert.Equal(text, Assert.Single(prompt.EnumerateArray()).GetProperty("text").GetString());
        }
        // The saved message keeps the text as sent, so the history still shows the image.
        Assert.Equal(text, _store.RowsAfter(_session.SessionId, 0).First(row => row.Kind == "user").Payload.GetProperty("text").GetString());
        await agent.FinishAsync(request, """{"stopReason":"end_turn"}""");
        await sending.WaitAsync(Wait);
        await agent.CloseAsync();
    }
}
