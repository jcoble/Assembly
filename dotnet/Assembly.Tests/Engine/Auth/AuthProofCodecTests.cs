using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Engine;
using Assembly.Engine.Effects;

namespace Assembly.Tests.Engine.Auth;

public sealed class AuthProofCodecTests
{
    private const string Observation = "0b16a63c-e123-4fb9-8314-865c606e2141";
    private const string BlockCommand = "467e0088-8dd0-4107-889b-b35edbb57009";
    private const string Blocker = """
        {"effectId":"send/M/1","originalClaimAttempt":2,"observationId":"0b16a63c-e123-4fb9-8314-865c606e2141","needId":"N","needVersionAtBlock":3,"blockCommandId":"467e0088-8dd0-4107-889b-b35edbb57009"}
        """;
    private const string Retry = """
        {"chatRunId":"R2","messageId":"M","effectId":"send/M/2","claimAttempt":1,"priorEffectId":"send/M/1","clientId":"client","epoch":7,"userRowSeq":1,"userRunId":"R1"}
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Generated_terminal_rows_retain_exact_full_ACP_proof_without_changing_the_real_execution(bool inserted)
    {
        var raw = Proof(1, "actual-acp", inserted ? "actual-provider" : null);
        using var input = JsonDocument.Parse("{\"state\":\"failed\",\"authFailure\":" + raw + "}");
        var terminal = input.RootElement.Deserialize(RowJsonContext.Rows.TurnPayload)!;
        var saved = JsonSerializer.SerializeToElement(terminal, RowJsonContext.Rows.TurnPayload);
        var proof = saved.GetProperty("authFailure");
        Assert.Equal(1, proof.GetProperty("version").GetInt32());
        Assert.Equal("actual-acp", proof.GetProperty("acpSessionId").GetString());
        Assert.Equal(inserted ? "actual-provider" : null, Nullable(proof, "providerMessageId"));
        Assert.Equal("R2", proof.GetProperty("chatRunId").GetString());
        Assert.Equal("R1", proof.GetProperty("delivery").GetProperty("userRunId").GetString());
        Assert.False(proof.TryGetProperty("openFailure", out _));
        Assert.Equal(Observation, proof.GetProperty("observationId").GetString());
        Assert.Equal(BlockCommand, proof.GetProperty("blockCommandId").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Generated_failed_open_rows_retain_closed_stage_and_absent_actual_ACP_and_provider_ids(int stage)
    {
        var raw = Proof(2, null, null, stage);
        var terminal = JsonSerializer.Deserialize("{\"state\":\"failed\",\"authFailure\":" + raw + "}", RowJsonContext.Rows.TurnPayload)!;
        var saved = JsonSerializer.SerializeToElement(terminal, RowJsonContext.Rows.TurnPayload).GetProperty("authFailure");
        Assert.Equal(2, saved.GetProperty("version").GetInt32());
        Assert.Equal(stage, saved.GetProperty("openFailure").GetInt32());
        Assert.Null(Nullable(saved, "acpSessionId")); Assert.Null(Nullable(saved, "providerMessageId"));
        Assert.Equal("R2", saved.GetProperty("chatRunId").GetString());
        Assert.Equal("R1", saved.GetProperty("delivery").GetProperty("userRunId").GetString());
    }

    [Fact]
    public void Generated_user_rows_retain_the_independent_blocker_and_real_retry_mapping()
    {
        var raw = "{\"text\":\"body\\u0000tail\",\"from\":\"parent\",\"clientId\":\"client\",\"senderLabel\":\"Captured parent\",\"authBlocker\":" + Blocker + ",\"retryExecution\":" + Retry + "}";
        var user = JsonSerializer.Deserialize(raw, RowJsonContext.Rows.UserPayload)!;
        var saved = JsonSerializer.SerializeToElement(user, RowJsonContext.Rows.UserPayload);
        Same(Blocker, saved.GetProperty("authBlocker")); Same(Retry, saved.GetProperty("retryExecution"));
        Assert.Equal("body\0tail", saved.GetProperty("text").GetString());
        Assert.Equal("Captured parent", saved.GetProperty("senderLabel").GetString());
    }

    [Fact]
    public void Generated_effect_receipt_retains_original_refusal_and_execution_after_user_pointer_changes()
    {
        var refusal = "{\"messageAttempt\":1,\"blocker\":" + Blocker + "}";
        var raw = "{\"state\":\"queued\",\"providerTurnId\":null,\"reason\":\"auth_required\",\"userRow\":{\"seq\":1,\"runId\":\"R1\",\"epoch\":7},\"retryExecution\":" + Retry + ",\"authRefusal\":" + refusal + "}";
        var data = JsonSerializer.Deserialize(raw, EngineJson.Default.MessageEffectData)!;
        var saved = JsonSerializer.SerializeToElement(data, EngineJson.Default.MessageEffectData);
        Same(refusal, saved.GetProperty("authRefusal")); Same(Retry, saved.GetProperty("retryExecution"));
        Assert.Equal("queued", saved.GetProperty("state").GetString());
        Assert.Equal("auth_required", saved.GetProperty("reason").GetString()); Assert.Null(saved.GetProperty("providerTurnId").GetString());
        Assert.Equal("R1", saved.GetProperty("userRow").GetProperty("runId").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exact_SQL_user_reader_exposes_retained_metadata_and_the_sole_ack_preserves_it(bool acknowledge)
    {
        using var f = new EngineFixture(); var rows = RowStore.Open(f.Db);
        rows.Commit("s", new RowConverter().Apply(new TurnStarted(1000, "R1", "body\0tail", "client")));
        f.Db.Writer.Write("test.retained-auth-metadata", s => s.Execute("""
            UPDATE rows SET payload=json_set(payload,'$.authBlocker',json($blocker),'$.retryExecution',json($retry))
            WHERE session_id='s' AND kind='user'
            """, new SqlArg("$blocker", Blocker), new("$retry", Retry)));
        if (acknowledge) f.Db.Writer.Write("test.sole-positive-ack", s => { rows.AcknowledgeUser(s, "s", "client", new("prompt_inserted", "actual-provider")); return 0; });
        using var snapshot = f.Db.OpenSnapshot(default); var user = rows.UserDeliveryOfClient(snapshot, "s", "client")!;
        var saved = JsonSerializer.SerializeToElement(user, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Same(Blocker, saved.GetProperty("authBlocker")); Same(Retry, saved.GetProperty("retryExecution"));
        Assert.Equal("R1", user.RunId); Assert.Equal(1, user.Seq); Assert.Equal("body\0tail", user.Text);
        Assert.Equal(acknowledge ? "actual-provider" : null, user.Acknowledgment?.ProviderMessageId);
        Assert.Equal(5, rows.StoredFormatVersion);
    }

    [Fact]
    public void Old_ordinary_null_payload_bytes_remain_exact()
    {
        Assert.Equal("{\"state\":\"failed\"}", JsonSerializer.Serialize(new TurnPayload("failed"), RowJsonContext.Rows.TurnPayload));
        Assert.Equal("{\"text\":\"hi\",\"from\":\"parent\",\"clientId\":\"client\"}", JsonSerializer.Serialize(new UserPayload("hi", "parent", "client"), RowJsonContext.Rows.UserPayload));
        Assert.Equal("{\"state\":\"queued\",\"providerTurnId\":null,\"reason\":\"auth_required\"}", JsonSerializer.Serialize(new MessageEffectData("queued", null, "auth_required"), EngineJson.Default.MessageEffectData));
    }

    private static string Proof(int version, string? acp, string? provider, int? stage = null)
    {
        var raw = JsonSerializer.Serialize(new
        {
            version,
            observationId = Observation,
            blockCommandId = BlockCommand,
            chatRunId = "R2",
            acpSessionId = acp,
            epoch = 7,
            clientId = "client",
            providerMessageId = provider,
            delivery = new { messageId = "M", effectId = "send/M/2", claimAttempt = 1, clientId = "client", userRowSeq = 1, userRunId = "R1" }
        });
        return stage is null ? raw : raw[..^1] + ",\"openFailure\":" + stage + "}";
    }
    private static string? Nullable(JsonElement value, string key) => value.TryGetProperty(key, out var found) ? found.GetString() : null;
    private static void Same(string expected, JsonElement actual)
    { using var json = JsonDocument.Parse(expected); Assert.True(JsonElement.DeepEquals(json.RootElement, actual)); }
}
