import assert from 'node:assert/strict';
import { test } from 'node:test';
import { agentItemFromEvent, displayItemFromAgentItem } from '../src/lib/shell/conversation/conversationTimeline.ts';
import type { AgentConversationEvent, AgentItem } from '../src/lib/shell/conversation/conversationTypes.ts';

test('commands keep their command kind when their output includes a diff', () => {
  const diff = '@@ -1 +1 @@\n-old\n+new';
  const native: AgentItem = { id: 'native-command', type: 'command', content: [{ channel: 'command-output', text: diff }], providerMetadata: { diff } };
  const event = (toolKind: string) => ({
    ownedId: 'command-kind', provider: 'codex', generation: 1, sequence: 1, timestampMs: 1,
    payload: { kind: 'toolCall', toolCallId: 'command', toolKind, name: 'git diff', diff, output: diff, status: 'completed' }
  } as unknown as AgentConversationEvent);
  for (const item of [native, agentItemFromEvent(event('command'))!]) {
    const row = displayItemFromAgentItem(item);
    assert.equal(row.kind, 'tool');
    assert.equal(row.kind === 'tool' && row.toolKind, 'command');
    assert.equal(row.kind === 'tool' && row.output, diff);
  }
  const edit = displayItemFromAgentItem(agentItemFromEvent(event('file-edit'))!);
  assert.equal(edit.kind === 'tool' && edit.toolKind, 'file-edit');
});
