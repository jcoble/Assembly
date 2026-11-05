using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Diagnostics;
using Assembly.Infrastructure.Remote.Install;
using Assembly.Infrastructure.Updates;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Infrastructure.Updates;

// The app's download: called off partway, and the listing its check read.
public sealed partial class AppUpdateTests
{
    // Sends the first half of the archive, then holds until the reader gives up.
    private sealed class HeldArchive(byte[] archive) : Stream
    {
        private bool _sent;
        internal readonly TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                var half = Math.Min(buffer.Length, archive.Length / 2);
                archive.AsMemory(0, half).CopyTo(buffer);
                return half;
            }
            Reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class HoldingArchive(HttpMessageHandler answers, HeldArchive held) : DelegatingHandler(answers)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.RequestUri!.ToString().EndsWith(Archive, StringComparison.Ordinal)) response.Content = new StreamContent(held);
            return response;
        }
    }

    [Fact]
    public async Task A_download_called_off_partway_leaves_nothing_behind_and_the_app_as_it_was()
    {
        var archive = Package("2.0.0");
        var held = new HeldArchive(archive);
        var status = new MachineStatus(new Assembly.Infrastructure.Topics.Topics(), []);
        var progress = new UpdateProgress(status);
        var run = progress.Begin(null, UpdateProgress.ThisMachine, 0, downloads: true);
        var update = Update(Feed(archive, around: answers => new HoldingArchive(answers, held)), progress: progress);
        var before = Snapshot(App);

        var staging = update.StageAsync("2.0.0", _ => { }, run);
        await held.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(status.Items[0].UpdatePercent > 0, $"Partway through the download the figure is {status.Items[0].UpdatePercent}.");
        Assert.True(status.Items[0].UpdateCancellable);

        Assert.True(progress.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => staging);

        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(App)!, ".assembly-update-*"));
        Assert.Null(update.Ready);
        Assert.Equal(before, Snapshot(App));
    }

    private static string[] Snapshot(string folder) => [.. Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(file => $"{file}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)))}")];

    [Fact]
    public async Task Getting_ready_uses_the_release_listing_the_check_just_read()
    {
        if (!OperatingSystem.IsLinux()) return;
        RemoteReleaseFeedTests.Handler? asked = null;
        var feed = Feed(Package("2.0.0"), around: answers => { asked = (RemoteReleaseFeedTests.Handler)answers; return answers; });
        var update = Update(feed);

        Assert.Equal("2.0.0", (await feed.PublishedAsync(default)).App);
        await update.StageAsync("2.0.0", _ => { }, default);

        Assert.Equal("2.0.0", update.Ready);
        Assert.Single(asked!.Asked, url => url == Listing);
    }

    [Fact]
    public void A_new_version_has_five_minutes_to_say_its_window_is_up_before_it_goes_back()
    {
        var home = Path.Combine(_root, "home");
        var plan = AppSwapPlan.For(new(App, home, Path.Combine(home, "assembly.db"), "1.0.0"), "fresh", "program", "stage", "2.0.0", "backup", Environment.ProcessId);
        Assert.Equal(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(plan.AcknowledgeSeconds));
    }
}
