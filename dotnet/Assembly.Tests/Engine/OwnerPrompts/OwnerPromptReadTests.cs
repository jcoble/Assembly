using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Engine;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Reading;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Socket;

namespace Assembly.Tests.Engine.OwnerPrompts;

public sealed class OwnerPromptReadTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(7L)]
    public void Reopen_reads_complete_bytes_and_replays_original_nullable_request(long? target)
    {
        using var f = new EngineFixture(); f.SeedSession(state: "running", epoch: 7);
        var text = new string('x', 1024 * 1024 - 1) + (target is null ? "😀\0" : "\0😀") + "\"\\";
        var command = new SendOwnerPrompt("s", text, "C", ["first\"😀", "second\\"], target);
        var prepared = OwnerPromptFingerprint.Prepare(f.Machine, command, default);
        var original = Accept(f, prepared);
        Assert.True(original.Applied, original.Error); f.Reopen();
        var reader = new EngineReader(f.Db, f.Machine);
        var page = reader.OwnerPrompts(new("s", f.Machine), Size, default);
        Assert.Equal(f.Machine, page.EngineMachine);
        var manifest = Assert.Single(page.Items);
        Assert.Equal(target, manifest.TargetEpoch); Assert.Equal(7, manifest.HeldEpoch); Assert.False(manifest.TextComplete);
        Assert.Equal(prepared.TextUtf8Bytes, manifest.TextUtf8Bytes);
        var rawText = ReadAll(reader, manifest, "text");
        Assert.Equal(text, rawText);
        var references = JsonSerializer.Deserialize(ReadAll(reader, manifest, "attachments"), Assembly.Application.Features.Chat.Sessions.SessionsJson.Default.IReadOnlyListString)!;
        var restored = OwnerPromptFingerprint.Prepare(page.EngineMachine, new("s", rawText, manifest.ClientId, references, manifest.TargetEpoch), default);
        Assert.Equal(prepared.ContentSha256, restored.ContentSha256);
        Assert.Equal(text, rawText); Assert.Equal(command.AttachmentIds, references);
        Assert.Equal(original, Accept(f, restored)); Assert.Equal(1, f.Count("engine_message"));
        var display = reader.Entity("message", "prompt/s/C", default)!.Value;
        Assert.Equal(new string('x', 256), display.GetProperty("text").GetString());
        Assert.False(display.TryGetProperty("attachmentIdsJson", out _));
        f.Db.Writer.Write("test.terminal-state", s => s.Execute("UPDATE engine_message SET state='next_turn',version=version+1;UPDATE engine_session SET epoch=8"));
        Assert.Empty(reader.OwnerPrompts(new("s"), Size, default).Items);
        Assert.Equal(text, ReadAll(reader, manifest, "text"));
        Assert.Equal(original, Accept(f, restored));
    }

    [Fact]
    public void Byte_bounded_pages_keep_last_included_cursor_and_count_limit()
    {
        using var f = new EngineFixture(); f.SeedSession(state: "running");
        var text = string.Concat(Enumerable.Repeat("😀\"\\", 256));
        for (var i = 0; i < 80; i++) Assert.True(Accept(f, OwnerPromptFingerprint.Prepare(f.Machine, new("s", text, $"C{i:D3}", [], null), default)).Applied);
        var reader = new EngineReader(f.Db, f.Machine);
        var first = reader.OwnerPrompts(new("s", Limit: 200), Size, default);
        Assert.InRange(first.Items.Count, 1, 79); Assert.True(Size(first) <= 65536);
        Assert.Equal(first.Items[^1].ClientId, first.NextClientId);
        List<string> seen = []; OwnerPromptPage page = first;
        while (true)
        {
            seen.AddRange(page.Items.Select(i => i.ClientId));
            if (page.NextClientId is null) break;
            page = reader.OwnerPrompts(new("s", f.Machine, page.NextCreatedMs, page.NextClientId, 200), Size, default);
            Assert.NotEmpty(page.Items); Assert.True(Size(page) <= 65536);
        }
        Assert.Equal(80, seen.Count); Assert.Equal(80, seen.Distinct().Count()); Assert.Equal(seen.Order(), seen);
        var limited = reader.OwnerPrompts(new("s", Limit: 3), Size, default);
        Assert.Equal(3, limited.Items.Count); Assert.Equal(limited.Items[^1].ClientId, limited.NextClientId);
        Assert.Throws<ArgumentException>(() => reader.OwnerPrompts(new("s", AfterCreatedMs: 1), Size, default));
        Assert.Throws<InvalidOperationException>(() => reader.OwnerPrompts(new("s", "paired-phone-id"), Size, default));
    }

    [Fact]
    public void Chunk_bounds_digest_identity_eof_and_cancellation_are_enforced()
    {
        using var f = new EngineFixture(); f.SeedSession(state: "running");
        var p = OwnerPromptFingerprint.Prepare(f.Machine, new("s", "\0😀", "C", [], null), default); Assert.True(Accept(f, p).Applied);
        var reader = new EngineReader(f.Db, f.Machine);
        var args = new OwnerPromptChunkArgs("s", "C", p.ContentSha256, "text", p.TextUtf8Bytes);
        var eof = reader.OwnerPromptContent(args, default); Assert.Empty(eof.Data); Assert.Null(eof.NextOffset);
        foreach (var invalid in new[] { args with { Offset = -1 }, args with { Offset = long.MaxValue }, args with { Count = 0 }, args with { Count = 1048577 }, args with { Part = "secret" } })
            Assert.Throws<ArgumentException>(() => reader.OwnerPromptContent(invalid, default));
        Assert.Throws<KeyNotFoundException>(() => reader.OwnerPromptContent(args with { Session = "other" }, default));
        Assert.Throws<InvalidOperationException>(() => reader.OwnerPromptContent(args with { ContentSha256 = new string('a', 64) }, default));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => reader.OwnerPromptContent(args, cancel.Token));
        var page = reader.OwnerPrompts(new("s"), Size, default); Assert.False(Assert.Single(page.Items).TextComplete);
    }

    private static string ReadAll(EngineReader reader, OwnerPromptManifest item, string part)
    {
        using var content = new MemoryStream(); long offset = 0;
        do
        {
            var chunk = reader.OwnerPromptContent(new("s", item.ClientId, item.ContentSha256, part, offset), default);
            content.Write(chunk.Data); Assert.Equal(offset, chunk.Offset);
            if (chunk.NextOffset is not { } next) break;
            offset = next;
        } while (true);
        return new UTF8Encoding(false, true).GetString(content.ToArray());
    }

    private static int Size(OwnerPromptPage page) => SocketDispatcher.Result(long.MaxValue,
        JsonSerializer.SerializeToElement(page, EngineSocketJson.Default.OwnerPromptPage)).Length;
    private static CommandResult Accept(EngineFixture f, PreparedOwnerPrompt prompt) => f.Db.Writer.Write("test.owner", s =>
        new EngineCommands(f.Machine, f.Limits).ApplyOwnerPrompt(s, new(OwnerPromptIdentity.CommandId(f.Machine, prompt.ToSessionId, prompt.ClientId),
            f.Machine, new(ActorKind.Window, "owner"), OwnerPromptIdentity.MessageId(prompt.ToSessionId, prompt.ClientId), 0, 0, 1000), prompt));
}
