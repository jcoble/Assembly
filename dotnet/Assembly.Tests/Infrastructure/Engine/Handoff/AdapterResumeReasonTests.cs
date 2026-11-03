using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Infrastructure.Engine.Handoff;

public sealed class AdapterResumeReasonTests
{
    [Theory]
    [InlineData(-32601, "unsupported")]
    [InlineData(-32000, "denied")]
    public void Known_protocol_refusals_produce_only_the_canonical_safe_reason(int code, string expected)
    {
        var error = new JsonRpcException(code, "PRIVATE_NATIVE_PATH_AND_PROVIDER_TEXT");
        Assert.Equal(expected, AdapterEffects.ResumeFailureReason(error));
    }

    [Fact]
    public void Transport_failure_and_timeout_are_transient()
    {
        Assert.Equal("transient", AdapterEffects.ResumeFailureReason(new IOException("PRIVATE_DETAIL")));
        Assert.Equal("transient", AdapterEffects.ResumeFailureReason(new TimeoutException("PRIVATE_DETAIL")));
    }

    [Fact]
    public void Unclassified_protocol_errors_are_not_guessed_from_provider_text()
    {
        Assert.Null(AdapterEffects.ResumeFailureReason(new JsonRpcException(-32001, "session not found")));
        Assert.Null(AdapterEffects.ResumeFailureReason(new JsonRpcException(-32603, "permission denied")));
        Assert.Null(AdapterEffects.ResumeFailureReason(new InvalidOperationException("PRIVATE_DETAIL")));
    }

    [Fact]
    public void Caller_cancellation_is_not_classified_as_a_resume_failure()
    {
        Assert.Null(AdapterEffects.ResumeFailureReason(new OperationCanceledException()));
    }
}
