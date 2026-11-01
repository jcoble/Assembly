using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Hosting;
using Assembly.Engine.Recording;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Assembly.Engine.Mcp;

public sealed class McpRecorder(IEngineRuntime runtime, McpGate gate, TimeProvider time)
{
    private static readonly McpJson ArgumentJson = new(new JsonSerializerOptions(McpJson.Default.Options)
    { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    public McpRequestFilter<CallToolRequestParams, CallToolResult> Filter => next => async (context, _) =>
    {
        var session = context.User?.FindFirst("assembly:session")?.Value;
        var epochText = context.User?.FindFirst("assembly:epoch")?.Value;
        var name = context.Params?.Name ?? "";
        if (string.IsNullOrWhiteSpace(session) || !long.TryParse(epochText, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch))
            return Refused(name, new("session_gone"));

        var callId = Guid.NewGuid().ToString("N");
        Attribution? attribution = null;
        McpRefusal? refusal = null;
        try
        {
            var arguments = JsonSerializer.SerializeToElement(context.Params?.Arguments is { } supplied
                ? new Dictionary<string, JsonElement>(supplied) : null, ArgumentJson.DictionaryStringJsonElement);
            runtime.Write(scope =>
            {
                CallerRow? row = null;
                try { row = gate.Resolve(scope, new(session, epoch)); }
                catch (McpRefused error) { refusal = error.Refusal; }
                if (row is null)
                {
                    refusal ??= new("session_gone");
                    if (scope.QueryFirst("SELECT COUNT(*) FROM engine_session WHERE id=$id", r => r.Long(0), new SqlArg("$id", session)) == 0)
                        return 0;
                    attribution = new(session, null, null, null, epoch);
                }
                else attribution = new(row.SessionId, row.RunId, row.StepId, row.Attempt, row.Epoch);
                Apply(scope, callId, 0, new ToolStarted(attribution, "mcp", callId, name, arguments, Bytes(arguments)));
                return 0;
            });
        }
        catch (Exception)
        {
            // Nothing executes unless the start write returned after commit. Storage details never enter an agent reply.
            return Refused(name, new("recording_failed"));
        }
        if (attribution is null) return Refused(name, refusal ?? new("session_gone"));

        CallToolResult result;
        JsonElement output;
        try
        {
            // A submitted engine write must finish even when the HTTP client disconnects.
            result = refusal is null ? await next(context, CancellationToken.None) : Refused(name, refusal);
            output = JsonSerializer.SerializeToElement(result, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(CallToolResult)));
        }
        catch (Exception)
        {
            // Exception messages may contain credentials; record only a fixed failure value and rethrow the original.
            End(callId, attribution, "failed", JsonSerializer.SerializeToElement("failed", EngineJson.Default.String));
            throw;
        }
        End(callId, attribution, result.IsError == true ? "refused" : "ok", output);
        return result;
    };

    private void End(string callId, Attribution attribution, string outcome, JsonElement output) =>
        runtime.Write(scope =>
        {
            Apply(scope, callId, 1, new ToolEnded(attribution, outcome, output, Bytes(output)));
            return 0;
        });

    private void Apply(IWriteScope scope, string id, long version, RecordingCommand command)
    {
        var epoch = command switch
        {
            ToolStarted start => start.Attribution.Epoch,
            ToolEnded end => end.Attribution.Epoch,
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
        var context = new CommandContext(Guid.NewGuid(), runtime.Machine, new(ActorKind.System, "mcp"),
            id, version, epoch, time.GetUtcNow().ToUnixTimeMilliseconds());
        if (!runtime.Commands.Apply(scope, context, command).Applied)
            throw new InvalidOperationException("Assembly could not persist MCP recording.");
    }

    private static long Bytes(JsonElement value) => Encoding.UTF8.GetByteCount(value.GetRawText());
    private static CallToolResult Refused(string name, McpRefusal refusal) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = Refusals.Text(name, refusal) }] };
}
