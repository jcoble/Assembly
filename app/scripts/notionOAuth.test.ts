import assert from 'node:assert/strict';

import { receiveNotionOAuthCode, waitForClaimCode } from '../src/lib/shell/notionOAuth.ts';
import { openIncomingLink } from '../src/lib/shell/workbenchNavigation.ts';

// The Notion callback page hands the claim code back through an app link. Only
// the sign-in this app started with that state takes it.
const signIn = new AbortController();
const code = waitForClaimCode('state-this-app-started', signIn.signal, async () => {});
let settled = false;
void code.then(() => (settled = true));

await openIncomingLink('assembly://notion-oauth?state=someone-elses-state&code=wrong-code');
await Promise.resolve();
assert.equal(settled, false, 'a link for a sign-in this app did not start is ignored');

await openIncomingLink('assembly://notion-oauth?state=state-this-app-started&code=the-claim-code');
assert.equal(await code, 'the-claim-code');

// Stopping the sign-in stops the wait, and a late link then does nothing.
const stopped = new AbortController();
const abandoned = waitForClaimCode('stopped-state', stopped.signal, async () => {});
stopped.abort(new Error('closed'));
await assert.rejects(abandoned, /closed/);
await openIncomingLink('assembly://notion-oauth?state=stopped-state&code=late');

// A callback can arrive before opening the browser resolves.
const instant = new AbortController();
let opened = false;
const immediate = waitForClaimCode('instant-state', instant.signal, async () => {
  opened = true;
  receiveNotionOAuthCode('instant-state', 'instant-code');
});
try {
  assert.equal(opened, true, 'the browser opens only after its waiter is registered');
  assert.equal(await immediate, 'instant-code');
} finally {
  instant.abort();
  await immediate.catch(() => {});
}

// A failed browser launch releases the waiter so a later sign-in can start.
await assert.rejects(waitForClaimCode('failed-open', new AbortController().signal, async () => {
  throw new Error('browser did not open');
}), /browser did not open/);
receiveNotionOAuthCode('failed-open', 'late');
console.log('notion oauth link: ok');
