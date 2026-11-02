using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Engine.Sessions;

namespace Assembly.Infrastructure.Engine;

public sealed partial class MachineCalls
{
    private void RegisterMachineStartAck(ICallRegistry calls)
    {
        Register(false, "start_ack");
        Register(true, "start_ack_bulk");

        void Register(bool bulk, string method) => calls.Write("engine", method,
            EngineSocketJson.Default.MachineStartAckArgs, EngineSocketJson.Default.MachineStartAck,
            (args, call) => Relay<Func<IWriteScope, MachineStartAck>>(call, operation =>
            {
                try
                {
                    var prepared = runtime.Reader.PrepareMachineStartAck(args, operation.Token);
                    return Task.FromResult(operation.Return<Func<IWriteScope, MachineStartAck>>(scope =>
                        runtime.Reader.ReadMachineStartAck(scope, prepared)));
                }
                catch (Exception error) when (error is ArgumentException or JsonException)
                { throw new CallFailed("bad_args"); }
            }), order: args => args.FromMachine, bulk: bulk);
    }
}
