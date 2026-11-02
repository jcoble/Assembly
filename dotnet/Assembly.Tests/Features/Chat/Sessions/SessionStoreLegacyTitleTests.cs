using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed class SessionStoreLegacyTitleTests : IDisposable
{
    private const string Publisher = "actual-engine-id";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly TempDatabase _file = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(1000));
    private readonly AppDatabase _db;
    private readonly SessionStore _store;

    public SessionStoreLegacyTitleTests()
    {
        _db = TestDatabase.Open(_file.Path);
        _store = SessionStore.Open(_db, _time, "checkouts");
    }

    [Theory]
    [InlineData("")]
    [InlineData("\0")]
    [InlineData("a\0😀\"\\end")]
    public void Fixed_blob_reads_preserve_empty_nul_unicode_and_eof(string title)
    {
        var reference = Seed(title);
        using var scope = _db.OpenSnapshot(default);
        var stat = _store.LegacyTitleStat(scope, Publisher, new(reference));
        Assert.Equal(reference, stat.Reference);
        Assert.Null(stat.OwningEngineVersion);
        Assert.Null(stat.OwningEngineEpoch);
        Assert.Equal(Utf8.GetBytes(title), ReadAll(reference, 1));
        Assert.Equal(title, _store.Get(reference.SessionId)!.Title);
        var eof = _store.LegacyTitleChunk(scope, Publisher, new(reference, reference.Utf8Bytes, 1));
        Assert.Empty(eof.Data);
        Assert.Null(eof.NextOffset);
    }

    [Fact]
    public void Full_nine_megabyte_title_reopens_and_old_reference_survives_real_touch_at_every_chunk()
    {
        var title = "\0" + new string('x', 9_000_000) + "😀";
        var reference = Seed(title);
        var reopened = SessionStore.Open(_db, _time, "checkouts");
        Assert.Equal(title, reopened.Get(reference.SessionId)!.Title);
        using var all = new MemoryStream();
        long offset = 0;
        while (offset < reference.Utf8Bytes)
        {
            _time.Advance(TimeSpan.FromMilliseconds(1));
            _store.Touch(reference.SessionId);
            using var scope = _db.OpenSnapshot(default);
            var stat = _store.LegacyTitleStat(scope, Publisher, new(reference));
            Assert.Equal(_time.GetUtcNow().ToUnixTimeMilliseconds(), stat.Reference.UpdatedAt);
            Assert.Equal(reference with { UpdatedAt = stat.Reference.UpdatedAt }, stat.Reference);
            var chunk = _store.LegacyTitleChunk(scope, Publisher, new(reference, offset, 1024 * 1024));
            Assert.Equal(offset, chunk.Offset);
            Assert.Equal(reference.Utf8Bytes, chunk.TotalBytes);
            Assert.Equal(Math.Min(1024 * 1024L, reference.Utf8Bytes - offset), chunk.Data.LongLength);
            all.Write(chunk.Data);
            offset += chunk.Data.LongLength;
            Assert.Equal(offset < reference.Utf8Bytes ? offset : (long?)null, chunk.NextOffset);
        }
        Assert.Equal(Utf8.GetBytes(title), all.ToArray());
        Assert.Equal(reference.Sha256, ExpectedHash(all.ToArray()));
        _store.SetTitle(reference.SessionId, title + "changed", "user");
        using var changed = _db.OpenSnapshot(default);
        Changed(() => _store.LegacyTitleStat(changed, Publisher, new(reference)));
        Changed(() => _store.LegacyTitleChunk(changed, Publisher, new(reference, 0, 1)));
    }

    [Fact]
    public void Null_title_is_not_an_empty_content_reference_and_invalid_utf8_is_refused()
    {
        var session = _store.Create("claude", "repo");
        var reference = new LegacySessionTitleRef(session.SessionId, Publisher, null, null, null, session.UpdatedAt, 0, ExpectedHash([]));
        using (var scope = _db.OpenSnapshot(default))
            Changed(() => _store.LegacyTitleStat(scope, Publisher, new(reference)));
        Assert.Null(_store.Item(_store.Get(session.SessionId)!).Title);
        Write("UPDATE session_info SET title=CAST(X'F0808080' AS TEXT) WHERE session_id=$id", session.SessionId);
        using var malformed = _db.OpenSnapshot(default);
        Assert.Throws<DecoderFallbackException>(() => _store.LegacyTitleStat(malformed, Publisher, new(reference with { Utf8Bytes = 4 })));
        Assert.Throws<DecoderFallbackException>(() => SessionTitleHash.Compute([0xff]));
    }

    [Fact]
    public void Publisher_full_hash_length_and_each_nullable_provenance_field_are_exact()
    {
        var reference = Seed("exact");
        using (var scope = _db.OpenSnapshot(default))
        {
            foreach (var forged in new[] {
                reference with { PublisherEngineMachine = "alias" }, reference with { StoredMachine = "" },
                reference with { ParentSessionId = "" }, reference with { TitleSource = "prompt" },
                reference with { Sha256 = new string('0', 64) }, reference with { Utf8Bytes = 4 } })
            {
                Changed(() => _store.LegacyTitleStat(scope, Publisher, new(forged)));
                Changed(() => _store.LegacyTitleChunk(scope, Publisher, new(forged, 0, 1)));
            }
            Changed(() => _store.LegacyTitleStat(scope, "other-host", new(reference)));
        }
        foreach (var column in new[] { "machine", "parent_session_id", "title_source" })
        {
            // Only the fixture selects a column; the production readers have fixed SQL.
            Write($"UPDATE session_info SET {column}='' WHERE session_id=$id", reference.SessionId);
            using (var changed = _db.OpenSnapshot(default))
                Changed(() => _store.LegacyTitleStat(changed, Publisher, new(reference)));
            Write($"UPDATE session_info SET {column}=NULL WHERE session_id=$id", reference.SessionId);
        }
        Write("UPDATE session_info SET machine='copy',parent_session_id='parent',title_source='helper' WHERE session_id=$id", reference.SessionId);
        var copied = reference with { StoredMachine = "copy", ParentSessionId = "parent", TitleSource = "helper" };
        Assert.Equal(Utf8.GetBytes("exact"), ReadAll(copied, 2));
        Write("DELETE FROM session_info WHERE session_id=$id", reference.SessionId);
        using var missing = _db.OpenSnapshot(default);
        Assert.Equal("unknown session", Assert.Throws<KeyNotFoundException>(() => _store.LegacyTitleStat(missing, Publisher, new(reference))).Message);
        Assert.Throws<KeyNotFoundException>(() => _store.LegacyTitleChunk(missing, Publisher, new(reference, 0, 1)));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(6, 1)]
    [InlineData(long.MaxValue, 1)]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 1048577)]
    public void Invalid_ranges_never_read_title_bytes(long offset, int count)
    {
        var reference = Seed("exact");
        using var scope = _db.OpenSnapshot(default);
        Assert.Equal("bad_args", Assert.Throws<ArgumentException>(() => _store.LegacyTitleChunk(scope, Publisher, new(reference, offset, count))).Message);
        Assert.Throws<OverflowException>(() => _store.LegacyTitleChunk(scope, Publisher, new(reference with { Utf8Bytes = long.MaxValue }, long.MaxValue, 1)));
    }

    [Fact]
    public void Reads_use_only_the_supplied_snapshot_across_a_real_concurrent_rename()
    {
        var reference = Seed("original");
        using var original = _db.OpenSnapshot(default);
        Assert.Equal(reference, _store.LegacyTitleStat(original, Publisher, new(reference)).Reference);
        _store.SetTitle(reference.SessionId, "replacement", "user");
        Assert.Equal(Utf8.GetBytes("original"), _store.LegacyTitleChunk(original, Publisher, new(reference, 0, 100)).Data);
        using var latest = _db.OpenSnapshot(default);
        Changed(() => _store.LegacyTitleChunk(latest, Publisher, new(reference, 0, 100)));
    }

    private LegacySessionTitleRef Seed(string title)
    {
        var session = _store.Create("claude", "repo");
        // Preserve a literal nullable rank rather than normalizing it to prompt.
        _db.Writer.Write("test.title", scope => scope.Execute("UPDATE session_info SET title=CAST($title AS TEXT) WHERE session_id=$id",
            new SqlArg("$id", session.SessionId), new SqlArg("$title", Utf8.GetBytes(title))));
        var bytes = Utf8.GetBytes(title);
        var reference = new LegacySessionTitleRef(session.SessionId, Publisher, null, null, null, session.UpdatedAt, bytes.LongLength, ExpectedHash(bytes));
        Assert.Equal(reference.Sha256, SessionTitleHash.Compute(bytes));
        return reference;
    }

    private byte[] ReadAll(LegacySessionTitleRef reference, int count)
    {
        using var all = new MemoryStream();
        using var scope = _db.OpenSnapshot(default);
        long offset = 0;
        do
        {
            var chunk = _store.LegacyTitleChunk(scope, Publisher, new(reference, offset, count));
            Assert.Equal(offset, chunk.Offset);
            all.Write(chunk.Data);
            if (chunk.NextOffset is not { } next) break;
            Assert.Equal(offset + chunk.Data.LongLength, next);
            offset = next;
        } while (true);
        return all.ToArray();
    }

    private void Write(string sql, string id) => _db.Writer.Write("test.provenance", scope => scope.Execute(sql, new SqlArg("$id", id)));
    private static void Changed(Action read) => Assert.Equal("title_changed", Assert.Throws<InvalidOperationException>(read).Message);
    private static string ExpectedHash(byte[] bytes)
    {
        var input = new byte["session-title/v1"u8.Length + 8 + bytes.Length];
        "session-title/v1"u8.CopyTo(input);
        BinaryPrimitives.WriteUInt64BigEndian(input.AsSpan("session-title/v1"u8.Length, 8), (ulong)bytes.LongLength);
        bytes.CopyTo(input, "session-title/v1"u8.Length + 8);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    public void Dispose() { _db.Dispose(); _file.Dispose(); }
}
