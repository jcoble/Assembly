import assert from 'node:assert/strict';

globalThis.$state = (value) => value;

const {
  BROWSER_MAX_ANNOTATIONS_PER_TAB,
  BROWSER_SELECTOR_MAX_LENGTH,
  BROWSER_SNIPPET_MAX_LENGTH,
  boundClassCount,
  boundSelector,
  boundTextSnippet,
  createImmutableBrowserFeedbackAttachment,
  selectionFromElementInput
} = await import('../src/lib/shell/browser/browserAnnotations.ts');
const { createBrowserWorkspace } = await import('../src/lib/shell/browser/browserTypes.ts');
const { createBrowserModel } = await import('../src/lib/shell/browser/browserModel.ts');

assert.equal(boundSelector('x'.repeat(BROWSER_SELECTOR_MAX_LENGTH + 20)).length, BROWSER_SELECTOR_MAX_LENGTH);
assert.equal(boundTextSnippet('x'.repeat(BROWSER_SNIPPET_MAX_LENGTH + 20)).length, BROWSER_SNIPPET_MAX_LENGTH);
assert.equal(boundClassCount(999), 32);
assert.equal(boundClassCount(-1), 0);

const attachment = createImmutableBrowserFeedbackAttachment({
  id: 'grab-1',
  kind: 'grab',
  workspaceId: 'workspace-1',
  tabId: 'tab-1',
  generation: 4,
  url: 'https://example.com/',
  title: 'Example',
  selector: 'main > button',
  accessibleName: 'Continue',
  textSnippet: 'Continue',
  createdAt: '2026-08-05T00:00:00.000Z'
});
assert.equal(Object.isFrozen(attachment), true, 'feedback is immutable');
assert.equal(attachment.note, null, 'Grab never invents a note');
assert.equal(attachment.intent, 'context', 'Grab is context intent');
assert.throws(
  () => createImmutableBrowserFeedbackAttachment({
    ...attachment,
    kind: 'annotation',
    note: null,
    intent: 'change'
  }),
  /note is required/
);

const workspace = createBrowserWorkspace({ workspaceId: 'workspace-1' });
const model = createBrowserModel({ workspace, now: () => '2026-08-05T00:00:00.000Z' });
const tab = model.createBrowserTab({ url: 'https://example.com/' });
const selection = selectionFromElementInput(tab, {
  selector: 'x'.repeat(3000),
  textSnippet: 'y'.repeat(700),
  classCount: 400,
  classes: Array.from({ length: 40 }, (_, index) => `class-${index}`)
});
assert.equal(selection.selector.length, BROWSER_SELECTOR_MAX_LENGTH);
assert.equal(selection.textSnippet.length, BROWSER_SNIPPET_MAX_LENGTH);
assert.equal(selection.classCount, 32);
assert.equal(selection.classes.length, 32);

for (let index = 0; index < BROWSER_MAX_ANNOTATIONS_PER_TAB; index += 1) {
  model.beginBrowserElementPicker('annotation');
  model.acceptBrowserElementSelection({ selector: `.item-${index}` });
  model.queueBrowserAnnotation({ note: `Change ${index}`, intent: 'change' });
}
assert.equal(tab.annotations.length, BROWSER_MAX_ANNOTATIONS_PER_TAB);
model.beginBrowserElementPicker('annotation');
model.acceptBrowserElementSelection({ selector: '.too-many' });
assert.throws(() => model.queueBrowserAnnotation({ note: 'one more', intent: 'change' }), /at most 100/);

console.log('browserAnnotations: all tests passed');
