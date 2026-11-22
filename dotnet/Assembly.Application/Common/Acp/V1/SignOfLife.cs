namespace Assembly.Application.Common.Acp.V1;

/// <summary>
/// Version 1 has no proof that a prompt was taken. It counts as taken at the agent's first turn content for that
/// session: a message or thought chunk or a tool call. A permission request or a successful reply also counts (the
/// client says so where those arrive). Housekeeping such as usage, config, command or mode updates does not: an agent
/// may send those and then refuse the prompt.
/// </summary>
internal static class SignOfLife
{
    public static bool Counts(string updateKind) =>
        updateKind is "agent_message_chunk" or "agent_thought_chunk" or "tool_call" or "tool_call_update";
}
