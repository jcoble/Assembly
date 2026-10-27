using Assembly.Desktop.Serve;

return args switch
{
    ["serve", .. var rest] => await DesktopServe.MainAsync(rest),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine(DesktopOptions.Usage);
    return 2;
}
