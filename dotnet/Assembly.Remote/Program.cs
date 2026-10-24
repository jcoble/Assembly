using Assembly.Remote.Record;
using Assembly.Remote.Replay;
using Assembly.Remote.Serve;
using Assembly.Remote.Smoke;

return args switch
{
    ["smoke", .. var rest] => await SmokeRun.MainAsync(rest),
    ["record", .. var rest] => await RecordRun.MainAsync(rest),
    ["rows", .. var rest] => ReplayRun.Run(rest),
    ["serve", .. var rest] => await ServeRun.MainAsync(rest),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine(SmokeOptions.Usage);
    Console.Error.WriteLine(RecordOptions.Usage);
    Console.Error.WriteLine(ReplayRun.Usage);
    Console.Error.WriteLine(ServeOptions.Usage);
    return 2;
}
