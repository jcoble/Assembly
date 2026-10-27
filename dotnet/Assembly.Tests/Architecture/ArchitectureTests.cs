using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace Assembly.Tests.Architecture;

/// <summary>
/// The layout's rules (plan section 4): the core in layers, every other part one folder per feature. It reads the
/// built assemblies for what they reference and the source tree for the rest. There are no exceptions. The page's
/// rule (no feature folder imports another) joins in C1, when chat's page folders move under lib/chat/.
/// </summary>
public sealed partial class ArchitectureTests
{
    /// <summary>
    /// The most files one folder may hold, counting the files directly in it. 30 is the plan's number, and it's Jesse's to
    /// change (plan section 12).
    /// </summary>
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

    [Theory]
    [InlineData("Assembly.Domain")]
    [InlineData("Assembly.Application")]
    public void The_core_opens_no_files(string name)
    {
        // Files on disk are an outside system: Infrastructure and the hosts open them and hand the core a stream or text.
        string[] banned = ["System.IO.File", "System.IO.Directory", "System.IO.FileStream", "System.IO.FileInfo", "System.IO.DirectoryInfo"];
        Assert.DoesNotContain(TypeReferences(name), banned.Contains);
        Assert.Empty(PathConstructors(name));
    }

    [Theory]
    [InlineData("Assembly.Domain")]
    [InlineData("Assembly.Application")]
    public void The_core_makes_no_native_calls(string name)
    {
        // Native calls go in Infrastructure. A LibraryImport's generated stub is a DllImport, so this catches both.
        using var stream = File.OpenRead(Load(name).Location);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var native = metadata.MethodDefinitions.Select(metadata.GetMethodDefinition)
            .Where(method => (method.Attributes & MethodAttributes.PinvokeImpl) != 0)
            .Select(method => $"{metadata.GetString(metadata.GetTypeDefinition(method.GetDeclaringType()).Name)}.{metadata.GetString(method.Name)}")
            .ToList();
        Assert.Empty(native);
    }

    [Fact]
    public void Infrastructure_references_no_host()
    {
        var references = OurReferences("Assembly.Infrastructure");
        Assert.DoesNotContain("Assembly.Desktop", references);
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
    public void No_feature_names_another()
    {
        // A feature is a folder under Application/Features/. Its code there, and its outside systems in the
        // Infrastructure folder of the same name, never name another feature's namespace in either project.
        var features = Path.Combine(Root, "dotnet", "Assembly.Application", "Features");
        Assert.True(Directory.Exists(features), $"{Relative(features)} doesn't exist yet");
        var names = Directory.GetDirectories(features).Select(folder => Path.GetFileName(folder)!).ToList();
        var crossings = (
            from feature in names
            from folder in new[] { Path.Combine(features, feature), Path.Combine(Root, "dotnet", "Assembly.Infrastructure", feature) }
            where Directory.Exists(folder)
            from file in SourceFiles(folder)
            let text = File.ReadAllText(file)
            from other in names
            where other != feature
                  && Regex.IsMatch(text, $@"\bAssembly\.(Application\.Features|Infrastructure)\.{other}\b")
            select $"{Relative(file)} names {other}").ToList();
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

    /// <summary>Every <c>new StreamWriter(path, …)</c> or <c>new StreamReader(path, …)</c>: a constructor whose first parameter is a string.</summary>
    private static List<string> PathConstructors(string name)
    {
        using var stream = File.OpenRead(Load(name).Location);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var found = new List<string>();
        foreach (var handle in metadata.MemberReferences)
        {
            var member = metadata.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference || metadata.GetString(member.Name) != ".ctor")
                continue;
            var type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
            var typeName = $"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}";
            if (typeName is not ("System.IO.StreamWriter" or "System.IO.StreamReader"))
                continue;
            var signature = metadata.GetBlobReader(member.Signature);
            signature.ReadSignatureHeader();
            if (signature.ReadCompressedInteger() > 0 && signature.ReadSignatureTypeCode() == SignatureTypeCode.Void
                && signature.ReadSignatureTypeCode() == SignatureTypeCode.String)
                found.Add($"new {typeName}(string, …)");
        }
        return found;
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
}
