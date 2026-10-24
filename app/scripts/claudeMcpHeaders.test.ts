import assert from 'node:assert/strict';
import { privateMcpHeaders } from './claudeMcpHeaders.ts';

const token = 'test-session-secret';
const request = {
  mcpServers: [{ type: 'http', name: 'assembly', url: 'http://127.0.0.1:1234/mcp', headers: [{ name: 'Authorization', value: `Bearer ${token}` }] }],
  _meta: { claudeCode: { options: { allowedTools: ['mcp__assembly'], env: { EXISTING: 'kept' } } } }
};
const secured = privateMcpHeaders(request);
assert.equal(JSON.stringify(secured.mcpServers).includes(token), false);
assert.equal(secured.mcpServers[0].headers[0].value, '${ASSEMBLY_MCP_AUTHORIZATION}');
assert.equal(secured._meta.claudeCode.options.env.ASSEMBLY_MCP_AUTHORIZATION, `Bearer ${token}`);
assert.equal(secured._meta.claudeCode.options.env.EXISTING, 'kept');
assert.deepEqual(secured._meta.claudeCode.options.allowedTools, ['mcp__assembly']);
console.log('private MCP headers: ok');
