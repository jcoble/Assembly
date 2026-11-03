using Assembly.Application.Common.Engine;
using Assembly.Engine.Handoffs;
using Assembly.Infrastructure.Engine.Handoff;

namespace Assembly.Tests.Infrastructure.Engine.Handoff;

/// <summary>SOURCE-DERIVED boundary fixtures; these captures are not production launch witnesses.</summary>
public sealed class NativeSessionFileInputsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("native-file-inputs-").FullName;

    private NativeSessionCapture Capture()
    {
        var storage = new ProviderFileContext("machine", "configured-launcher", "claude", "", "accepted-launch",
            _root, null);
        var current = new NativeSessionWitness("assembly-session", 7, storage.MachineId, storage.ProviderId,
            storage.AccountContext, storage.LaunchBindingId, "public-alias", "current-native", _root,
            "projects/selected/current-native.jsonl", null, "single_file_jsonl", "none");
        return new(storage, current);
    }

    [Fact]
    public void Structural_validation_preserves_public_alias_and_native_identity_separately()
    {
        var capture = Capture();
        NativeSessionFileInputs.Validate(capture);
        Assert.NotEqual(capture.Current.AcpSessionId, capture.Current.ResumeId);
        Assert.Equal(Path.Combine(_root, "projects", "selected", "current-native.jsonl"),
            NativeSessionFileInputs.ResolveProviderPath(capture.Storage.ProviderHome, capture.Current.SelectedRelativePath));
    }

    private static HandoffReleaseEffectData Release(NativeSessionCapture capture) =>
        new(new("a1234567-1111-4111-8111-111111111111", capture.Current.MachineId, capture.Current.SessionId,
            "target-machine", capture.Current.Epoch, capture.Current.Epoch + 1), capture.Current.SessionId,
            capture.Current.Epoch, capture.Current.AcpSessionId, "actual-operation", "release-effect", 1);

    [Fact]
    public void Release_crosscheck_uses_public_ACP_without_conflating_the_native_resume_id()
    {
        var capture = Capture();
        NativeSessionFileInputs.ValidateRelease(capture, Release(capture));
        Assert.Equal("unavailable", Assert.Throws<HandoffRefused>(() => NativeSessionFileInputs.ValidateRelease(capture,
            Release(capture) with { ProviderSessionId = capture.Current.ResumeId })).Message);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("machine")]
    [InlineData("session")]
    [InlineData("epoch")]
    [InlineData("operation")]
    [InlineData("effect")]
    [InlineData("attempt")]
    public void Absent_or_mismatched_positive_release_data_cannot_bind_the_source(string change)
    {
        var capture = Capture();
        var release = Release(capture);
        var changed = change switch
        {
            "missing" => null,
            "machine" => release with { Identity = release.Identity with { SourceMachine = "other" } },
            "session" => release with { SessionId = "other" },
            "epoch" => release with { Epoch = release.Epoch + 1 },
            "operation" => release with { AdapterOperationId = "" },
            "effect" => release with { ReleaseEffectId = "" },
            _ => release with { ReleaseAttempt = 0 }
        };
        Assert.Equal("unavailable", Assert.Throws<HandoffRefused>(() =>
            NativeSessionFileInputs.ValidateRelease(capture, changed)).Message);
    }

    [Theory]
    [InlineData("machine")]
    [InlineData("provider")]
    [InlineData("account")]
    [InlineData("launch")]
    public void Storage_cannot_lend_another_binding_to_a_current_witness(string field)
    {
        var capture = Capture();
        var storage = field switch
        {
            "machine" => capture.Storage with { MachineId = "other" },
            "provider" => capture.Storage with { ProviderId = "other" },
            "account" => capture.Storage with { AccountContext = "other" },
            _ => capture.Storage with { LaunchBindingId = "other" }
        };
        Assert.Equal("bad_args", Assert.Throws<HandoffRefused>(() =>
            NativeSessionFileInputs.Validate(capture with { Storage = storage })).Message);
    }

    [Theory]
    [InlineData("unknown", "none")]
    [InlineData("paginated", "none")]
    [InlineData("single_file_jsonl", "zstd")]
    public void Unverified_or_unsupported_history_format_is_not_plain_JSONL(string mode, string compression)
    {
        var capture = Capture();
        Assert.Equal("unsupported", Assert.Throws<HandoffRefused>(() => NativeSessionFileInputs.Validate(capture with
        { Current = capture.Current with { HistoryMode = mode, Compression = compression } })).Message);
    }

    [Theory]
    [InlineData("../outside.jsonl")]
    [InlineData("projects/../outside.jsonl")]
    [InlineData("/outside.jsonl")]
    [InlineData("projects//file.jsonl")]
    [InlineData("projects\\file.jsonl")]
    public void Selected_paths_are_relative_and_have_no_traversal_or_empty_components(string path)
    {
        Assert.Equal("bad_args", Assert.Throws<HandoffRefused>(() =>
            NativeSessionFileInputs.ResolveProviderPath(_root, path)).Message);
    }

    [Fact]
    public void Provider_root_and_selected_components_must_not_be_links()
    {
        var actual = Path.Combine(_root, "actual");
        Directory.CreateDirectory(actual);
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, actual);
        Assert.Throws<HandoffRefused>(() => NativeSessionFileInputs.ResolveProviderPath(link, "file.jsonl"));
        Assert.Throws<HandoffRefused>(() => NativeSessionFileInputs.ResolveProviderPath(_root, "link/file.jsonl"));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
