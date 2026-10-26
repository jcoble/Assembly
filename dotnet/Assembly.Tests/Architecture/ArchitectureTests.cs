using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace Assembly.Tests.Architecture;

/// <summary>
/// The layout's rules (plan section 4): the core in layers, every other part one folder per feature. It reads the
/// built assemblies for what they reference and the source tree for the rest. There are no exceptions.
/// </summary>
public sealed partial class ArchitectureTests
{
    /// <summary>The most files one folder may hold, counting the files directly in it. Jesse's number.</summary>
    private const int MaxFilesPerFolder = 30;

    private static readonly string Root = FindRoot();

    [Fact]
    public void Domain_references_no_other_assembly_of_ours()
    {
        Assert.Empty(OurReferences("Assembly.Domain"));
    }

    [Fact]
    public void Application_references_only_domain()
    {
        Assert.Equal(["Assembly.Domain"], OurReferences("Assembly.Application"));
    }

    [Theory]
    [InlineData("Assembly.Domain")]
    [InlineData("Assembly.Application")]
    public void The_core_uses_no_sqlite_web_server_or_process(string name)
    {
        var references = Load(name).GetReferencedAssemblies().Select(reference => reference.Name!).ToList();
        Assert.DoesNotContain(references, reference => reference.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal));
        Assert.DoesNotContain(references, reference => reference.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        // Starting a process stays in Infrastructure: no type of System.Diagnostics.Process may be named at all.
        Assert.DoesNotContain(TypeReferences(name), type => type.StartsWith("System.Diagnostics.Process", StringComparison.Ordinal));
    }

    [Fact]
    public void Infrastructure_references_no_host()
    {
        var references = OurReferences("Assembly.Infrastructure");
        Assert.DoesNotContain("Assembly.Mac", references);
        Assert.DoesNotContain("Assembly.Remote", references);
    }

    [Theory]
    [InlineData("Assembly.Domain")]
    [InlineData("Assembly.Application")]
    [InlineData("Assembly.Infrastructure")]
    public void Every_file_s_namespace_matches_its_folder(string project)
    {
        var folder = Path.Combine(Root, "dotnet", project);
        var wrong = SourceFiles(folder)
            .Select(file => (file, expected: Expected(project, folder, file), found: NamespaceOf(file)))
            .Where(file => file.found != file.expected)
            .Select(file => $"{Relative(file.file)}: {file.found ?? "no namespace"}, not {file.expected}")
            .ToList();
        Assert.Empty(wrong);
    }

    [Fact]
    public void No_backend_feature_names_another()
    {
        var features = Path.Combine(Root, "dotnet", "Assembly.Application", "Features");
        Assert.True(Directory.Exists(features), $"{Relative(features)} doesn't exist yet");
        var names = Directory.GetDirectories(features).Select(Path.GetFileName).ToList();
        var crossings = (
            from feature in names
            from file in SourceFiles(Path.Combine(features, feature!))
            let text = File.ReadAllText(file)
            from other in names
            where other != feature && Regex.IsMatch(text, $@"\bAssembly\.Application\.Features\.{other}\b")
            select $"{Relative(file)} names {other}").ToList();
        Assert.Empty(crossings);
    }

    [Fact]
    public void No_page_feature_imports_another()
    {
        var lib = Path.Combine(Root, "app", "src", "lib");
        string[] core = ["host", "components", "time"];
        var features = Directory.GetDirectories(lib).Select(Path.GetFileName).Where(name => !core.Contains(name)).ToList();
        var crossings = (
            from feature in features
            from file in Directory.EnumerateFiles(Path.Combine(lib, feature!), "*", SearchOption.AllDirectories)
            where file.EndsWith(".ts", StringComparison.Ordinal) || file.EndsWith(".svelte", StringComparison.Ordinal)
            from import in ImportPattern().Matches(File.ReadAllText(file)).Select(match => match.Groups[1].Value)
            where import.StartsWith('.') || import.StartsWith("$lib/", StringComparison.Ordinal)
            let target = import.StartsWith('.')
                ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, import))
                : Path.GetFullPath(Path.Combine(lib, import["$lib/".Length..]))
            let reached = FeatureOf(lib, target)
            where reached is not null && reached != feature && features.Contains(reached)
            select $"{Relative(file)} imports {import}").ToList();
        Assert.Empty(crossings);
    }

    [Fact]
    public void No_folder_holds_more_than_the_limit()
    {
        var crowded = new[] { Path.Combine(Root, "dotnet"), Path.Combine(Root, "app", "src") }
            .SelectMany(top => Directory.EnumerateDirectories(top, "*", SearchOption.AllDirectories).Prepend(top))
            .Where(folder => !Skipped(folder))
            .Select(folder => (folder, count: Directory.GetFiles(folder).Length))
            .Where(folder => folder.count > MaxFilesPerFolder)
            .Select(folder => $"{Relative(folder.folder)}: {folder.count}")
            .ToList();
        Assert.Empty(crowded);
    }

    private static System.Reflection.Assembly Load(string name) => System.Reflection.Assembly.Load(new AssemblyName(name));

    private static List<string> OurReferences(string name) =>
        Load(name).GetReferencedAssemblies().Select(reference => reference.Name!)
            .Where(reference => reference.StartsWith("Assembly.", StringComparison.Ordinal)).Order().ToList();

    private static List<string> TypeReferences(string name)
    {
        using var stream = File.OpenRead(Load(name).Location);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.TypeReferences.Select(metadata.GetTypeReference)
            .Select(type => $"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}").ToList();
    }

    private static IEnumerable<string> SourceFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories).Where(file => !Skipped(file));

    private static string Expected(string project, string folder, string file)
    {
        var sub = Path.GetRelativePath(folder, Path.GetDirectoryName(file)!);
        return sub == "." ? project : $"{project}.{sub.Replace(Path.DirectorySeparatorChar, '.')}";
    }

    private static string? NamespaceOf(string file) =>
        NamespacePattern().Match(File.ReadAllText(file)) is { Success: true } match ? match.Groups[1].Value : null;

    private static string? FeatureOf(string lib, string target)
    {
        var relative = Path.GetRelativePath(lib, target);
        if (relative.StartsWith("..", StringComparison.Ordinal))
            return null;
        var parts = relative.Split(Path.DirectorySeparatorChar);
        return parts.Length > 1 ? parts[0] : null;
    }

    private static bool Skipped(string path) =>
        path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or "node_modules");

    private static string Relative(string path) => Path.GetRelativePath(Root, path);

    private static string FindRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (Directory.Exists(Path.Combine(folder.FullName, "dotnet")) && Directory.Exists(Path.Combine(folder.FullName, "app")))
                return folder.FullName;
        throw new InvalidOperationException("The repo root isn't above the test's folder.");
    }

    [GeneratedRegex(@"^\s*namespace\s+([\w.]+)\s*;", RegexOptions.Multiline)]
    private static partial Regex NamespacePattern();

    [GeneratedRegex(@"(?:from|import)\s*\(?\s*['""]([^'""]+)['""]")]
    private static partial Regex ImportPattern();
}
