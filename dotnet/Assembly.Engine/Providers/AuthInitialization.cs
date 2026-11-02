using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;

namespace Assembly.Engine.Providers;

/// <summary>Fixed restart transitions inside the one initialization writer, before effect owners exist.</summary>
internal static class AuthInitialization
{
    internal static void Interrupt(IWriteScope scope, EngineCommands commands, string machine, long now)
    {
        string? after = null;
        while (true)
        {
            var providers = scope.Query("""
                SELECT id,version FROM engine_provider WHERE machine=$machine AND auth_state='checking'
                    AND ($after IS NULL OR id>$after) ORDER BY id LIMIT 100
                """, r => (Id: r.Text(0), Version: r.Long(1)), new SqlArg("$machine", machine), new("$after", after));
            foreach (var row in providers)
            {
                var context = new CommandContext(Guid.NewGuid(), machine, new(ActorKind.System, "engine.initialize"), row.Id, row.Version, 0, now);
                if (!commands.Apply(scope, context, new InterruptProviderCheck()).Applied)
                    throw new InvalidOperationException("Provider interruption refused during initialization.");
            }
            if (providers.Count < 100) break;
            after = providers[^1].Id;
        }
        after = null;
        while (true)
        {
            var operations = scope.Query("""
                SELECT id,version FROM engine_sign_in WHERE machine=$machine AND state IN ('starting','waiting_for_person','finishing')
                    AND ($after IS NULL OR id>$after) ORDER BY id LIMIT 100
                """, r => (Id: r.Text(0), Version: r.Long(1)), new SqlArg("$machine", machine), new("$after", after));
            foreach (var row in operations)
            {
                var context = new CommandContext(Guid.NewGuid(), machine, new(ActorKind.System, "engine.initialize"), row.Id, row.Version, 0, now);
                if (!commands.Apply(scope, context, new InterruptSignIn()).Applied)
                    throw new InvalidOperationException("Sign-in interruption refused during initialization.");
            }
            if (operations.Count < 100) break;
            after = operations[^1].Id;
        }
    }
}
