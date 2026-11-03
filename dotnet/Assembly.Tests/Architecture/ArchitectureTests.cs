using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace Assembly.Tests.Architecture;

/// <summary>
/// The layout's rules (plan section 4): the core in layers, every other part one folder per feature. It reads the
/// built assemblies for what they reference and the retained .NET and native source trees for the rest.
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
    [InlineData("Assembly.Engine")]
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
    [InlineData("Assembly.Engine")]
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
    [InlineData("Assembly.Engine")]
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
    public void Engine_references_application_contracts_and_domain_proofs()
    {
        Assert.Equal(["Assembly.Application", "Assembly.Domain"], OurReferences("Assembly.Engine"));
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
    [InlineData("Assembly.Engine")]
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
        Assert.Empty(FindCrowdedFolders(Root));
    }

    [Fact]
    public void The_root_is_found_without_the_old_app_folder()
    {
        var scratch = Directory.CreateTempSubdirectory("assembly-architecture-");
        try
        {
            var repoRoot = scratch.FullName;
            Directory.CreateDirectory(Path.Combine(repoRoot, "dotnet"));
            File.WriteAllText(Path.Combine(repoRoot, "dotnet", "Assembly.slnx"), "<Solution />");
            Directory.CreateDirectory(Path.Combine(repoRoot, "desktop", "src"));
            var nested = Path.Combine(repoRoot, "dotnet", "Assembly.Tests", "bin");
            Directory.CreateDirectory(nested);
            // An old-layout decoy must not hide the retained root above it.
            Directory.CreateDirectory(Path.Combine(nested, "dotnet"));
            Directory.CreateDirectory(Path.Combine(nested, "app"));

            Assert.False(Directory.Exists(Path.Combine(repoRoot, "app")));
            Assert.Equal(repoRoot, FindRoot(nested));
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("dotnet/Assembly.slnx")]
    [InlineData("desktop/src")]
    public void Both_retained_root_markers_are_required(string missing)
    {
        var scratch = Directory.CreateTempSubdirectory("assembly-architecture-");
        try
        {
            Directory.CreateDirectory(Path.Combine(scratch.FullName, "dotnet"));
            var solution = Path.Combine(scratch.FullName, "dotnet", "Assembly.slnx");
            var native = Path.Combine(scratch.FullName, "desktop", "src");
            if (missing != "dotnet/Assembly.slnx")
                File.WriteAllText(solution, "<Solution />");
            if (missing != "desktop/src")
                Directory.CreateDirectory(native);

            Assert.Throws<InvalidOperationException>(() => FindRoot(scratch.FullName));
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("dotnet", "Assembly.Sample")]
    [InlineData("desktop", "src")]
    public void Retained_folders_allow_thirty_files_and_reject_thirty_one(string top, string sub)
    {
        var scratch = Directory.CreateTempSubdirectory("assembly-architecture-");
        try
        {
            var repoRoot = scratch.FullName;
            Directory.CreateDirectory(Path.Combine(repoRoot, "dotnet"));
            Directory.CreateDirectory(Path.Combine(repoRoot, "desktop", "src"));
            var folder = Directory.CreateDirectory(Path.Combine(repoRoot, top, sub)).FullName;
            for (var index = 0; index < MaxFilesPerFolder; index++)
                File.WriteAllText(Path.Combine(folder, $"file{index}"), "");
            foreach (var skipped in new[] { "bin", "obj", "node_modules" })
            {
                var generated = Directory.CreateDirectory(Path.Combine(folder, skipped)).FullName;
                for (var index = 0; index <= MaxFilesPerFolder; index++)
                    File.WriteAllText(Path.Combine(generated, $"file{index}"), "");
            }

            Assert.Empty(FindCrowdedFolders(repoRoot));
            File.WriteAllText(Path.Combine(folder, "overflow"), "");
            Assert.Equal([$"{Path.GetRelativePath(repoRoot, folder)}: 31"], FindCrowdedFolders(repoRoot));
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    private static List<string> FindCrowdedFolders(string root) =>
        new[] { Path.Combine(root, "dotnet"), Path.Combine(root, "desktop", "src") }
            .SelectMany(top => Directory.EnumerateDirectories(top, "*", SearchOption.AllDirectories).Prepend(top))
            .Where(folder => !Skipped(folder))
            .Select(folder => (folder, count: Directory.GetFiles(folder).Length))
            .Where(folder => folder.count > MaxFilesPerFolder)
            .Select(folder => $"{Path.GetRelativePath(root, folder.folder)}: {folder.count}")
            .ToList();

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

    private static string FindRoot(string? start = null)
    {
        for (var folder = new DirectoryInfo(start ?? AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "dotnet", "Assembly.slnx"))
                && Directory.Exists(Path.Combine(folder.FullName, "desktop", "src")))
                return folder.FullName;
        throw new InvalidOperationException("The repo root isn't above the test's folder.");
    }

    [GeneratedRegex(@"^\s*namespace\s+([\w.]+)\s*;", RegexOptions.Multiline)]
    private static partial Regex NamespacePattern();
}
