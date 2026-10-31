using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Persistence;

namespace Assembly.Application.Features.Engine;

public sealed class EngineCommands
{
    private readonly string _machine;

    public EngineCommands(string machine, Func<int> childLimit)
    {
        _machine = machine;
        ArgumentNullException.ThrowIfNull(childLimit);
    }

    public CommandResult Apply(IWriteScope scope, CommandContext context, EngineCommand command)
    {
        var canonical = JsonSerializer.Serialize(command, EngineJson.Default.EngineCommand);
        return EngineJournal.Execute(scope, context, canonical, () =>
        {
            if (context.Machine != _machine)
                return new(false, false, "wrong_machine", context.ExpectedVersion, 0, context.Epoch);
            // The owning tasks add typed cases and their ownership/version/epoch checks here.
            // Each transition gathers its drafts and calls Commit once. No production command exists yet.
            return command switch
            {
                _ => new(false, false, "unsupported_command", context.ExpectedVersion, 0, context.Epoch)
            };
        });
    }
}
