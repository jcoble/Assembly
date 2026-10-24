import assert from 'node:assert/strict';

import type { OwnedSession } from '../src/lib/shell/ownedSessions.ts';
import {
  createTerminalService,
  type TerminalBackend,
  type TerminalViewHooks
} from '../src/lib/shell/terminal/terminalService.ts';
import type { TerminalOutputEnvelope } from '../src/lib/tauriSource.ts';

const writes: string[] = [];
const closed: string[] = [];
const exits: string[] = [];
let disposedViews = 0;
let liveViews = 0;
let maxLiveViews = 0;
let listener: ((envelope: TerminalOutputEnvelope) => void) | null = null;

const backend: TerminalBackend = {
  start: async () => null,
  write: async () => true,
  resize: async () => true,
  close: async (sessionId) => {
    closed.push(sessionId);
    return true;
  },
  readScrollback: async () => ({ data: 'saved output', generation: 1, sequence: 0 }),
  list: async () => [],
  listen: async (handler) => {
    listener = handler;
    return () => { listener = null; };
  }
};

const owned: OwnedSession = {
  ownedId: 'owned-a',
  agent: 'claude',
  viaCmux: false,
  source: 'scanned',
  title: 'A',
  projectPath: '/repo',
  cwd: '/repo',
  resumeCommand: null,
  nativeSessionId: 'native-a',
  ptySessionId: 'pty-a',
  state: 'background',
  completedAt: null,
  settledAt: null,
  branch: null,
  taskId: null,
  pullRequest: null,
  messageCount: null,
  latestTurnPreview: null,
  lastActivity: null
};

const service = createTerminalService({
  backend,
  createView: (_host: HTMLElement, _hooks: TerminalViewHooks) => {
    liveViews += 1;
    maxLiveViews = Math.max(maxLiveViews, liveViews);
    return {
      write: (data) => writes.push(data),
      fit: () => undefined,
      focus: () => undefined,
      setVisible: () => undefined,
      dispose: () => {
        disposedViews += 1;
        liveViews -= 1;
      }
    };
  },
  onExit: (ownedId) => exits.push(ownedId)
});

await service.attach();
assert.equal(service.trackExisting(owned, { cols: 96, rows: 28 }), true);
assert.deepEqual(writes, [], 'tracking an inactive PTY does not allocate or hydrate a view');

const host = { isConnected: true } as HTMLElement;
assert.equal(await service.adoptExisting(owned, host, { cols: 96, rows: 28 }), true);
assert.deepEqual(writes, ['saved output']);

service.releaseView(owned.ownedId);
assert.equal(disposedViews, 1);
assert.deepEqual(closed, [], 'releasing a frontend view must not close its PTY');

listener?.({
  sessionId: 'pty-a',
  generation: 1,
  sequence: 1,
  byteLength: 17,
  kind: 'terminal-output',
  chunk: {
    sessionId: 'pty-a',
    data: 'background output',
    terminated: true,
    exitCode: 0,
    signal: null
  }
});
assert.deepEqual(writes, ['saved output'], 'inactive output stays out of WebKit');
assert.deepEqual(exits, ['owned-a'], 'the lightweight PTY mapping still routes exits');

await service.closeOwned(owned.ownedId);
assert.deepEqual(closed, ['pty-a']);

let activeOwnedId: string | null = null;
for (let index = 0; index < 12; index += 1) {
  if (activeOwnedId) service.releaseView(activeOwnedId);
  const session = {
    ...owned,
    ownedId: `owned-${index}`,
    ptySessionId: `pty-${index}`,
    title: `Session ${index}`
  };
  service.trackExisting(session, { cols: 96, rows: 28 });
  await service.adoptExisting(session, host, { cols: 96, rows: 28 });
  activeOwnedId = session.ownedId;
  assert.equal(liveViews, 1, `switch ${index + 1} retains only its active xterm view`);
}
assert.equal(maxLiveViews, 1, 'twelve session switches never overlap xterm views');
service.dispose();
assert.equal(liveViews, 0);

console.log('terminal view lifecycle tests passed');
