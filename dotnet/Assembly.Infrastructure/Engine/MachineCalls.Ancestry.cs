using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;

namespace Assembly.Infrastructure.Engine;

public sealed partial class MachineCalls
{
    private void RegisterForeignAncestry(ICallRegistry calls) =>
        calls.Write("engine", "foreign_ancestry", EngineSocketJson.Default.ForeignAncestryArgs, EngineJson.Default.CommandResult,
            (args, call) =>
            {
                RequireOwner(call);
                if (string.IsNullOrWhiteSpace(args.FromMachine) || args.FromMachine == runtime.Machine)
                    throw new CallFailed("forbidden");
                PreparedForeignAncestryBatch prepared;
                try { prepared = runtime.Commands.PrepareForeignAncestry(args); }
                catch (ForeignAncestryRefused refusal) { throw new CallFailed(refusal.Result.Error!); }
                return Task.FromResult<Func<IWriteScope, CommandResult>>(scope =>
                {
                    try { return runtime.Commands.ApplyForeignAncestryBatch(scope, prepared); }
                    catch (ForeignAncestryRefused refusal)
                    {
                        // Keep throwing through AtomicWriter: no partial FK/marker/reference may commit.
                        throw new CallFailed(refusal.Result.Error!);
                    }
                });
            }, order: args => args.FromMachine);
}
