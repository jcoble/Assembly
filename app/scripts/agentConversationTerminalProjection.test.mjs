import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const conversation = path.join(root, 'src-tauri/src/agent_conversation');
const projectionPath = path.join(conversation, 'terminal_projection.rs');
const projection = fs.readFileSync(projectionPath, 'utf8');
const productionProjection = projection.split('#[cfg(test)]')[0];
const manager = fs.readFileSync(path.join(conversation, 'manager.rs'), 'utf8');
const main = fs.readFileSync(path.join(root, 'src-tauri/src/main.rs'), 'utf8');
const projectionStreams = fs.readFileSync(path.join(root, 'src-tauri/src/projection_streams.rs'), 'utf8');
const transcriptRoot = path.join(conversation, 'history/transcript');

assert.equal(fs.existsSync(path.join(conversation, 'transcript.rs')), false, 'legacy monolithic transcript reader must be removed');
for (const file of ['mod.rs', 'codex.rs', 'claude.rs']) {
  assert.equal(fs.existsSync(path.join(transcriptRoot, file)), true, `missing transcript/${file}`);
}
assert.match(projection, /canonical_path: Option<PathBuf>/, 'projection caches the canonical path');
assert.match(projection, /identity: Option<FileIdentity>/, 'projection caches file identity');
assert.match(projection, /offset: u64/, 'projection retains its read offset');
assert.match(projection, /partial_line: Vec<u8>/, 'projection retains an incomplete final line');
for (const reason of ['Truncated', 'Rotated', 'Archived', 'Gap', 'Periodic']) {
  assert.match(projection, new RegExp(`ReconcileReason::${reason}`), `missing ${reason.toLowerCase()} reconciliation`);
}
assert.match(projection, /MAX_RECONCILIATION_EVENTS/, 'reconciliation output must be bounded');
assert.match(projection, /impl Drop for TerminalProjectionRegistry/, 'app-state drop must stop all watchers');
assert.match(projection, /previous\.stop\(\)/, 'replacement must stop and join the prior watcher');
assert.match(projection, /watcher\.stop\(\)/, 'explicit stop must stop and join the watcher');
assert.match(productionProjection, /manager\.submit_terminal_projection\(/, 'projection must enter the shared conversation journal');
assert.match(manager, /record_payload_for_session_and_dispatch\([\s\S]*AgentConversationPayload::TerminalProjection\(projection\)/,
  'projected events must be journaled and dispatched through the shared manager');
assert.match(main, /projection_streams\.publish_agent_event\(event\.clone\(\)\)/,
  'the shared manager emitter must publish conversation events');
assert.match(projectionStreams, /BoundedProjectionStream::new\("agent-conversation-event"\)/,
  'conversation events must use the canonical bounded frontend stream');
assert.doesNotMatch(productionProjection, /AgentEventType::(?:TurnStarted|ApprovalRequested|ItemStarted|ItemUpdated)/,
  'durable transcript projection must not invent live turns, approvals, or active item/tool state');
assert.doesNotMatch(productionProjection, /portable_pty|CommandBuilder|spawn_command/,
  'terminal projection must never create a PTY or process');
assert.match(productionProjection, /"historical"\.into\(\), serde_json::Value::Bool\(true\)/,
  'every emitted event must be marked historical in provider metadata');

const fixtureRoot = path.join(root, 'src-tauri/fixtures/agent_conversation/terminal_projection');
for (const file of ['codex_append.jsonl', 'claude_append.jsonl', 'no_invented_state.jsonl']) {
  const lines = fs.readFileSync(path.join(fixtureRoot, file), 'utf8').trim().split('\n');
  assert.ok(lines.length > 0, `${file} must not be empty`);
  for (const line of lines) assert.doesNotThrow(() => JSON.parse(line), `${file} must contain durable JSONL`);
}

console.log('agentConversationTerminalProjection contract: PASS');
