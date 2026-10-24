import assert from 'node:assert/strict';
import {
  ConversationDraftPersistence,
  type ConversationDraftBackend
} from './conversationDraftPersistence.ts';

const stored = new Map<string, string>();
const writes: string[] = [];
const loaded: string[] = [];
const backend: ConversationDraftBackend = {
  async set(ownedId, text) {
    writes.push(`${ownedId}:${text}`);
    stored.set(ownedId, text);
  },
  async get(ownedId) {
    return stored.get(ownedId) ?? null;
  },
  async clear(ownedId) {
    writes.push(`${ownedId}:clear`);
    stored.delete(ownedId);
  }
};
const drafts = new ConversationDraftPersistence(
  backend,
  (ownedId, text) => loaded.push(`${ownedId}:${text}`)
);

drafts.schedule('owned-a', 'first');
drafts.schedule('owned-a', 'second');
await drafts.flush('owned-a');
assert.deepEqual(writes, ['owned-a:first', 'owned-a:second'], 'typing writes drafts in order');

drafts.schedule('owned-a', 'blurred');
await drafts.flush('owned-a');
assert.equal(stored.get('owned-a'), 'blurred', 'blur or session switch waits for queued writes');

await drafts.load('owned-a');
assert.deepEqual(loaded, ['owned-a:blurred'], 'activation restores the stored draft');

drafts.schedule('owned-a', 'not sent');
await drafts.clear('owned-a');
assert.equal(stored.has('owned-a'), false, 'send cancels pending writes and clears storage');
assert.equal(writes.at(-1), 'owned-a:clear');

console.log('conversation draft persistence tests passed');
