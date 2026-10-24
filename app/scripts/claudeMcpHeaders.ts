// The Agent SDK puts MCP config on the CLI command line. Claude Code expands
// ${VAR} in HTTP headers; the SDK's per-query env keeps the value off argv.
// https://code.claude.com/docs/en/mcp#environment-variable-expansion-in-mcp-json
export function privateMcpHeaders(params: Record<string, any>): Record<string, any> {
  const options = params._meta?.claudeCode?.options;
  const env = { ...options?.env };
  const mcpServers = params.mcpServers?.map((server: any) => {
    if (server.type !== 'http' || server.name !== 'assembly') return server;
    return { ...server, headers: server.headers.map((header: any) => {
      if (header.name.toLowerCase() !== 'authorization') return header;
      env.ASSEMBLY_MCP_AUTHORIZATION = header.value;
      return { ...header, value: '${ASSEMBLY_MCP_AUTHORIZATION}' };
    }) };
  });
  return { ...params, mcpServers, _meta: { ...params._meta, claudeCode: {
    ...params._meta?.claudeCode, options: { ...options, env }
  } } };
}
