// How background work is put into words: the rail label, the chat line's
// elapsed times and link targets, who the quit warning names, and the Agents
// panel's state words. Logic only; nothing here renders a component.
import assert from 'node:assert/strict';
import {
  backgroundWorkLabel,
  backgroundWorkSummary,
  backgroundWorkTarget,
  formatBackgroundElapsed,
  localBackgroundWorkSessions,
  type OwnedSession
} from '../src/lib/shell/ownedSessions.ts';
import { AGENT_STATUS_TONE, AGENT_STATUS_WORD, agentStatus } from '../src/lib/shell/panels/agents/agentActivityModel.ts';
import type { BackgroundWorkItem } from '../src/lib/tauriSource.ts';

const subagent = (id: string, label = 'Explore auth'): BackgroundWorkItem =>
  ({ id: `claude-child:${id}`, kind: 'subagent', label, startedAtMs: 1_000 });
const command = (id: string, label = 'npm test'): BackgroundWorkItem =>
  ({ id: `background-task:${id}`, kind: 'command', label, startedAtMs: 1_000 });

{ // the rail label counts each kind and says nothing when nothing runs
  assert.equal(backgroundWorkSummary([]), null);
  assert.equal(backgroundWorkSummary([subagent('a')]), '1 sub-agent running');
  assert.equal(backgroundWorkSummary([subagent('a'), subagent('b')]), '2 sub-agents running');
  assert.equal(backgroundWorkSummary([command('a')]), '1 background command running');
  assert.equal(backgroundWorkSummary([command('a'), command('b')]), '2 background commands running');
  assert.equal(backgroundWorkSummary([subagent('a'), command('b')]), '1 sub-agent · 1 command running');
  assert.equal(backgroundWorkSummary([subagent('a'), subagent('b'), command('c'), command('d')]),
    '2 sub-agents · 2 commands running');
}

{ // elapsed time reads as seconds, then minutes and seconds, then hours and minutes
  assert.equal(formatBackgroundElapsed(-5_000), '0s');
  assert.equal(formatBackgroundElapsed(14_900), '14s');
  assert.equal(formatBackgroundElapsed(134_000), '2m 14s');
  assert.equal(formatBackgroundElapsed(3_600_000 + 5 * 60_000 + 9_000), '1h 5m');
}

{ // an empty label falls back to what kind of work it is
  assert.equal(backgroundWorkLabel(subagent('a', 'Review diff')), 'Review diff');
  assert.equal(backgroundWorkLabel(subagent('a', '  ')), 'Sub-agent');
  assert.equal(backgroundWorkLabel(command('a', '')), 'Background command');
}

{ // a sub-agent link opens that child; a command link finds its tool row
  assert.deepEqual(backgroundWorkTarget(subagent('agent-7')), { kind: 'child', childId: 'agent-7' });
  assert.deepEqual(backgroundWorkTarget(command('shell-1')), { kind: 'tool', itemId: 'background-task:shell-1' });
}

{ // only sessions on this Mac with work running hold up a quit
  const session = (ownedId: string, environment: 'local' | 'remote', work: BackgroundWorkItem[]) =>
    ({ ownedId, executionEnvironment: environment, backgroundWork: work }) as unknown as OwnedSession;
  const sessions = [
    session('local-busy', 'local', [subagent('a')]),
    session('local-idle', 'local', []),
    session('remote-busy', 'remote', [command('b')]),
    session('local-command', 'local', [command('c')])
  ];
  assert.deepEqual(localBackgroundWorkSessions(sessions).map((entry) => entry.ownedId), ['local-busy', 'local-command']);
  assert.deepEqual(localBackgroundWorkSessions([session('remote-busy', 'remote', [command('b')])]), []);
}

{ // the Agents panel names ended children plainly; unknown states stay Idle
  assert.equal(AGENT_STATUS_WORD[agentStatus('disconnected')], 'Disconnected');
  assert.equal(AGENT_STATUS_WORD[agentStatus('cancelled')], 'Stopped');
  assert.equal(AGENT_STATUS_WORD[agentStatus('canceled')], 'Stopped');
  assert.equal(AGENT_STATUS_TONE[agentStatus('disconnected')], 'neutral');
  assert.equal(AGENT_STATUS_TONE[agentStatus('cancelled')], 'neutral');
  assert.equal(AGENT_STATUS_WORD[agentStatus('something-new')], 'Idle');
  assert.equal(AGENT_STATUS_WORD[agentStatus('running')], 'Working');
}

console.log('backgroundWorkPresentation tests passed');
