namespace Assembly.Remote.Record;

/// <summary><c>record --out DIR [--only a,b] -- &lt;adapter command…&gt;</c></summary>
public sealed record RecordOptions(string OutDirectory, IReadOnlySet<string> Only, string FileName, IReadOnlyList<string> Arguments)
{
    public const string Usage = "usage: Assembly.Remote record --out DIR [--only name,name] -- <adapter command...>";

    public static RecordOptions? Parse(IReadOnlyList<string> args)
    {
        string? outDirectory = null;
        var only = new HashSet<string>(StringComparer.Ordinal);

        var i = 0;
        for (; i < args.Count && args[i] != "--"; i += 2)
        {
            if (i + 1 >= args.Count)
                return null;
            switch (args[i])
            {
                case "--out": outDirectory = args[i + 1]; break;
                case "--only": only.UnionWith(args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries)); break;
                default: return null;
            }
        }

        var command = args.Skip(i + 1).ToList();
        if (outDirectory is null || command.Count == 0)
            return null;
        return new RecordOptions(Path.GetFullPath(outDirectory), only, command[0], command[1..]);
    }
}
