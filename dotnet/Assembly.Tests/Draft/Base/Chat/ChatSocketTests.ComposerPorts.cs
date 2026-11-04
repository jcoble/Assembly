using System.Text.Json;
using Assembly.Domain.Sessions;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Tests.Draft.Base.Chat;

public sealed partial class ChatSocketTests
{
    [Fact]
    public async Task Composer_port_discovery_preserves_raw_config_and_creates_no_session()
    {
        const string options = """[{"id":"raw/模型","category":"model","unknown":{"keep":true},"options":[{"value":"m/二","name":"Model 二"}]}]""";
        const string commands = """[{"name":"review","description":"Inspect","input":{"hint":"file/路径"}}]""";
        _box.Config = new(options, new Dictionary<string, string> { ["raw/模型"] = "m/二" }, commands);
        using var socket = await OpenAsync();
        await CallAsync(socket, 1, "config", "discover", """{"agent":"claude","targetId":"selected-target","cwd":"/fixture/项目","machine":"box"}""");
        using var reply = JsonDocument.Parse(await ReadAsync(socket));
        Assert.Equal("result", reply.RootElement.GetProperty("t").GetString());
        var result = reply.RootElement.GetProperty("result");
        using var expectedOptions = JsonDocument.Parse(options);
        using var expectedCommands = JsonDocument.Parse(commands);
        Assert.True(JsonElement.DeepEquals(expectedOptions.RootElement, result.GetProperty("options")));
        Assert.True(JsonElement.DeepEquals(expectedCommands.RootElement, result.GetProperty("commands")));
        Assert.Equal("m/二", result.GetProperty("choices").GetProperty("raw/模型").GetString());
        Assert.Equal(JsonValueKind.Object, result.GetProperty("modelOptions").ValueKind);
        Assert.Equal(["discover claude selected-target /fixture/项目"], _box.Asked);
        Assert.Empty(_sessions.List(null, 50, default).Sessions);
        Assert.Empty(_here.Asked);
    }

    [Fact]
    public async Task Composer_port_warm_uses_the_existing_session_owner_and_exact_target()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        _box.Config = new("[]", new Dictionary<string, string> { ["effort"] = "medium" });
        using var socket = await OpenAsync();
        await CallAsync(socket, 1, "config", "warm", $$"""{"session":"{{session.SessionId}}","targetId":"selected-target"}""");
        using var reply = JsonDocument.Parse(await ReadAsync(socket));
        Assert.Equal("result", reply.RootElement.GetProperty("t").GetString());
        Assert.Equal("medium", reply.RootElement.GetProperty("result").GetProperty("choices").GetProperty("effort").GetString());
        Assert.Equal(["warm config selected-target"], _box.Asked);
        Assert.Empty(_here.Asked);
    }

    [Fact]
    public async Task Composer_port_set_forwards_the_selected_target_without_changing_legacy_shape()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        using var socket = await OpenAsync();
        await CallAsync(socket, 1, "config", "set", $$"""{"session":"{{session.SessionId}}","configId":"raw/id","value":"raw/value","targetId":"selected-target"}""");
        using var reply = JsonDocument.Parse(await ReadAsync(socket));
        Assert.Equal("result", reply.RootElement.GetProperty("t").GetString());
        Assert.Equal(["choices", "commands", "options"], reply.RootElement.GetProperty("result").EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(["set config raw/id raw/value target selected-target"], _box.Asked);
    }

    [Fact]
    public async Task Composer_port_create_saves_initial_choices_and_actual_folder_before_reply()
    {
        using var socket = await OpenAsync();
        var target = ConfigTargetIdentity.Capture(_launcher, "claude").TargetId;
        var project = CreateProject("项目");
        var create = new SessionsFeature.CreateArgs("claude", "composer_choices", project.Id,
            InitialConfig: new(target, new Dictionary<string, string> { ["raw/模型"] = "m/二", ["effort"] = "medium" }),
            ExpectedSource: new(_engine.Machine, 0, null));
        await CallAsync(socket, 1, "sessions", "create", JsonSerializer.Serialize(create, SessionsJson.Default.CreateArgs));
        using var reply = JsonDocument.Parse(await ReadAsync(socket));
        Assert.Equal("result", reply.RootElement.GetProperty("t").GetString());
        var id = reply.RootElement.GetProperty("result").GetProperty("sessionId").GetString()!;
        Assert.Equal(project.RootPath, _sessions.Get(id)!.WorkingFolder);
        Assert.Equal(project.Id, _sessions.Get(id)!.ProjectId);
        Assert.Equal(target, _sessions.Config(id).ConfigTarget);
        Assert.Equal("m/二", _sessions.Config(id).Choices["raw/模型"]);
        Assert.Equal("medium", _sessions.Config(id).Choices["effort"]);
    }

    [Theory]
    [InlineData("initialConfig", "{\"targetId\":\"selected-target\",\"choices\":{\"model\":\"b\"}}")]
    [InlineData("workingFolder", "\"/fixture/folder\"")]
    public async Task Composer_port_helper_refuses_owner_form_payload_without_creation(string field, string value)
    {
        using var socket = await OpenAsync();
        await CallAsync(socket, 1, "sessions", "create", $$"""{"agent":"claude","clientId":"invalid_helper","parentSessionId":"parent","{{field}}":{{value}}}""");
        using var reply = JsonDocument.Parse(await ReadAsync(socket));
        Assert.Equal("error", reply.RootElement.GetProperty("t").GetString());
        Assert.Equal(field == "workingFolder" ? "bad_args" : "wrong_machine", reply.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("helper", _here.Asked);
        Assert.Empty(_sessions.List(null, 50, default).Sessions);
    }

    [Fact]
    public async Task Composer_port_cancel_reaches_the_owned_discovery()
    {
        _box.Hold();
        using var socket = await OpenAsync();
        await CallAsync(socket, 1, "config", "discover", """{"agent":"claude","targetId":"selected-target","cwd":"/fixture/folder","machine":"box"}""");
        var read = ReadAsync(socket);
        var first = await Task.WhenAny(_box.Reached.Task, read).WaitAsync(Wait);
        if (ReferenceEquals(first, read))
        {
            using var early = JsonDocument.Parse(await read);
            Assert.Equal("cancelled", early.RootElement.GetProperty("code").GetString());
        }
        await _box.Reached.Task.WaitAsync(Wait);
        await SendAsync(socket, """{"t":"cancel","id":1}""");
        await _box.Cancelled.Task.WaitAsync(Wait);
        Assert.Contains("\"code\":\"cancelled\"", await read);
        Assert.Empty(_sessions.List(null, 50, default).Sessions);
    }
}
