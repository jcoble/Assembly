using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;
using Assembly.Infrastructure.Engine;

namespace Assembly.Infrastructure.Remote;

internal sealed partial class RemoteMachine
{
    private static ExpectedSource LocalSource(EngineMirror.LiveMirrorBinding live) => new(live.Machine, 0, null);

    internal async Task<bool> StopBoundAsync(string session, string run, EngineMirror.LiveMirrorBinding live, CancellationToken ct)
    {
        try { return (await BoundChatAsync("send", "stop", new SendFeature.StopArgs(run, session, LocalSource(live)), live, ct)).GetProperty("ok").GetBoolean(); }
        catch (CallFailed error) when (error.Message == "that run isn't running") { return false; }
    }

    internal async Task<bool> AnswerBoundAsync(string session, string ask, JsonElement answer, EngineMirror.LiveMirrorBinding live, CancellationToken ct)
    {
        try { return (await BoundChatAsync("send", "answer", new SendFeature.AnswerArgs(session, ask, answer, LocalSource(live)), live, ct)).GetProperty("ok").GetBoolean(); }
        catch (CallFailed error) when (error.Message == "that ask isn't open, or the answer doesn't fit it") { return false; }
    }

    internal async Task<bool> StopTaskBoundAsync(string session, string task, EngineMirror.LiveMirrorBinding live, CancellationToken ct)
    {
        try { return (await BoundChatAsync("send", "stopTask", new SendFeature.StopTaskArgs(session, task, LocalSource(live)), live, ct)).GetProperty("ok").GetBoolean(); }
        catch (CallFailed error) when (error.Message == "that task isn't running, or can't be stopped") { return false; }
    }

    internal async Task<SessionConfig> ConfigBoundAsync(string session, EngineMirror.LiveMirrorBinding live, CancellationToken ct) =>
        ReadConfig(await BoundChatAsync("config", "get", new ConfigFeature.GetArgs(session, LocalSource(live)), live, ct));

    internal async Task<SessionConfig> WarmConfigBoundAsync(string session, string target, EngineMirror.LiveMirrorBinding live, CancellationToken ct) =>
        ReadConfig(await BoundChatAsync("config", "warm", new ConfigFeature.WarmArgs(session, UnwrapTarget(target), ExpectedSource: LocalSource(live)), live, ct));

    internal async Task<SessionConfig> SetConfigBoundAsync(string session, string config, string value, string? target,
        EngineMirror.LiveMirrorBinding live, CancellationToken ct) =>
        ReadConfig(await BoundChatAsync("config", "set", new ConfigFeature.SetArgs(session, config, value,
            target is null ? null : UnwrapTarget(target), LocalSource(live)), live, ct));

    private async Task<JsonElement> BoundChatAsync(string feature, string method, object args,
        EngineMirror.LiveMirrorBinding live, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, live.Invalidated, live.OwnerBinding.Invalidated);
        return await CallAsync(feature, method, JsonSerializer.SerializeToElement(args, args.GetType(), SessionsJson.Default),
            false, stop.Token, live.Machine, expectedOwner: live.OwnerBinding, expectedMirror: live);
    }

    internal async Task<OwnerPromptPage> ReadBoundOwnerPromptsAsync(OwnerPromptReadArgs request,
        Func<OwnerPromptPage, int> encodedBytes, EngineMirror.LiveMirrorBinding live, CancellationToken ct)
    {
        async Task<OwnerPromptPage> Read(OwnerPromptReadArgs args) =>
            (await BoundChatAsync("engine", "owner_prompts", args with { Machine = null, ExpectedSource = LocalSource(live) }, live, ct))
                .Deserialize(EngineSocketJson.Default.OwnerPromptPage) ?? throw new IOException("Invalid owner prompt page");
        var page = await Read(request);
        if (encodedBytes(page) > 64 * 1024)
        {
            if (page.Items.Count < 2) throw new CallFailed("owner response exceeds budget");
            page = await Read(request with { Limit = page.Items.Count - 1 });
            if (encodedBytes(page) > 64 * 1024) throw new CallFailed("owner response exceeds budget");
        }
        return page;
    }

    internal async Task<OwnerPromptChunk> ReadBoundOwnerPromptChunkAsync(OwnerPromptChunkArgs request,
        EngineMirror.LiveMirrorBinding live, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, live.Invalidated, live.OwnerBinding.Invalidated);
        var args = request with { Machine = null, ExpectedSource = LocalSource(live) };
        var result = await CallAsync("engine", "owner_prompt_chunk", JsonSerializer.SerializeToElement(args, EngineSocketJson.Default.OwnerPromptChunkArgs),
            true, stop.Token, live.Machine, expectedOwner: live.OwnerBinding, expectedMirror: live);
        return result.Deserialize(EngineSocketJson.Default.OwnerPromptChunk) ?? throw new IOException("Invalid owner prompt chunk");
    }
}
