using Assembly.Mac.Serve;
using Assembly.Mac.Window;

return args switch
{
    ["serve", .. var rest] => await MacServe.MainAsync(rest),
    // Synchronous, so the window runs on the main thread.
    ["window", .. var rest] => MacWindow.Run(rest),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine(MacOptions.Usage);
    return 2;
}
