using System.Text;
using Assembly.Engine;
using Assembly.Engine.Relays;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine.SessionTitles;

public sealed class StartTitlePayloadTests
{
    [Theory]
    [InlineData("provider")]
    [InlineData("model")]
    [InlineData("folder")]
    [InlineData("parent")]
    [InlineData("run")]
    [InlineData("task")]
    [InlineData("repo")]
    [InlineData("managed")]
    public void All_original_Start_facts_are_bound_to_the_full_canonical_before_writer_admission(string field)
    {
        var command = new StartSession("codex", null, "/folder", "Full\0Title😀", "parent", null, null);
        var changed = field switch
        {
            "provider" => command with { Provider = "other" },
            "model" => command with { Model = "model" },
            "folder" => command with { Folder = "/changed" },
            "parent" => command with { ParentId = "changed" },
            "run" => command with { RunId = "run" },
            "task" => command with { TaskUrl = "https://task" },
            "repo" => command with { RepoUrl = "https://repo" },
            _ => command with { ManagedCheckout = false }
        };
        var prepared = EngineCommands.PrepareSessionTitle(command); var facts = StartTitlePayload.Facts(changed); var title = Encoding.UTF8.GetBytes(command.Title);
        var query = new MachineStartAckArgs(Guid.NewGuid(), "source", Guid.NewGuid().ToString(), 0, 0, MachineStartCanonical.FromPrepared(prepared));
        var request = new StartTitleBeginArgs(1, "target", query, new(facts.LongLength, RelayHash.Sha(facts)), new(title.LongLength, RelayHash.Sha(title)));
        Assert.Throws<ArgumentException>(() => StartTitlePayload.Prepare("target", request, facts, title));
    }
}
