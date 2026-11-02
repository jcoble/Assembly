using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Engine.OwnerPrompts;

public sealed class OwnerPromptPreparationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Raw_owner_command_is_refused_before_the_journal_even_above_generic_budget()
    {
        using var f = new EngineFixture(); f.SeedSession();
        var command = new SendOwnerPrompt("s", new string('x', 65536), "C", [], null);
        var context = new global::Assembly.Engine.Contracts.CommandContext(OwnerPromptIdentity.CommandId(f.Machine, "s", "C"),
            f.Machine, new(global::Assembly.Engine.Contracts.ActorKind.Window, "owner"), "prompt/s/C", 0, 0, 1000);
        var result = f.Apply(context, command);
        Assert.Equal("forbidden", result.Error);
        Assert.Equal(0, f.Count("engine_command_receipt"));
        Assert.Equal(0, f.Count("engine_message")); Assert.Empty(f.Events());
    }
    [Theory]
    [InlineData(null, "eeee9bf0201c8845ab0eb8a93accec76a735109fd37de7f6f0a1cb0806ed0e90")]
    [InlineData(7L, "463b0787ab43a1b3bfc690a6051d24c269257d271e5d2db0abc7e2e5fedfdc1f")]
    public void Prepared_digest_matches_fixed_protocol_vectors(long? epoch, string expected)
    {
        var prepared = OwnerPromptFingerprint.Prepare("machine", new("s", "hi", "C", ["/x", "/y"], epoch), default);
        Assert.Equal(expected, prepared.ContentSha256);
        Assert.Equal(epoch, prepared.TargetEpoch); Assert.Equal(2, prepared.TextUtf8Bytes);
        Assert.Equal("[\"/x\",\"/y\"]", prepared.AttachmentIdsJson);
    }

    [Fact]
    public void Unicode_nul_and_escaped_reference_digest_matches_fixed_vector()
    {
        var prepared = OwnerPromptFingerprint.Prepare("machine",
            new("s", "A😀\0é", "C", ["/u/é.png", "/u/\"two\".jpg"], null), default);
        Assert.Equal("15ee27c32254a030ebbf5b1743e7c18db55cd1b00afc99064ef1cf2bc268b3c0", prepared.ContentSha256);
        Assert.Equal(8, prepared.TextUtf8Bytes);
        Assert.Equal(new[] { "/u/é.png", "/u/\"two\".jpg" },
            JsonSerializer.Deserialize(prepared.AttachmentIdsJson, EngineJson.Default.IReadOnlyListString));
    }

    [Fact]
    public void Preparation_owns_references_and_binds_full_identity_order_and_final_bytes()
    {
        var references = new List<string> { "/one", "/two" };
        var original = new SendOwnerPrompt("s", new string('x', 65536) + "é", "C", references, null);
        var prepared = OwnerPromptFingerprint.Prepare("machine", original, default);
        references[0] = "/bad"; references.Reverse();
        Assert.Equal(new[] { "/one", "/two" }, prepared.AttachmentIds);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)prepared.AttachmentIds)[0] = "/changed");
        foreach (var changed in new[]
        {
            original with { AttachmentIds = prepared.AttachmentIds, Text = new string('x', 65536) + "è" },
            original with { AttachmentIds = prepared.AttachmentIds, Text = new string('x', 65536) + "e\u0301" },
            original with { AttachmentIds = ["/two", "/one"] },
            original with { AttachmentIds = ["/one", "/tow"] },
            original with { AttachmentIds = prepared.AttachmentIds, TargetEpoch = 0 },
            original with { AttachmentIds = prepared.AttachmentIds, ToSessionId = "other" },
            original with { AttachmentIds = prepared.AttachmentIds, ClientId = "other" }
        })
            Assert.NotEqual(prepared.ContentSha256, OwnerPromptFingerprint.Prepare("machine", changed, default).ContentSha256);
        Assert.NotEqual(prepared.ContentSha256, OwnerPromptFingerprint.Prepare("other", original with { AttachmentIds = prepared.AttachmentIds }, default).ContentSha256);
    }

    [Fact]
    public void Preparation_handles_scalar_split_without_full_escaped_text_and_rejects_invalid_utf16()
    {
        var text = new string('a', 4095) + "😀\0";
        var prepared = OwnerPromptFingerprint.Prepare("machine", new("s", text, "C", [], null), default);
        Assert.Equal(4100, prepared.TextUtf8Bytes); Assert.Equal(text, prepared.Text);
        Assert.Throws<EncoderFallbackException>(() => OwnerPromptFingerprint.Prepare("machine", new("s", "\ud800", "C", [], null), default));
        Assert.Throws<EncoderFallbackException>(() => OwnerPromptFingerprint.Prepare("machine", new("s", "text", "C", ["\udc00"], null), default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => OwnerPromptFingerprint.Prepare("machine", new("s", text, "C", [], null), cancelled.Token));
    }

    [Fact]
    public void Prepared_large_payload_uses_one_bounded_journal_receipt_and_replays_actual_result()
    {
        using var f = new EngineFixture(); f.SeedSession(state: "running", epoch: 7);
        var text = string.Concat(Enumerable.Repeat("😀\"\\\0", 10000));
        var command = new SendOwnerPrompt("s", text, "C", ["/one", "/two"], null);
        var prepared = OwnerPromptFingerprint.Prepare(f.Machine, command, default);
        Assert.True(prepared.TextUtf8Bytes > 32768); Assert.Equal(text, prepared.Text);
        Assert.InRange(Encoding.UTF8.GetByteCount(prepared.CanonicalJournal), 1, 512);
        using var envelope = JsonDocument.Parse(prepared.CanonicalJournal);
        Assert.Equal(new[] { "kind", "version", "requestSha256", "textUtf8Bytes", "attachmentJsonUtf8Bytes", "attachmentCount" },
            envelope.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(prepared.ContentSha256, envelope.RootElement.GetProperty("requestSha256").GetString());
        var context = Context(f, prepared);
        var result = Apply(f, context, prepared);
        Assert.NotEqual("command_too_large", result.Error);
        Assert.Equal(1, f.Count("engine_command_receipt"));
        var events = f.Count("engine_event"); var effects = f.Count("engine_outbox");
        f.Reopen();
        Assert.Equal(result, Apply(f, context with { NowMs = 3000 }, OwnerPromptFingerprint.Prepare(f.Machine, command, default)));
        Assert.Equal(events, f.Count("engine_event")); Assert.Equal(effects, f.Count("engine_outbox"));
        Assert.Equal("client_id_conflict", Apply(f, context, OwnerPromptFingerprint.Prepare(f.Machine, command with { Text = text[..^1] + "x" }, default)).Error);
        Assert.Equal(1, f.Count("engine_command_receipt"));
        output.WriteLine($"Actual prepared Journal result: applied={result.Applied}, error={result.Error}; storage acceptance remains a separate pending transition.");
    }

    [Theory]
    [InlineData("message")]
    [InlineData("command")]
    [InlineData("version")]
    [InlineData("epoch")]
    [InlineData("actor")]
    public void Prepared_seam_checks_original_creation_identity_inside_existing_journal(string changed)
    {
        using var f = new EngineFixture(); f.SeedSession();
        var prepared = OwnerPromptFingerprint.Prepare(f.Machine, new("s", "text", "C", [], null), default);
        var context = Context(f, prepared);
        context = changed switch
        {
            "message" => context with { EntityId = "s" },
            "command" => context with { CommandId = Guid.NewGuid() },
            "version" => context with { ExpectedVersion = 1 },
            "epoch" => context with { Epoch = 7 },
            _ => context with { Actor = new(ActorKind.System, "owner") }
        };
        Assert.Equal(changed == "actor" ? "forbidden" : "bad_args", Apply(f, context, prepared).Error);
        Assert.Equal(0, f.Count("engine_message")); Assert.Equal(0, f.Count("engine_outbox")); Assert.Empty(f.Events());
    }

    [Fact]
    public void Generated_manifest_and_chunk_payloads_respect_their_independent_size_bounds()
    {
        var preview = string.Concat(Enumerable.Repeat("😀", 256));
        var manifest = new OwnerPromptManifest(new string('C', 64), new string('a', 64), preview, false,
            long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue, "queued", long.MaxValue);
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, SessionsJson.Default.OwnerPromptManifest);
        Assert.True(json.Length < 4096);
        var page = new OwnerPromptPage("engine-machine", [manifest], long.MaxValue, manifest.ClientId);
        var encoded = JsonSerializer.Serialize(page, EngineSocketJson.Default.OwnerPromptPage);
        Assert.Equal("engine-machine", JsonSerializer.Deserialize(encoded, EngineSocketJson.Default.OwnerPromptPage)!.EngineMachine);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"items\":[],\"nextCreatedMs\":null,\"nextClientId\":null}", EngineSocketJson.Default.OwnerPromptPage));
        var chunk = new OwnerPromptChunk(new string('a', 64), "text", 0, 1024 * 1024, new byte[1024 * 1024], null);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(chunk, EngineSocketJson.Default.OwnerPromptChunk);
        Assert.True(bytes.Length < 4 * 1024 * 1024);
        var restored = JsonSerializer.Deserialize(bytes, EngineSocketJson.Default.OwnerPromptChunk)!;
        Assert.Equal(chunk.Data, restored.Data); Assert.Equal(chunk.TotalBytes, restored.TotalBytes);
        output.WriteLine($"Generated DTO sizes only: manifest={json.Length}, 1MiB chunk={bytes.Length}; actual page/chunk routes remain pending schema/read source.");
    }

    private static CommandContext Context(EngineFixture f, PreparedOwnerPrompt prepared) => new(
        OwnerPromptIdentity.CommandId(f.Machine, prepared.ToSessionId, prepared.ClientId), f.Machine,
        new(ActorKind.Window, "owner"), OwnerPromptIdentity.MessageId(prepared.ToSessionId, prepared.ClientId), 0, 0, 1000);

    private static CommandResult Apply(EngineFixture f, CommandContext context, PreparedOwnerPrompt prepared) =>
        f.Db.Writer.Write("test.owner-prepared", scope => new EngineCommands(f.Machine, f.Limits).ApplyOwnerPrompt(scope, context, prepared));

}
