using Assembly.Mac.Serve;

return args switch
{
    ["serve", .. var rest] => await MacServe.MainAsync(rest),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine(MacOptions.Usage);
    return 2;
}
