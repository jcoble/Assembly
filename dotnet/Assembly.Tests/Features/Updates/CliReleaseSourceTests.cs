using System.Net;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Updates;
using Microsoft.Extensions.Time.Testing;

namespace Assembly.Tests.Features.Updates;

public sealed class CliReleaseSourceTests
{
    [Theory]
    [InlineData("claude", "@anthropic-ai/claude-code", "https://registry.npmjs.org/@anthropic-ai%2Fclaude-code/latest")]
    [InlineData("codex", "@openai/codex", "https://registry.npmjs.org/@openai%2Fcodex/latest")]
    public async Task Latest_binds_provider_package_source_and_completion_time(string provider, string package, string url)
    {
        var clock = new FakeTimeProvider();
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal(url, request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType);
            clock.Advance(TimeSpan.FromSeconds(2));
            return Task.FromResult(Response(request, JsonSerializer.Serialize(new { name = package, version = "12.34.56", extra = new { safe = true } })));
        });
        using var client = new HttpClient(handler);
        var candidate = await new CliReleaseSource(client, clock).LatestAsync(provider, default);
        Assert.Equal(new CliCandidate(provider, "12.34.56", url, clock.GetUtcNow().ToUnixTimeMilliseconds()), candidate);
        // The leaf does not dispose the caller's client or cache the first result.
        await new CliReleaseSource(client, clock).LatestAsync(provider, default);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("0.0.0")]
    [InlineData("1.2.3+stable.000-build")]
    [InlineData("99999999999999999999.2.3")]
    public async Task Stable_versions_are_preserved(string version)
    {
        using var client = Client($$"""{"name":"@openai/codex","version":"{{version}}"}""");
        Assert.Equal(version, (await new CliReleaseSource(client, TimeProvider.System).LatestAsync("codex", default)).Version);
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.3-beta.1")]
    [InlineData("1.2.3 ")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3+x..y")]
    [InlineData("1.2.3+x+y")]
    [InlineData("１.2.3")]
    public async Task Nonstable_or_invalid_versions_are_unverified(string version)
    {
        using var client = Client($$"""{"name":"@openai/codex","version":"{{version}}"}""");
        await Unverified(new CliReleaseSource(client, TimeProvider.System));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"name\":\"@openai/codex\",\"version\":123}")]
    [InlineData("{\"name\":\"@anthropic-ai/claude-code\",\"version\":\"1.2.3\"}")]
    [InlineData("{\"name\":\"@openai/codex\",\"name\":\"@openai/codex\",\"version\":\"1.2.3\"}")]
    [InlineData("{\"name\":\"@openai/codex\",\"version\":\"1.2.3\",\"version\":\"9.9.9\"}")]
    [InlineData("{\"name\":null,\"version\":\"1.2.3\"}")]
    public async Task Malformed_or_wrong_package_metadata_is_unverified(string body)
    {
        using var client = Client(body);
        await Unverified(new CliReleaseSource(client, TimeProvider.System));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("Codex")]
    [InlineData("")]
    [InlineData("codex/../../secret")]
    public async Task Unsupported_provider_does_not_make_a_request(string provider)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("must not request"));
        using var client = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<CallFailed>(() => new CliReleaseSource(client, TimeProvider.System).LatestAsync(provider, default));
        Assert.Equal("CLI release check unsupported provider", failure.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task Http_failure_is_unverified_without_response_text(int status)
    {
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(Response(request, "private body", (HttpStatusCode)status))));
        await Unverified(new CliReleaseSource(client, TimeProvider.System));
    }

    [Fact]
    public async Task Network_failure_is_sanitized_and_not_retried()
    {
        using var handler = new Handler((_, _) => throw new HttpRequestException("private proxy details"));
        using var client = new HttpClient(handler);
        await Unverified(new CliReleaseSource(client, TimeProvider.System));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Redirected_metadata_cannot_be_bound_to_the_fixed_source()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response(new HttpRequestMessage(HttpMethod.Get, "https://example.com/latest"), Valid))));
        await Unverified(new CliReleaseSource(client, TimeProvider.System));
    }

    [Fact]
    public async Task Invalid_utf8_is_unverified()
    {
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent([0xff])
        })));
        await Unverified(new CliReleaseSource(client, TimeProvider.System));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Metadata_size_is_bounded_even_without_content_length(bool declared)
    {
        using var client = new HttpClient(new Handler((request, _) =>
        {
            HttpContent content = declared ? new StringContent(new string(' ', 65537)) : new UnknownLengthContent(new MemoryStream(new byte[65537]));
            if (!declared) content.Headers.ContentLength = null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content });
        }));
        await Unverified(new CliReleaseSource(client, TimeProvider.System));
    }

    [Fact]
    public async Task Caller_cancellation_before_request_is_preserved()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("must not request"));
        using var client = new HttpClient(handler);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CliReleaseSource(client, TimeProvider.System).LatestAsync("codex", cancel.Token));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Caller_cancellation_and_owned_deadline_have_distinct_results(bool callerCancels)
    {
        var clock = new FakeTimeProvider();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, ct) =>
        {
            reached.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        });
        using var client = new HttpClient(handler);
        using var caller = new CancellationTokenSource();
        var pending = new CliReleaseSource(client, clock).LatestAsync("codex", caller.Token);
        await reached.Task;
        if (callerCancels) caller.Cancel();
        else clock.Advance(TimeSpan.FromSeconds(10));
        if (callerCancels) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else Assert.Equal("CLI release check unverified", (await Assert.ThrowsAsync<CallFailed>(() => pending)).Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Body_read_is_cancelled_and_stream_is_disposed(bool callerCancels)
    {
        var clock = new FakeTimeProvider();
        using var stream = new WaitingStream();
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new UnknownLengthContent(stream)
        })));
        using var caller = new CancellationTokenSource();
        var pending = new CliReleaseSource(client, clock).LatestAsync("codex", caller.Token);
        await stream.Reached.Task;
        if (callerCancels) caller.Cancel();
        else clock.Advance(TimeSpan.FromSeconds(10));
        if (callerCancels) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else Assert.Equal("CLI release check unverified", (await Assert.ThrowsAsync<CallFailed>(() => pending)).Message);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Body_io_failure_is_unverified_and_disposes_stream()
    {
        using var stream = new WaitingStream(fail: true);
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new UnknownLengthContent(stream)
        })));
        await Unverified(new CliReleaseSource(client, TimeProvider.System));
        Assert.True(stream.Disposed);
    }

    private sealed class UnknownLengthContent(Stream stream) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context) => stream.CopyToAsync(destination);
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
        protected override void Dispose(bool disposing)
        {
            if (disposing) stream.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class WaitingStream(bool fail = false) : MemoryStream
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reached.TrySetResult();
            if (fail) throw new IOException("private read details");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private const string Valid = "{\"name\":\"@openai/codex\",\"version\":\"1.2.3\"}";
    private static HttpClient Client(string body) => new(new Handler((request, _) => Task.FromResult(Response(request, body))));
    private static HttpResponseMessage Response(HttpRequestMessage request, string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static async Task Unverified(CliReleaseSource source) =>
        Assert.Equal("CLI release check unverified", (await Assert.ThrowsAsync<CallFailed>(() => source.LatestAsync("codex", default))).Message);

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return send(request, ct);
        }
    }
}
