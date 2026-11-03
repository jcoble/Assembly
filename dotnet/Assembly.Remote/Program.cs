using Assembly.Remote.Record;
using Assembly.Remote.Serve;

return args switch
{
    ["record", .. var rest] => await RecordRun.MainAsync(rest),
    ["serve", .. var rest] => await ServeRun.MainAsync(rest),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine(RecordOptions.Usage);
    Console.Error.WriteLine(ServeOptions.Usage);
    return 2;
}
