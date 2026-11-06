import assert from 'node:assert/strict';
import { test } from 'node:test';
import { flatten, nodeWithId, type Node } from './agent-desktop-ids.ts';

const tree: Node = {
  role: 'window',
  children: [
    { role: 'button', name: 'New session', ref_id: '@s1:e4', native_id: { value: 'sessions.new-session' } },
    { role: 'tablist', children: [{ role: 'tab', name: 'Files', ref_id: '@s1:e7', native_id: { value: 'side-tabs.files' } }] },
    { role: 'button', name: '' },
  ],
};

test('a control is found by its id wherever it sits in the tree, not by its ref', () => {
  assert.equal(nodeWithId(tree, 'side-tabs.files')?.name, 'Files');
  assert.equal(nodeWithId(tree, 'sessions.new-session')?.role, 'button');
  assert.equal(nodeWithId(tree, 'sessions.missing'), undefined);
});

test('flatten lists every node once, the unnamed ones too', () => {
  assert.equal(flatten(tree).length, 5);
});
