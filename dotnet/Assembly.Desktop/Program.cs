using Assembly.Desktop.Serve;
using Assembly.Infrastructure.Updates;

return args switch
{
    ["serve", .. var rest] => await DesktopServe.MainAsync(rest),
    ["finish-update", var plan] => await AppSwap.MainAsync(plan),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine(DesktopOptions.Usage);
    return 2;
}
