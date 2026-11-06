using Assembly.DevSeed;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    var request = SeedJson.Parse<SeedRequest>(await Console.In.ReadToEndAsync(cancellation.Token));
    var result = DevSeed.Run(request, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), cancellation.Token);
    Console.WriteLine(SeedJson.Stringify(result));
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"Dev seed refused: {error.Message}");
    return 1;
}
