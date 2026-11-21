using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Infrastructure.Engine.Handoff;

public sealed class AdapterResumeReasonTests
{
    [Fact]
    public void Caller_cancellation_is_not_classified_as_a_resume_failure()
    {
        Assert.Null(AdapterEffects.ResumeFailureReason(new OperationCanceledException()));
    }
}
