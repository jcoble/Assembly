import assert from 'node:assert/strict';
import { createLiveConversationTerminals } from '../src/lib/liveConversationTerminals.ts';

/**
 * Build a spy TerminalView plus a fake `createTerminal` factory. No DOM/xterm:
 * every method records its calls so tests can assert on the bookkeeping.
 */
function createHarness(extraDeps = {}) {
  const created = []; // one entry per createTerminal() call
  let createCalls = 0;

  function makeView(host) {
    const calls = {
      write: [],
      fit: 0,
      focus: 0,
      setVisible: [],
      dispose: 0,
      // Every setVisible/dispose in the order it happened. A view that is
      // disposed while its host still says `display: block` covers the live
      // terminal, so the ORDER of those two is the thing worth asserting.
      order: []
    };
    const view = {
      host,
      calls,
      write(data) {
        calls.write.push(data);
      },
      fit() {
        calls.fit += 1;
      },
      focus() {
        calls.focus += 1;
      },
      setVisible(visible) {
        calls.setVisible.push(visible);
        calls.order.push(`visible:${visible}`);
      },
      dispose() {
        calls.dispose += 1;
        calls.order.push('dispose');
      },
      // Convenience helpers for assertions.
      visibleNow() {
        return calls.setVisible.length ? calls.setVisible[calls.setVisible.length - 1] : null;
      }
    };
    return view;
  }

  const deps = {
    createTerminal(host) {
      createCalls += 1;
      const view = makeView(host);
      created.push(view);
      return view;
    },
    ...extraDeps
  };

  const manager = createLiveConversationTerminals(deps);
  return {
    manager,
    created,
    get createCalls() {
      return createCalls;
    }
  };
}

const fakeHost = (id) => ({ __host: id });

// --- ensureView is idempotent per key: twice -> exactly one createTerminal ---
{
  const harness = createHarness();
  const { manager, created } = harness;
  const v1 = manager.ensureView('a', { host: fakeHost('a') });
  const v2 = manager.ensureView('a', { host: fakeHost('a-again') });
  assert.equal(harness.createCalls, 1, 'ensureView twice for same key should create exactly one terminal');
  assert.equal(v1, v2, 'ensureView should return the same view for the same key');
  assert.equal(created.length, 1, 'only one TerminalView spy should be created');
  assert.deepEqual(manager.liveKeys(), ['a']);
  assert.equal(manager.hasView('a'), true);
  assert.equal(manager.hasView('zzz'), false);
}

// --- viewFor hands back an EXISTING view and never builds one ---
{
  const harness = createHarness();
  const { manager } = harness;
  assert.equal(manager.viewFor('a'), null, 'viewFor is null before the view exists');
  assert.equal(harness.createCalls, 0, 'and asking must not build one');
  const view = manager.ensureView('a', { host: fakeHost('a') });
  assert.equal(manager.viewFor('a'), view, 'viewFor returns the view ensureView created');
  assert.equal(harness.createCalls, 1, 'and still creates nothing of its own');
  manager.closeView('a');
  assert.equal(manager.viewFor('a'), null, 'a closed key has no view again');
}

// --- first view created becomes active when none is active ---
{
  const { manager } = createHarness();
  assert.equal(manager.activeKey(), null, 'no active view before any are created');
  manager.ensureView('first', { host: fakeHost('first') });
  assert.equal(manager.activeKey(), 'first', 'first created view becomes active');
  manager.ensureView('second', { host: fakeHost('second') });
  assert.equal(manager.activeKey(), 'first', 'creating a second view must NOT steal active');
}

// --- showView('b') makes only b visible; all others get setVisible(false) ---
{
  const { manager, created } = createHarness();
  manager.ensureView('a', { host: fakeHost('a') }); // becomes active+visible
  manager.ensureView('b', { host: fakeHost('b') });
  manager.ensureView('c', { host: fakeHost('c') });
  const [a, b, c] = created;

  manager.showView('b');

  assert.equal(manager.activeKey(), 'b', 'activeKey should follow showView');
  assert.equal(b.visibleNow(), true, 'b should be visible');
  assert.equal(a.visibleNow(), false, 'a should be hidden after showing b');
  assert.equal(c.visibleNow(), false, 'c should be hidden after showing b');
  assert.equal(b.calls.fit >= 1, true, 'shown view should be fit');
  assert.equal(b.calls.focus >= 1, true, 'shown view should be focused');
  // Hidden views are not re-fit/focused by showView('b').
  assert.equal(c.calls.fit, 0, 'never-shown view c should not be fit');
  assert.equal(c.calls.focus, 0, 'never-shown view c should not be focused');
}

// --- showView only resizes the shown session; no PTY churn on the others ---
{
  const resizeCalls = [];
  const { manager } = createHarness({
    resizeSession: (sessionId) => resizeCalls.push(sessionId)
  });
  manager.ensureView('a', { host: fakeHost('a'), sessionId: 'sess-a' });
  manager.ensureView('b', { host: fakeHost('b'), sessionId: 'sess-b' });
  resizeCalls.length = 0; // ignore resizes from initial activation
  manager.showView('b');
  assert.deepEqual(resizeCalls, ['sess-b'], 'only the shown session should be resized');
}

// --- feed to a NON-visible key still writes to that view's terminal ---
{
  const { manager, created } = createHarness();
  manager.ensureView('a', { host: fakeHost('a') }); // active/visible
  manager.ensureView('b', { host: fakeHost('b') }); // hidden
  const [a, b] = created;
  assert.notEqual(b.visibleNow(), true, 'b should not be visible (it was never shown)');

  manager.feed('b', 'output-while-hidden');

  assert.deepEqual(b.calls.write, ['output-while-hidden'], 'hidden view should still receive writes');
  assert.deepEqual(a.calls.write, [], 'feed to b must not write to a');

  // feed to unknown key is a no-op (no throw).
  manager.feed('does-not-exist', 'nope');
}

// --- bindSession + keyForSession round-trip ---
{
  const { manager } = createHarness();
  manager.ensureView('a', { host: fakeHost('a') });
  assert.equal(manager.keyForSession('sess-1'), null, 'unbound session resolves to null');
  manager.bindSession('a', 'sess-1');
  assert.equal(manager.keyForSession('sess-1'), 'a', 'bindSession should make keyForSession resolve');

  // Rebinding the same key to a new session releases the old mapping.
  manager.bindSession('a', 'sess-2');
  assert.equal(manager.keyForSession('sess-2'), 'a', 'new session should resolve to the key');
  assert.equal(manager.keyForSession('sess-1'), null, 'old session mapping should be released');
}

// --- ensureView adopts a sessionId passed at creation time ---
{
  const { manager } = createHarness();
  manager.ensureView('a', { host: fakeHost('a'), sessionId: 'sess-created' });
  assert.equal(manager.keyForSession('sess-created'), 'a', 'sessionId from ensureView should be mapped');
}

// --- feedSession routes to the right view by sessionId ---
{
  const { manager, created } = createHarness();
  manager.ensureView('a', { host: fakeHost('a'), sessionId: 'sess-a' });
  manager.ensureView('b', { host: fakeHost('b'), sessionId: 'sess-b' });
  const [a, b] = created;

  manager.feedSession('sess-b', 'data-for-b');
  assert.deepEqual(b.calls.write, ['data-for-b'], 'feedSession should write to the matching view');
  assert.deepEqual(a.calls.write, [], 'feedSession should not write to the wrong view');

  // Unknown session is a no-op.
  manager.feedSession('sess-unknown', 'nope');
  assert.deepEqual(a.calls.write, []);
}

// --- ensureView hydrates from saved scrollback when a sessionId is given ---
{
  const { manager, created } = createHarness({
    readScrollback: (sessionId) => (sessionId === 'sess-a' ? 'PRIOR-SCROLLBACK' : null)
  });
  manager.ensureView('a', { host: fakeHost('a'), sessionId: 'sess-a' });
  manager.ensureView('b', { host: fakeHost('b'), sessionId: 'sess-b' });
  const [a, b] = created;
  assert.deepEqual(a.calls.write, ['PRIOR-SCROLLBACK'], 'view should hydrate from scrollback on create');
  assert.deepEqual(b.calls.write, [], 'no scrollback means no initial write');
}

// --- markTerminated keeps the view + record (no auto-remove) ---
{
  const { manager, created } = createHarness();
  manager.ensureView('a', { host: fakeHost('a') });
  const [a] = created;
  manager.markTerminated('a');
  assert.deepEqual(manager.liveKeys(), ['a'], 'terminated view should remain in liveKeys');
  assert.equal(a.calls.dispose, 0, 'markTerminated must not dispose the view');
  // Still feedable after termination (e.g. trailing output / exit notice).
  manager.feed('a', 'exit code 0');
  assert.deepEqual(a.calls.write, ['exit code 0']);
}

// --- closeView removes only that key and disposes only its view ---
{
  const { manager, created } = createHarness();
  manager.ensureView('a', { host: fakeHost('a'), sessionId: 'sess-a' });
  manager.ensureView('b', { host: fakeHost('b'), sessionId: 'sess-b' });
  manager.ensureView('c', { host: fakeHost('c') });
  const [a, b, c] = created;

  manager.closeView('b');

  assert.deepEqual(manager.liveKeys(), ['a', 'c'], 'closeView should remove only that key');
  assert.equal(b.calls.dispose, 1, 'closed view should be disposed exactly once');
  assert.equal(a.calls.dispose, 0, 'other views must not be disposed');
  assert.equal(c.calls.dispose, 0, 'other views must not be disposed');
  assert.equal(manager.hasView('b'), false, 'closed key should no longer exist');
  assert.equal(manager.keyForSession('sess-b'), null, 'closed view sessionId mapping should be cleared');
  assert.equal(manager.keyForSession('sess-a'), 'a', 'other sessionId mappings should survive');
}

// --- closeView on the ACTIVE key clears or hands off active ---
{
  const { manager } = createHarness();
  manager.ensureView('a', { host: fakeHost('a') }); // active
  manager.ensureView('b', { host: fakeHost('b') });
  assert.equal(manager.activeKey(), 'a');

  manager.closeView('a');
  // Another view survives -> active hands off to it instead of going null.
  assert.equal(manager.hasView('a'), false);
  assert.equal(manager.activeKey(), 'b', 'closing the active view should hand off to a survivor');

  manager.closeView('b');
  assert.equal(manager.activeKey(), null, 'closing the last view should clear activeKey');
  assert.deepEqual(manager.liveKeys(), []);

  // closeView on unknown key is a no-op.
  manager.closeView('ghost');
}

// --- disposeAll empties liveKeys and disposes every view ---
{
  const { manager, created } = createHarness();
  manager.ensureView('a', { host: fakeHost('a'), sessionId: 'sess-a' });
  manager.ensureView('b', { host: fakeHost('b'), sessionId: 'sess-b' });
  manager.ensureView('c', { host: fakeHost('c') });
  const views = created.slice();

  manager.disposeAll();

  assert.deepEqual(manager.liveKeys(), [], 'disposeAll should empty liveKeys');
  assert.equal(manager.activeKey(), null, 'disposeAll should clear activeKey');
  assert.equal(manager.keyForSession('sess-a'), null, 'disposeAll should clear session mappings');
  assert.equal(manager.keyForSession('sess-b'), null, 'disposeAll should clear session mappings');
  for (const view of views) {
    assert.equal(view.calls.dispose, 1, 'disposeAll should dispose every view exactly once');
  }
}

// --- constructor guards against a missing createTerminal ---
{
  assert.throws(
    () => createLiveConversationTerminals({}),
    /createTerminal/,
    'manager should require deps.createTerminal'
  );
}

// --- hiding a conversation RELEASES its buffer; the PTY binding survives ---
{
  const harness = createHarness({
    readScrollback: (sessionId) => (sessionId === 'sess-b' ? 'RING-REPLAY' : null)
  });
  const { manager, created } = harness;
  manager.ensureView('a', { host: fakeHost('a'), sessionId: 'sess-a' }); // active
  manager.ensureView('b', { host: fakeHost('b'), sessionId: 'sess-b' });
  const [, firstB] = created;

  manager.showView('a');

  assert.equal(firstB.calls.dispose, 1, 'hiding b disposes its view and frees the buffer');
  assert.deepEqual(
    firstB.calls.order.slice(-2),
    ['visible:false', 'dispose'],
    'the host is hidden BEFORE the view goes, or it covers the live terminal'
  );
  assert.equal(manager.viewFor('b'), null, 'a released record has no view');
  assert.equal(manager.hasView('b'), true, 'but the record itself survives');
  assert.equal(manager.keyForSession('sess-b'), 'b', 'and so does its PTY mapping');
  assert.deepEqual(manager.liveKeys(), ['a', 'b'], 'releasing is not closing');

  // Output for a released conversation is dropped here — the backend ring keeps
  // it — and must never throw at a view that no longer exists.
  const writesWhenReleased = firstB.calls.write.length;
  assert.doesNotThrow(() => manager.feedSession('sess-b', 'while-released'));
  assert.equal(firstB.calls.write.length, writesWhenReleased, 'nothing reaches the disposed view');

  // Showing it again rebuilds the view and replays the ring into it.
  const rebuilt = manager.ensureView('b', { host: fakeHost('b'), sessionId: 'sess-b' });
  manager.showView('b');
  assert.equal(harness.createCalls, 3, 'the re-show builds a NEW terminal');
  assert.notEqual(rebuilt, firstB, 'and it is not the disposed one');
  assert.deepEqual(rebuilt.calls.write, ['RING-REPLAY'], 'rebuilt views hydrate from scrollback');
  assert.equal(rebuilt.visibleNow(), true, 'and the rebuilt view is the visible one');
  assert.equal(manager.activeKey(), 'b');
}

// --- releaseView is callable on its own, and keeps the record's host ---
{
  const harness = createHarness();
  const { manager, created } = harness;
  const host = fakeHost('a');
  manager.ensureView('a', { host, sessionId: 'sess-a' });
  const [first] = created;
  assert.equal(manager.hostFor('a'), host, 'the manager keeps the host it built on');

  manager.releaseView('a');

  assert.equal(first.calls.dispose, 1, 'releaseView disposes the view');
  assert.equal(manager.activeKey(), null, 'releasing the ACTIVE view leaves nothing showing');
  assert.equal(manager.keyForSession('sess-a'), 'a', 'the PTY mapping is untouched');
  assert.equal(manager.hostFor('a'), host, 'and the host is still there to rebuild on');

  // Idempotent, and unknown keys are a no-op.
  manager.releaseView('a');
  assert.equal(first.calls.dispose, 1, 'releasing twice disposes once');
  assert.doesNotThrow(() => manager.releaseView('ghost'));

  // ensureView rebuilds on the remembered host when none is supplied anew.
  const rebuilt = manager.ensureView('a', { host: manager.hostFor('a') });
  assert.equal(harness.createCalls, 2);
  assert.equal(rebuilt.host, host, 'the rebuilt view is bound to the same host');
}

// --- a TERMINATED conversation keeps its view: nothing can re-wrap it ---
{
  const { manager, created } = createHarness();
  manager.ensureView('a', { host: fakeHost('a') }); // active
  manager.ensureView('b', { host: fakeHost('b') });
  const [, b] = created;
  manager.markTerminated('b');

  manager.showView('a');

  assert.equal(b.calls.dispose, 0, 'a finished session keeps its final output');
  assert.equal(b.visibleNow(), false, 'it is hidden, not released');
  assert.equal(manager.viewFor('b'), b, 'and the view is still there to show again');

  // The explicit call refuses too, for the same reason.
  manager.releaseView('b');
  assert.equal(b.calls.dispose, 0, 'releaseView leaves a terminated view alone');
}

// --- showing a released key is a no-op until its owner rebuilds it ---
{
  const { manager } = createHarness();
  manager.ensureView('a', { host: fakeHost('a') });
  manager.ensureView('b', { host: fakeHost('b') });
  manager.showView('a'); // releases b
  assert.doesNotThrow(() => manager.showView('b'), 'showing a released key must not throw');
  assert.equal(manager.activeKey(), 'b', 'it still becomes the active key');
  assert.equal(manager.viewFor('b'), null, 'the buffer arrives when its owner rebuilds it');
}

console.log('liveConversationTerminals: all tests passed');
