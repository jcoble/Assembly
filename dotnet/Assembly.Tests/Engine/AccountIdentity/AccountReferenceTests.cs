using Assembly.Application.Common.Database;

namespace Assembly.Tests.Engine.AccountIdentity;

public sealed class AccountReferenceTests
{
    [Theory]
    [InlineData("provider-key")]
    [InlineData("sign-in-provider")]
    [InlineData("event-key")]
    [InlineData("probe-id")]
    [InlineData("probe-order")]
    [InlineData("probe-payload")]
    [InlineData("probe-receipt")]
    [InlineData("begin-order")]
    [InlineData("begin-payload")]
    [InlineData("begin-receipt")]
    [InlineData("mirror-account")]
    [InlineData("mirror-key")]
    [InlineData("mirror-event")]
    [InlineData("cursor")]
    [InlineData("mirror-sign-in-provider")]
    [InlineData("mirror-sign-in-account")]
    public void Legacy_reference_must_resolve_an_exact_retained_default_context_identity_before_nine(string boundary)
    {
        using var f = new AccountIdentityFixture();
        f.Core.Db.Writer.Write("test.legacy-reference", scope => scope.Execute(boundary switch
        {
            "provider-key" => "UPDATE engine_provider SET id='wrong-provider-key'",
            "sign-in-provider" => "UPDATE engine_sign_in SET provider='missing-provider'",
            "event-key" => "UPDATE engine_event SET entity_id='missing-provider/' WHERE entity_kind='provider' AND machine=$machine",
            "probe-id" => "UPDATE engine_outbox SET id='wrong-probe-id' WHERE kind='probe_provider'",
            "probe-order" => "UPDATE engine_outbox SET order_key='provider/missing-provider/' WHERE kind='probe_provider'",
            "probe-payload" => "UPDATE engine_outbox SET payload_json=json_set(payload_json,'$.targetId',$raw) WHERE kind='probe_provider'",
            "probe-receipt" => "UPDATE engine_outbox SET receipt_json=json_set(receipt_json,'$.data.targetId',$raw) WHERE kind='probe_provider'",
            "begin-order" => "UPDATE engine_outbox SET order_key='provider/missing-provider/' WHERE kind='begin_sign_in'",
            "begin-payload" => "UPDATE engine_outbox SET payload_json=json_set(payload_json,'$.provider','missing-provider') WHERE kind='begin_sign_in'",
            "begin-receipt" => "UPDATE engine_outbox SET receipt_json=json_set(receipt_json,'$.data.operationId','missing-operation') WHERE kind='begin_sign_in'",
            "mirror-account" => "UPDATE engine_mirror_row SET row_json=json_set(row_json,'$.accountContext',$raw) WHERE entity_kind='provider'",
            "mirror-key" => "UPDATE engine_mirror_row SET entity_id='wrong-provider-key' WHERE entity_kind='provider'",
            "mirror-event" => "UPDATE engine_event SET entity_id='missing-provider/' WHERE machine='peer' AND entity_kind='provider'",
            "cursor" => "UPDATE engine_mirror SET at_json=json_set(at_json,'$.entityId',$raw) WHERE machine='peer'",
            "mirror-sign-in-provider" => "UPDATE engine_mirror_row SET row_json=json_set(row_json,'$.provider','missing-provider') WHERE entity_kind='sign_in'",
            _ => "UPDATE engine_mirror_row SET row_json=json_set(row_json,'$.accountContext',$raw) WHERE entity_kind='sign_in'"
        }, new SqlArg("$raw", AccountIdentityFixture.RawCanary), new("$machine", f.Core.Machine)));
        var before = f.Digest(); var error = Assert.Throws<InvalidOperationException>(() => f.Initialize());
        Assert.Equal("account_identity_upgrade_required", error.Message); Assert.Equal(8, f.Core.Number("SELECT schema_version FROM engine_meta")); Assert.Equal(before, f.Digest());
    }
}
