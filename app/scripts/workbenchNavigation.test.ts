import assert from 'node:assert/strict';

import { onOpenFile, type OpenFileRequest } from '../src/lib/shell/openFileBus.ts';
import {
  CENTER_TAB_IDS,
  RIGHT_TAB_IDS,
  clearWorkbenchNavigation,
  focusComposerWith,
  openDiffForFile,
  openFileInEditor,
  openUrlInBrowser,
  parseGithubPullRequestUrl,
  parseAssemblyLink,
  parseNotionPageId,
  registerBrowserUrlNavigation,
  registerWorkbenchNavigation,
  sendToSession,
  showCenterTab,
  showRightTab,
  startWorkbenchSession
} from '../src/lib/shell/workbenchNavigation.ts';

/** Everything each caller did, in the order it happened. */
let trace: string[] = [];
const openedFiles: OpenFileRequest[] = [];
onOpenFile((request) => {
  trace.push(`requestOpenFile:${request.path}`);
  openedFiles.push(request);
});

// ── Nothing registered: every caller is a quiet no-op ────────────────────────

clearWorkbenchNavigation();
showCenterTab('editor');
showRightTab('files');
await openDiffForFile({ projectRoot: '/repo', relativePath: 'src/a.ts' });
await openUrlInBrowser({ url: 'https://example.test' });
await focusComposerWith({ ownedId: 'one' });
assert.equal(
  await startWorkbenchSession({ prompt: 'hello', cwd: '/repo', projectPath: '/repo', title: 'Look' }),
  null,
  'starting a session with nothing registered answers null rather than throwing'
);
assert.deepEqual(
  trace.filter((entry) => !entry.startsWith('requestOpenFile')),
  [],
  'no handler is registered, so nothing was called'
);

// `openFileInEditor` still puts the request on the file bus — that bus is not a
// workbench handler and works on its own.
trace = [];
openFileInEditor({ path: '/repo/src/a.ts' });
assert.deepEqual(trace, ['requestOpenFile:/repo/src/a.ts'], 'the file bus is independent of the handlers');

// ── Registration merges, one field at a time ────────────────────────────────

clearWorkbenchNavigation();
trace = [];
registerWorkbenchNavigation({ showCenterTab: (id) => trace.push(`center:${id}`) });
showCenterTab('diff');
showRightTab('run');
assert.deepEqual(trace, ['center:diff'], 'registering one handler leaves the others unregistered');

registerWorkbenchNavigation({ showRightTab: (id) => trace.push(`right:${id}`) });
showCenterTab('session');
showRightTab('run');
assert.deepEqual(
  trace,
  ['center:diff', 'center:session', 'right:run'],
  'a second registration adds to the first rather than replacing it'
);

await assert.rejects(
  sendToSession({ ownedId: 'one', text: 'look' }),
  /session sender is not ready/,
  'an explicit send must not silently discard a message when its handler is missing'
);

// ── The composed callers, and the order they do things in ───────────────────

clearWorkbenchNavigation();
trace = [];
openedFiles.length = 0;
registerWorkbenchNavigation({
  showCenterTab: (id) => trace.push(`center:${id}`),
  showRightTab: (id) => trace.push(`right:${id}`),
  openDiff: (request) => {
    trace.push(`diff:${request.projectRoot}:${request.relativePath}`);
  },
  openUrl: (request) => {
    trace.push(`url:${request.url}`);
  },
  focusComposer: (handoff) => {
    trace.push(`composer:${handoff.ownedId}`);
  },
  sendToSession: async (request) => {
    trace.push(`send:${request.ownedId}:${request.text}`);
  },
  startSession: async (request) => {
    trace.push(`session:${request.cwd}`);
    return 'owned-1';
  }
});

openFileInEditor({ path: '/repo/src/a.ts', projectRoot: '/repo' });
assert.deepEqual(
  trace,
  ['requestOpenFile:/repo/src/a.ts', 'center:editor'],
  'a file goes on the bus first, then the editor is brought forward'
);
assert.equal(openedFiles[0]?.projectRoot, '/repo', 'the whole request reaches the bus');

trace = [];
await openDiffForFile({ projectRoot: '/repo', relativePath: 'src/a.ts' });
assert.deepEqual(
  trace,
  ['diff:/repo:src/a.ts', 'center:diff'],
  'the file is selected before the diff tab is brought forward'
);

trace = [];
await openUrlInBrowser({ url: 'https://example.test' });
assert.deepEqual(
  trace,
  ['center:browser', 'url:https://example.test'],
  'the browser tab is on screen before it is asked to navigate'
);

trace = [];
await focusComposerWith({ ownedId: 'one', appendText: 'look at this' });
assert.deepEqual(
  trace,
  ['composer:one', 'center:session'],
  'the composer is handed its content, then the session comes to the front'
);

trace = [];
await sendToSession({ ownedId: 'one', text: 'send this' });
assert.deepEqual(
  trace,
  ['send:one:send this', 'center:session'],
  'the message is accepted before the session comes to the front'
);

trace = [];
assert.equal(
  await startWorkbenchSession({ prompt: 'hi', cwd: '/repo', projectPath: '/repo', title: 'Look' }),
  'owned-1',
  'the new session’s id is handed back to the caller'
);
assert.deepEqual(trace, ['session:/repo']);

// ── Clearing unregisters everything ─────────────────────────────────────────

clearWorkbenchNavigation();
trace = [];
showCenterTab('editor');
showRightTab('files');
await openDiffForFile({ projectRoot: '/repo', relativePath: 'src/a.ts' });
await openUrlInBrowser({ url: 'https://example.test' });
await focusComposerWith({ ownedId: 'one' });
assert.equal(
  await startWorkbenchSession({ prompt: 'hi', cwd: '/repo', projectPath: '/repo', title: 'Look' }),
  null
);
assert.deepEqual(trace, [], 'after clearing, every caller is a no-op again');

// The browser tab places its view only after showCenterTab selects it, so its first
// requested URL must wait for that late registration rather than disappear.
clearWorkbenchNavigation();
trace = [];
registerWorkbenchNavigation({ showCenterTab: (id) => trace.push(`center:${id}`) });
await openUrlInBrowser({ url: 'https://notion.test/setup' });
assert.deepEqual(trace, ['center:browser']);
const releaseBrowser = registerBrowserUrlNavigation((request) => {
  trace.push(`url:${request.url}`);
});
assert.deepEqual(trace, ['center:browser', 'url:https://notion.test/setup']);
releaseBrowser();

// ── A Notion link is read as its page id, dashed the way the Notion API writes it ──

const notionId = '3f4394b0-689d-81b1-9819-c696dad2fd48';
assert.equal(parseNotionPageId('https://app.notion.com/p/3f4394b0689d81b19819c696dad2fd48'), notionId);
assert.equal(parseNotionPageId('https://www.notion.so/team/TSK-1400-Tasks-tab-3f4394b0689d81b19819c696dad2fd48?pvs=4'), notionId);
assert.equal(parseNotionPageId('https://notion.so/3f4394b0-689d-81b1-9819-c696dad2fd48'), notionId);
assert.equal(parseNotionPageId('https://acme.notion.site/Fix-deadbeef-3f4394b0689d81b19819c696dad2fd48'), notionId);
for (const url of [
  'https://notion.so.evil.test/3f4394b0689d81b19819c696dad2fd48',
  'https://www.notion.so/team/Just-a-title',
  'https://www.notion.so/x3f4394b0689d81b19819c696dad2fd48',
  'notion://www.notion.so/3f4394b0689d81b19819c696dad2fd48'
]) {
  assert.equal(parseNotionPageId(url), null, url);
}

// ── An assembly:// link names a session, task, web page or file; anything else is null ──

assert.deepEqual(parseAssemblyLink('assembly://session/owned-123'), { kind: 'session', ownedId: 'owned-123' });
assert.deepEqual(parseAssemblyLink('assembly://task/3f4394b0689d81b19819c696dad2fd48'), { kind: 'task', pageId: notionId });
assert.deepEqual(
  parseAssemblyLink(`assembly://open?url=${encodeURIComponent('https://github.com/o/r/pull/3')}`),
  { kind: 'open', url: 'https://github.com/o/r/pull/3' }
);
assert.deepEqual(parseAssemblyLink('assembly://file?path=%2Fwork%2Fa%20b.cs'), { kind: 'file', path: '/work/a b.cs' });
assert.deepEqual(
  parseAssemblyLink('assembly://file?path=%2Fwork%2Fa.cs&profile=box'),
  { kind: 'file', path: 'assembly-remote://box/work/a.cs' }
);
for (const url of [
  'assembly://session/',
  'assembly://session/a/b',
  'assembly://task/not-a-page',
  'assembly://open?url=javascript%3Aalert(1)',
  'assembly://file?path=relative.cs',
  'assembly://settings',
  'https://session/owned-123'
]) {
  assert.equal(parseAssemblyLink(url), null, url);
}

// ── A GitHub pull request link opens the Pull requests tab, not the browser ──

assert.deepEqual(parseGithubPullRequestUrl('https://github.com/jcoble/mac-command-bar/pull/111'), { repository: 'jcoble/mac-command-bar', number: 111 });
assert.deepEqual(parseGithubPullRequestUrl('https://github.com/o/r.js/pull/7/files?w=1#diff-1'), { repository: 'o/r.js', number: 7 });
assert.deepEqual(parseGithubPullRequestUrl('https://www.github.com/o/r/pull/3/'), { repository: 'o/r', number: 3 });
for (const url of [
  'https://github.com/o/r/issues/3',
  'https://github.com/o/r/pull/0',
  'https://github.com/o/r/pull/12abc',
  'https://github.com/o/r/pulls',
  'https://github.com.evil.test/o/r/pull/3',
  'https://gist.github.com/o/r/pull/3',
  'file:///github.com/o/r/pull/3',
  'not a url'
]) {
  assert.equal(parseGithubPullRequestUrl(url), null, url);
}

clearWorkbenchNavigation();
trace = [];
registerWorkbenchNavigation({
  showCenterTab: (id) => trace.push(`center:${id}`),
  openUrl: (request) => {
    trace.push(`url:${request.url}`);
  },
  openPullRequest: (link) => {
    trace.push(`pr:${link.repository}#${link.number}`);
  }
});
await openUrlInBrowser({ url: 'https://github.com/o/r/pull/42' });
assert.deepEqual(trace, ['pr:o/r#42', 'center:pull-requests'], 'the pull request is chosen, then its tab comes forward; the browser is untouched');
trace = [];
await openUrlInBrowser({ url: 'https://github.com/o/r/issues/42' });
assert.deepEqual(trace, ['center:browser', 'url:https://github.com/o/r/issues/42'], 'other GitHub links still open in the browser');
clearWorkbenchNavigation();

assert.equal(RIGHT_TAB_IDS.length, 8);
assert.equal(CENTER_TAB_IDS.length, 6);

console.log('workbenchNavigation: no-op safety, partial registration, call order, and clearing passed');
