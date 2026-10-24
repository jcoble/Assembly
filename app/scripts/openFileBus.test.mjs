import assert from 'node:assert/strict';
import {
  onOpenFile,
  requestOpenConversationFile,
  requestOpenFile,
  resolveConversationFilePath
} from '../src/lib/shell/openFileBus.ts';

assert.equal(
  resolveConversationFilePath('src/App.svelte', '/work/session'),
  '/work/session/src/App.svelte',
  'relative conversation links resolve against the session cwd'
);
assert.equal(
  resolveConversationFilePath('/outside/App.svelte', '/work/session'),
  '/outside/App.svelte',
  'absolute conversation links pass through'
);
assert.equal(
  resolveConversationFilePath('~/notes/todo.md', '/work/session'),
  '~/notes/todo.md',
  'tilde-prefixed conversation links pass through'
);

// request before any subscriber is parked and delivered to the first subscriber
{
  requestOpenFile({ path: '/a.ts', line: 3 });
  const seen = [];
  const off = onOpenFile((request) => seen.push(request));
  assert.deepEqual(seen, [{ path: '/a.ts', line: 3 }], 'parked request delivered on subscribe');
  off();
}

// live delivery to all subscribers; unsubscribe stops delivery
{
  const a = [];
  const b = [];
  const offA = onOpenFile((request) => a.push(request.path));
  const offB = onOpenFile((request) => b.push(request.path));
  requestOpenFile({ path: '/b.ts' });
  assert.deepEqual(a, ['/b.ts']);
  assert.deepEqual(b, ['/b.ts']);
  offA();
  requestOpenFile({ path: '/c.ts' });
  assert.deepEqual(a, ['/b.ts'], 'unsubscribed listener not called');
  assert.deepEqual(b, ['/b.ts', '/c.ts']);
  offB();
}

// with a live subscriber nothing is parked: a later subscriber gets nothing old
{
  const first = [];
  const off = onOpenFile((request) => first.push(request.path));
  requestOpenFile({ path: '/d.ts' });
  off();
  const late = [];
  const offLate = onOpenFile((request) => late.push(request.path));
  assert.deepEqual(late, [], 'delivered requests are not replayed');
  offLate();
}

// A tool-call link outside the session's project opens the full path the call
// recorded, read-only, on the session's own machine.
{
  const seen = [];
  const off = onOpenFile((request) => seen.push(request));
  const outside = '/home/me/other-repo/src/remote.rs';
  const local = { cwd: '/work/session', executionEnvironment: 'local' };
  assert.equal(requestOpenConversationFile(local, 'remote.rs', { path: outside }), null);
  const remote = { cwd: '/work/session', executionEnvironment: 'remote', remoteProfileId: 'box' };
  assert.equal(requestOpenConversationFile(remote, outside, { root: '/work/session' }), null);
  assert.deepEqual(seen, [
    { path: outside, projectRoot: '/work/session', readOnly: true, line: undefined },
    {
      path: `assembly-remote://box${outside}`,
      projectRoot: 'assembly-remote://box/work/session',
      readOnly: true,
      line: undefined
    }
  ]);
  off();
}

// File links in a conversation (tool calls, agent text) open through
// requestOpenConversationFile. Each block below pins one rule of where the link
// lands and whether the editor may save it.
const opened = [];
const offConversation = onOpenFile((request) => opened.push(request));
const localSession = { cwd: '/work/session/', executionEnvironment: 'local', remoteProfileId: null };

// A relative link with a line suffix opens inside the session folder, editable, at that line.
{
  opened.length = 0;
  assert.equal(requestOpenConversationFile(localSession, 'src/App.svelte:12'), null);
  assert.deepEqual(opened, [{ path: '/work/session/src/App.svelte', projectRoot: '/work/session', readOnly: false, line: 12 }]);
}

// A link outside the workspace still opens, read-only, rooted at the session (dcbacf631).
{
  opened.length = 0;
  assert.equal(requestOpenConversationFile(localSession, '/elsewhere/notes.md'), null);
  assert.deepEqual(opened, [{ path: '/elsewhere/notes.md', projectRoot: '/work/session', readOnly: true, line: undefined }]);
}

// A link that climbs with ".." is refused with a sentence, and nothing opens.
{
  opened.length = 0;
  assert.equal(requestOpenConversationFile(localSession, '../other/secret.ts'), 'That file link could not be opened.');
  assert.deepEqual(opened, []);
}

// A file a tool call recorded in another checkout opens read-only at the recorded
// path, not as a same-named file in this session (da25f9a63, c395f81ac).
{
  opened.length = 0;
  assert.equal(requestOpenConversationFile(localSession, 'src/a.ts', { path: '/work/other-checkout/src/a.ts' }), null);
  assert.deepEqual(opened, [{ path: '/work/other-checkout/src/a.ts', projectRoot: '/work/session', readOnly: true, line: undefined }]);
}

// A remote session's link opens on that session's machine; without a saved
// machine it says so and opens nothing on the Mac (725896a73).
{
  opened.length = 0;
  const remote = { cwd: '/home/me/repo', executionEnvironment: 'remote', remoteProfileId: 'workbox-test' };
  assert.equal(requestOpenConversationFile(remote, 'src/main.rs#L7'), null);
  assert.deepEqual(opened, [{
    path: 'assembly-remote://workbox-test/home/me/repo/src/main.rs',
    projectRoot: 'assembly-remote://workbox-test/home/me/repo',
    readOnly: false,
    line: 7
  }]);
  opened.length = 0;
  assert.equal(requestOpenConversationFile({ ...remote, remoteProfileId: null }, 'src/main.rs'), 'Connect to this conversation’s saved machine first.');
  assert.deepEqual(opened, []);
}
offConversation();

console.log('openFileBus: all tests passed');
