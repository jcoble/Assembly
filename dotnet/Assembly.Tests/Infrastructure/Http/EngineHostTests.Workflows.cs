using System.Security.Cryptography;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Effects;
using Assembly.Engine.Hosting;
using Assembly.Engine.Messages;
using Assembly.Engine.Sessions;
using Assembly.Engine.Workflows.Execution;
using Assembly.Infrastructure.Git;
using Assembly.Tests.Features.Chat.Sessions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Assembly.Tests.Infrastructure.Http;

public sealed partial class EngineHostTests
{
    [Theory]
    [InlineData("desktop")]
    [InlineData("remote")]
    public async Task Both_hosts_run_definition_from_actual_sdk_to_checked_command_execution(string host)
    {
        var home = Directory.CreateTempSubdirectory("assembly-workflow-host-").FullName;
        var root = Path.Combine(home, "project");
        var launcher = new FakeLauncher();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".assembly", "workflows"));
            await File.WriteAllTextAsync(Path.Combine(root, ".assembly", "workflows", "command.yml"), """
                name: Actual command workflow
                policy: {commands: {allow: ["*"]}, default: deny}
                steps:
                  check:
                    workspace: parent
                    run: ["printf workflow-command-ok"]
                """);
            var git = new GitCli();
            async Task Git(params string[] args)
            { await foreach (var value in git.ReadAsync(root, args, null, default)) { _ = value; } }
            await Git("init"); await Git("config", "user.name", "Workflow Host Fixture");
            await Git("config", "user.email", "workflow.invalid");
            await Git("add", "."); await Git("commit", "-m", "fixture");
            await using var running = await Host.Start(host, home, null, launcher: launcher);
            using var socket = await running.Open();
            await Command(socket, "workflow-owner", new StartSession("codex", null, root, "Workflow owner", null, null, null));
            await running.WaitState("sessions", "workflow-owner", "idle");
            ((IEngineRuntime)running.Runtime).Write(scope => scope.Execute("""
                INSERT INTO projects(id,machine,root_path,title,created_at) VALUES('workflow-project',$machine,$root,'Workflow project',1);
                UPDATE engine_session SET project_id='workflow-project' WHERE id='workflow-owner';
                """, new SqlArg("$machine", running.Runtime.Machine), new("$root", root)));
            await Command(socket, "workflow-owner-prompt", new SendMessage(null, "workflow-owner", "Exercise the workflow tool", "prompt"));
            var agent = await launcher.NextAsync(); var opening = await agent.StartAsync();
            var bearer = opening.GetProperty("params").GetProperty("mcpServers")[0].GetProperty("headers")[0].GetProperty("value").GetString()![7..];
            var prompt = await agent.ReadAsync("session/prompt");
            await agent.InsertAsync(prompt, "owner-provider-message");
            using var deadline = new CancellationTokenSource(Timeout);
            var finished = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var heard = running.Runtime.Publisher.Register(item =>
            {
                if (item.EntityKind == "run" && item.Data.State == "succeeded") finished.TrySetResult(item.EntityId);
            }, out _);
            await using var client = await McpClient.CreateAsync(new HttpClientTransport(new()
            {
                Endpoint = new Uri(running.Address, "/mcp"),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + bearer }
            }), cancellationToken: deadline.Token);
            Assert.Contains(await client.ListToolsAsync(cancellationToken: deadline.Token), tool => tool.Name == "start_workflow");
            var args = new Dictionary<string, object?> { ["path"] = ".assembly/workflows/command.yml", ["request"] = "Check the isolated fixture", ["requestKey"] = "workflow-host-command" };
            var first = await client.CallToolAsync("start_workflow", args, cancellationToken: deadline.Token);
            Assert.False(first.IsError, string.Join("\n", first.Content.OfType<TextContentBlock>().Select(block => block.Text)));
            using var accepted = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(first.Content)).Text);
            var run = accepted.RootElement.GetProperty("runId").GetString()!;
            var retry = await client.CallToolAsync("start_workflow", args, cancellationToken: deadline.Token);
            Assert.False(retry.IsError);
            using var replay = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(retry.Content)).Text);
            Assert.Equal(run, replay.RootElement.GetProperty("runId").GetString());
            Assert.Equal(run, await finished.Task.WaitAsync(deadline.Token));
            var receipt = ((IEngineRuntime)running.Runtime).Read(scope =>
            {
                Assert.Equal(1, scope.QueryFirst("SELECT COUNT(*) FROM engine_workflow_start", row => row.Long(0)));
                Assert.Equal(1, scope.QueryFirst("SELECT COUNT(*) FROM engine_workflow_snapshot", row => row.Long(0)));
                Assert.Equal("succeeded", scope.QueryFirst("SELECT state FROM engine_step WHERE run_id=$run", row => row.Text(0), new SqlArg("$run", run)));
                return scope.QueryFirst("SELECT receipt_json FROM engine_outbox WHERE run_id=$run AND kind='workflow_command' AND state='done'", row => row.Text(0), new SqlArg("$run", run));
            }, deadline.Token);
            var saved = JsonSerializer.Deserialize(Assert.IsType<string>(receipt), Assembly.Engine.EngineJson.Default.EffectReceipt)!;
            Assert.Equal(EffectDisposition.Confirmed, saved.Disposition);
            var command = saved.Data.Deserialize(WorkflowExecutionJson.Default.CommandExecutionReceipt)!;
            Assert.Equal(0, command.ExitCode); Assert.False(command.Stopped); Assert.Null(command.Error);
            var bytes = await File.ReadAllBytesAsync(command.OutputReceiptPath, deadline.Token);
            Assert.Equal(command.OutputSha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            Assert.Contains("workflow-command-ok", command.Summary);
            Assert.Equal(1, launcher.Launches); // The actual owner only; command steps create no synthetic provider session.
            await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""");
            await agent.CloseAsync();
        }
        finally { Directory.Delete(home, true); }
    }
}
