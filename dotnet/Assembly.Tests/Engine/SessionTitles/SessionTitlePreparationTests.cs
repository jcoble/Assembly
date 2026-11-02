using Assembly.Engine;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine.SessionTitles;

public sealed class SessionTitlePreparationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("normal title")]
    [InlineData("\0title\0")]
    [InlineData("😀\"\\\n\u2028é")]
    public void Small_title_commands_keep_exact_existing_generated_canonical(string title)
    {
        foreach (var command in new SessionCommand[] { new ReportSessionTitle(title), new StartSession("claude", null, "/folder", title, null, null, null) })
        {
            var prepared = PreparedSessionTitleCommand.Prepare(command);
            Assert.False(prepared.Oversized);
            Assert.Equal(JsonSerializer.Serialize<EngineCommand>(command, EngineJson.Default.EngineCommand), prepared.CanonicalJournal);
            Assert.Equal(new UTF8Encoding(false, true).GetBytes(title), prepared.TitleUtf8);
        }
    }

    [Fact]
    public void Full_oversized_hash_matches_actual_generated_serializer_for_scalar_and_control_boundaries()
    {
        var title = new string('x', 511) + "😀\0\"\\\n\u2028" + string.Concat(Enumerable.Repeat("😀\"\\\0", 7000));
        var command = new StartSession("claude", null, "/folder", title, "parent", null, null, ManagedCheckout: false);
        var prepared = PreparedSessionTitleCommand.Prepare(command);
        Assert.True(prepared.Oversized);
        var actual = JsonSerializer.SerializeToUtf8Bytes<EngineCommand>(command, EngineJson.Default.EngineCommand);
        using var envelope = JsonDocument.Parse(prepared.CanonicalJournal);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(actual)), envelope.RootElement.GetProperty("commandSha256").GetString());
        Assert.Equal(actual.LongLength, envelope.RootElement.GetProperty("commandUtf8Bytes").GetInt64());
        Assert.Equal(new UTF8Encoding(false, true).GetBytes(title), prepared.TitleUtf8);
        Assert.InRange(Encoding.UTF8.GetByteCount(prepared.CanonicalJournal), 1, 512);
    }

    [Fact]
    public void Structural_string_token_selection_does_not_replace_another_equal_field()
    {
        var command = new StartSession("", "", "", "title\0suffix", "", "", "", "", false);
        var result = PreparedTextJson.Create(command with { Title = "" }, "title", command.Title);
        Assert.Equal(JsonSerializer.Serialize<EngineCommand>(command, EngineJson.Default.EngineCommand), result.SmallCanonical);
        Assert.Throws<ArgumentException>(() => PreparedSessionTitleCommand.Prepare(command with { Folder = new string('x', 40000) }));
        Assert.Throws<EncoderFallbackException>(() => PreparedSessionTitleCommand.Prepare(new ReportSessionTitle("\ud800")));
    }
}
