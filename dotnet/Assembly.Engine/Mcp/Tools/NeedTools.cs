using Assembly.Engine.NeedsMe;
using ModelContextProtocol.Server;

namespace Assembly.Engine.Mcp.Tools;

/// <summary>The two need tools share the host's one checked answer service.</summary>
public static class NeedTools
{
    public static IReadOnlyList<McpServerTool> Create(McpGate gate, INeedAnswers needs) =>
        [new AnswerNeedTool(gate, needs), new AskOwnerTool(gate, needs)];
}
