using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;

namespace Assembly.Tests.Engine.Needs;

public sealed class PreparedNeedCommandTests
{
    private static readonly EngineActor Adapter = new(ActorKind.System, "adapter");
    private static OpenNeed Open(string text = "Question") => new("s", null, null, "question", text, [], null, null, null);
    private static string Canonical(NeedCommand command) => JsonSerializer.Serialize((EngineCommand)command, EngineJson.Default.EngineCommand);

    [Fact]
    public void Omitted_full_provider_question_preserves_original_generated_bytes_and_receipt()
    {
        const string original = """{"kind":"open_need","sessionId":"s","runId":null,"stepId":null,"needKind":"question","text":"Question","options":[],"providerRequestId":null,"chatRowId":null,"deadlineMs":null}""";
        var command = Open();
        Assert.Equal(original, Canonical(command));
        var prepared = EngineCommands.PrepareNeedCommand(command);
        Assert.Equal(original, prepared.SmallCanonical);
        Assert.Equal(original, prepared.CanonicalJournal);
        using var f = new EngineFixture(); f.SeedSession();
        var context = new CommandContext(Guid.NewGuid(), f.Machine, Adapter, "n", 0, 0, 1000);
        var ordinary = f.Apply(context, command);
        Assert.True(ordinary.Applied);
        var replay = f.Db.Writer.Write("test.prepared-small-replay", s => new EngineCommands(f.Machine, f.Limits).ApplyPreparedNeed(s, context with { NowMs = 9999 }, prepared));
        Assert.Equal(ordinary, replay);
        Assert.Equal(1, f.Count("engine_needs_me"));
        Assert.Equal(1, f.Count("engine_command_receipt"));
    }

    [Theory]
    [InlineData(32768)]
    [InlineData(32769)]
    public void Exact_generated_canonical_boundary_retains_small_bytes_or_fixed_envelope(int length)
    {
        var overhead = Encoding.UTF8.GetByteCount(Canonical(Open("")));
        var command = Open(new string('x', length - overhead));
        var bytes = JsonSerializer.SerializeToUtf8Bytes((EngineCommand)command, EngineJson.Default.EngineCommand);
        Assert.Equal(length, bytes.Length);
        var prepared = EngineCommands.PrepareNeedCommand(command);
        Assert.Equal(length, prepared.CanonicalUtf8Bytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), prepared.CanonicalSha256);
        if (length == 32768) Assert.Equal(Encoding.UTF8.GetString(bytes), prepared.SmallCanonical);
        else
        {
            Assert.Null(prepared.SmallCanonical);
            using var envelope = JsonDocument.Parse(prepared.CanonicalJournal);
            Assert.Equal("open_need", envelope.RootElement.GetProperty("kind").GetString());
            Assert.Equal(1, envelope.RootElement.GetProperty("needJournalVersion").GetInt32());
            Assert.Equal(length, envelope.RootElement.GetProperty("commandUtf8Bytes").GetInt64());
            Assert.Equal(prepared.CanonicalSha256, envelope.RootElement.GetProperty("commandSha256").GetString());
            Assert.Equal(4, envelope.RootElement.EnumerateObject().Count());
        }
    }

    [Fact]
    public void Full_generated_form_and_options_are_frozen_and_hashed_without_flattening()
    {
        var text = "prefix\0\n[user] 😀 " + new string('x', 2 * 1024 * 1024);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { kind = "question", state = "open", title = (string?)null, message = "", schema = new { properties = new { body = new { title = text, values = new[] { "", "λ", text } } } } }));
        var options = new List<string> { "", "once\0", "persistent" };
        var command = Open(text) with { Options = options, ProviderQuestion = document.RootElement };
        var expected = JsonSerializer.SerializeToUtf8Bytes((EngineCommand)command, EngineJson.Default.EngineCommand);
        var prepared = EngineCommands.PrepareNeedCommand(command);
        options[0] = "mutated"; document.Dispose();
        Assert.Equal(expected, JsonSerializer.SerializeToUtf8Bytes((EngineCommand)prepared.Command, EngineJson.Default.EngineCommand));
        Assert.Equal(expected.LongLength, prepared.CanonicalUtf8Bytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(expected)), prepared.CanonicalSha256);
        Assert.Null(prepared.SmallCanonical);
        Assert.True(Encoding.UTF8.GetByteCount(prepared.CanonicalJournal) < 256);
    }

    [Fact]
    public void Actual_large_need_keeps_full_NUL_bytes_and_old_large_refusal_replays()
    {
        using var f = new EngineFixture(); f.SeedSession();
        var text = "before\0after😀\n[system] " + new string('q', 90000);
        var command = Open(text);
        var original = new CommandContext(Guid.NewGuid(), f.Machine, Adapter, "old", 0, 0, 1000);
        var refused = f.Apply(original, command);
        Assert.Equal("command_too_large", refused.Error);
        var prepared = EngineCommands.PrepareNeedCommand(command);
        var context = original with { CommandId = Guid.NewGuid(), EntityId = "new" };
        var accepted = f.Db.Writer.Write("test.large-need", s => new EngineCommands(f.Machine, f.Limits).ApplyPreparedNeed(s, context, prepared));
        Assert.True(accepted.Applied, accepted.Error);
        Assert.Equal(Encoding.UTF8.GetBytes(text), f.Db.QueryFirst("SELECT CAST(text AS BLOB) FROM engine_needs_me WHERE id='new'", r => r.Blob(0), default));
        Assert.Equal(refused, f.Apply(original with { NowMs = 5000 }, command));
        Assert.Equal(1, f.Count("engine_needs_me"));
    }

    [Fact]
    public void Full_resolve_object_changes_identity_and_caller_document_can_close()
    {
        using var answer = JsonDocument.Parse("""{"action":"accept","content":{"body":"before\u0000after😀","nested":[1,true,{"value":"λ"}]}}""");
        var command = new ResolveNeed("answered", answer.RootElement);
        var expected = Canonical(command);
        var prepared = EngineCommands.PrepareNeedCommand(command);
        answer.Dispose();
        Assert.Equal(expected, Canonical(prepared.Command));
        var changed = EngineCommands.PrepareNeedCommand(new ResolveNeed("answered", JsonSerializer.SerializeToElement(new { action = "decline" })));
        Assert.NotEqual(prepared.CanonicalSha256, changed.CanonicalSha256);
    }

    [Fact]
    public void Cancellation_and_invalid_UTF16_refuse_before_writer_entry()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => EngineCommands.PrepareNeedCommand(Open(), stop.Token));
        Assert.Throws<EncoderFallbackException>(() => EngineCommands.PrepareNeedCommand(Open("unpaired \ud800")));
        Assert.Throws<ArgumentException>(() => EngineCommands.PrepareNeedCommand(new UnsupportedNeed()));
    }

    private sealed record UnsupportedNeed : NeedCommand;
}
