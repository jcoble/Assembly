import assert from 'node:assert/strict';
import {
  captureAssistanceContext,
  hashAssistanceFacts,
  isAssistanceContextCurrent,
  revalidateAssistanceTarget
} from '../src/lib/shell/assistance/assistanceContext.ts';

const input = {
  ownedId: 'owned-1',
  generation: 7,
  surface: 'browser',
  target: { surface: 'browser', id: 'tab-1', expectedValueHash: 'target-1' },
  facts: {
    repository: 'repo text',
    diff: 'diff text',
    diagnostics: 'diagnostic text',
    browser: 'browser text',
    task: 'task text',
    model: 'model text',
    token: 'should not survive',
    environment: 'should not survive',
    prompt: 'should not survive',
    cookies: 'should not survive',
    hiddenDom: 'should not survive',
    unrestrictedFile: 'should not survive'
  }
};

const context = captureAssistanceContext(input, { now: 1000 });
assert.equal(context instanceof Promise, false, 'capture is synchronous');
assert.equal(context.ownedId, 'owned-1');
assert.equal(context.generation, 7);
assert.equal(Object.keys(context.boundedFacts).some((key) => /token|environment|prompt|cookie|hidden|unrestricted/i.test(key)), false);
for (const value of Object.values(context.boundedFacts)) {
  assert.ok(value.startsWith('['), 'untrusted text is delimited');
  assert.ok(value.length <= 2048, 'untrusted text is bounded');
}

const same = captureAssistanceContext(input, { now: 1000 });
const changed = captureAssistanceContext({ ...input, facts: { ...input.facts, diff: 'changed' } }, { now: 1000 });
assert.equal(hashAssistanceFacts(context.facts), hashAssistanceFacts(same.facts));
assert.notEqual(hashAssistanceFacts(context.facts), hashAssistanceFacts(changed.facts));
assert.equal(isAssistanceContextCurrent(context, same), true);
assert.equal(isAssistanceContextCurrent(context, { ...same, generation: 8 }), false);
assert.equal(isAssistanceContextCurrent(context, changed), false);
assert.equal(revalidateAssistanceTarget(context, same.target), true);
assert.equal(revalidateAssistanceTarget(context, { ...same.target, expectedValueHash: 'changed' }), false);

const source = captureAssistanceContext.toString();
assert.doesNotMatch(source, /readFile|fetch\(|invoke\(/, 'capture has no implicit IO');

console.log('assistanceContext: all tests passed');
