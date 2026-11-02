using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Features.Projects;
using Assembly.Infrastructure.Git;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Projects;
using Assembly.Infrastructure.Socket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Features.Projects;

public sealed class ProjectsSocketTests : IAsyncLifetime
{
    private readonly TempDatabase _file = new();
    private readonly string _root = Directory.CreateTempSubdirectory("project-socket-").FullName;
    private AppDatabase _db = null!;
    private WebApplication _app = null!;
    private Uri _address = null!;
    private ProjectStore _store = null!;
    public async Task InitializeAsync()
    {
        var topics = new TopicSet(); _db = AppDatabase.Open(_file.Path, TimeProvider.System, topics);
        _store = new(_db, TimeProvider.System);
        var folders = new LocalProjectFolders(new GitCli());
        var calls = new SocketDispatcher(_db.Writer, topics, TimeProvider.System, NullLogger.Instance);
        new ProjectsFeature(_store, machine => machine == "local" ? folders : null, _db).Register(calls);
        new FoldersFeature(folders).Register(calls);
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0)); _app = builder.Build(); _app.UseWebSockets();
        var endpoint = new SocketEndpoint("test-projects", calls, "test-machine", TimeProvider.System);
        _app.Map("/socket", endpoint.HandleAsync); _app.Lifetime.ApplicationStopping.Register(endpoint.CloseAll);
        await _app.StartAsync();
        var http = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        _address = new($"ws://{http.Authority}/socket");
    }
    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync(); _db.Dispose(); _file.Dispose(); Directory.Delete(_root, true);
    }
    [Fact]
    public async Task Add_returns_camel_case_inspection_and_publishes_after_commit_only()
    {
        using var observer = await Open(); using var writer = await Open();
        await Send(observer, """{"t":"sub","id":9,"topic":"projects"}""");
        Assert.Equal(0, (await Read(observer)).GetProperty("items")[0].GetProperty("item").GetProperty("revision").GetInt64());
        var added = Result(await Call(writer, "add", $$"""{"machine":"local","path":{{JsonSerializer.Serialize(_root)}}}"""));
        Assert.False(added.GetProperty("alreadyExisted").GetBoolean());
        Assert.False(added.GetProperty("inspection").GetProperty("isGit").GetBoolean());
        var project = added.GetProperty("project"); var id = project.GetProperty("id").GetString()!;
        Assert.Equal(_root, project.GetProperty("rootPath").GetString());
        Assert.Equal("local", project.GetProperty("machine").GetString());
        var changed = await Read(observer); Assert.Equal(1, changed.GetProperty("item").GetProperty("revision").GetInt64());
        Assert.Equal(id, _store.AtRoot("local", _root)!.Id);
        var again = Result(await Call(writer, "add", $$"""{"machine":"local","path":{{JsonSerializer.Serialize(_root)}}}"""));
        Assert.True(again.GetProperty("alreadyExisted").GetBoolean()); Assert.Equal(1, _store.Revision(default));
    }
    [Fact]
    public async Task Plain_folder_refs_are_bad_args_and_do_not_leak_runtime_exception_details()
    {
        using var socket = await Open();
        var added = Result(await Call(socket, "add", $$"""{"machine":"local","path":{{JsonSerializer.Serialize(_root)}}}"""));
        var id = added.GetProperty("project").GetProperty("id").GetString();
        var refused = await Call(socket, "refs", $$"""{"projectId":"{{id}}","limit":100}""");
        Assert.Equal("bad_args", refused.GetProperty("code").GetString());
        Assert.Equal("This folder is not a Git repository.", refused.GetProperty("message").GetString());
        Assert.Equal(1, _store.Revision(default));
    }
    [Theory]
    [InlineData("list", "{\"limit\":0}")]
    [InlineData("list", "{\"limit\":101}")]
    [InlineData("rename", "{\"id\":\"missing\",\"title\":\"  \"}")]
    [InlineData("add", "{}")]
    public async Task Malformed_arguments_are_bad_args_without_writes(string method, string args)
    {
        using var socket = await Open(); var refused = await Call(socket, method, args);
        Assert.Equal("bad_args", refused.GetProperty("code").GetString()); Assert.Equal(0, _store.Revision(default));
    }
    [Fact]
    public async Task Missing_project_and_machine_stay_not_found_and_missing_folder_stays_failed()
    {
        using var socket = await Open();
        Assert.Equal("not_found", (await Call(socket, "inspect", """{"projectId":"missing"}""")).GetProperty("code").GetString());
        Assert.Equal("not_found", (await Call(socket, "folders", """{"machine":"missing","path":"~"}""")).GetProperty("code").GetString());
        var absent = Path.Combine(_root, "missing");
        var failed = await Call(socket, "add", $$"""{"machine":"local","path":{{JsonSerializer.Serialize(absent)}}}""");
        Assert.Equal("failed", failed.GetProperty("code").GetString()); Assert.Equal("That folder does not exist.", failed.GetProperty("message").GetString());
        Assert.Equal(0, _store.Revision(default));
    }
    [Fact]
    public async Task Folder_browsing_with_a_slash_uses_the_requested_machine_and_real_next_directory()
    {
        var child = Directory.CreateDirectory(Path.Combine(_root, "next directory")).FullName;
        var deeper = Directory.CreateDirectory(Path.Combine(child, "deeper")).FullName;
        using var socket = await Open();
        var first = Result(await Call(socket, "folders", $$"""{"machine":"local","path":{{JsonSerializer.Serialize(_root + "/")}}}"""));
        Assert.Equal(_root, first.GetProperty("path").GetString());
        Assert.Equal(child, first.GetProperty("directories")[0].GetString());
        var next = Result(await Call(socket, "folders", $$"""{"machine":"local","path":{{JsonSerializer.Serialize(child + "/")}}}"""));
        Assert.Equal(deeper, next.GetProperty("directories")[0].GetString());
        Assert.Equal(0, _store.Revision(default));
    }
    [Fact]
    public async Task Remove_default_hides_project_keeps_files_and_readd_restores_identity()
    {
        File.WriteAllText(Path.Combine(_root, "keep"), "unchanged"); using var socket = await Open();
        var added = Result(await Call(socket, "add", $$"""{"machine":"local","path":{{JsonSerializer.Serialize(_root)}}}"""));
        var id = added.GetProperty("project").GetProperty("id").GetString();
        var removed = Result(await Call(socket, "remove", $$"""{"id":"{{id}}"}"""));
        Assert.Equal(JsonValueKind.Number, removed.GetProperty("project").GetProperty("removedAt").ValueKind);
        Assert.Equal(0, Result(await Call(socket, "list", "{}")).GetProperty("projects").GetArrayLength());
        Assert.Equal("unchanged", File.ReadAllText(Path.Combine(_root, "keep")));
        var restored = Result(await Call(socket, "add", $$"""{"machine":"local","path":{{JsonSerializer.Serialize(_root)}}}"""));
        Assert.Equal(id, restored.GetProperty("project").GetProperty("id").GetString());
    }
    private async Task<ClientWebSocket> Open()
    {
        var socket = new ClientWebSocket(); await socket.ConnectAsync(_address, default);
        await Send(socket, """{"t":"hello","protocol":1,"token":"test-projects"}""");
        Assert.Equal("welcome", (await Read(socket)).GetProperty("t").GetString()); return socket;
    }
    private static async Task<JsonElement> Call(WebSocket socket, string method, string args)
    {
        await Send(socket, $$"""{"t":"call","id":1,"feature":"projects","method":"{{method}}","args":{{args}}}""");
        return await Read(socket);
    }
    private static JsonElement Result(JsonElement frame)
    {
        Assert.Equal("result", frame.GetProperty("t").GetString()); return frame.GetProperty("result");
    }
    private static Task Send(WebSocket socket, string frame) => socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, default);
    private static async Task<JsonElement> Read(WebSocket socket)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var bytes = new MemoryStream();
        var buffer = new byte[8192]; WebSocketReceiveResult part;
        do { part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token); bytes.Write(buffer, 0, part.Count); }
        while (!part.EndOfMessage);
        using var json = JsonDocument.Parse(bytes.ToArray()); return json.RootElement.Clone();
    }
}
