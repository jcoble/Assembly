using Assembly.Engine.Helpers;
using Assembly.Engine.Threads;

namespace Assembly.Tests.Helpers;

public sealed class MessageFrameTests
{
    [Fact]
    public void A_framed_message_names_its_sender_and_a_person_s_prompt_names_none()
    {
        var from = new ThreadInfo("0f40f1b5-1111-2222-3333-444455556666", "codex", "https://github.com/o/r", "Count the lines", null, 0);
        Assert.Equal(from.ThreadId, MessageFrame.SenderOf(MessageFrame.Of("m1", from, "Which file?")));
        Assert.Null(MessageFrame.SenderOf("Which file?"));
        Assert.Null(MessageFrame.SenderOf("Count the lines.\n\nMessage m1 from another agent's session (thread x, \"t\")"));
    }

    [Fact]
    public void A_message_that_waited_says_how_long_and_a_fresh_one_says_nothing()
    {
        var from = new ThreadInfo("t1", "codex", "https://github.com/o/r", "Count the lines", null, 0);
        Assert.Contains("It was sent 12 min ago", MessageFrame.Of("m1", from, "Which file?", TimeSpan.FromMinutes(12.5)));
        Assert.DoesNotContain("min ago", MessageFrame.Of("m1", from, "Which file?", TimeSpan.FromSeconds(20)));
        Assert.Equal("t1", MessageFrame.SenderOf(MessageFrame.Of("m1", from, "Which file?", TimeSpan.FromMinutes(12))));
    }
}
