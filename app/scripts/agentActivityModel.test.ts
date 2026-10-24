import assert from 'node:assert/strict';
import { agentActivityRows } from '../src/lib/shell/panels/agents/agentActivityModel.ts';

{ // a_running_helper_waiting_on_the_owner_needs_you
  const child = (childId: string) => ({ childId, title: childId, state: 'running', updatedAtMs: 1 });
  const rows = agentActivityRows([child('asks'), child('busy')] as never, {}, new Set(['asks']));
  assert.deepEqual(rows.map((row) => [row.childId, row.status]), [['asks', 'waiting'], ['busy', 'working']]);
}
console.log('agentActivityModel tests passed');
