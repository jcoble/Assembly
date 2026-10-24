import assert from 'node:assert/strict';
import { messageSession } from '../src/lib/shell/messages/agentMessagesModel.svelte.ts';

const remote = { ownedId: 'sender', remoteProfileId: 'box-a' };
const local = { ownedId: 'local', remoteProfileId: null };
assert.equal(messageSession('box-a:sender', [remote]), remote);
assert.equal(messageSession('box-b:sender', [remote]), null, 'another machine cannot redirect to this chat');
assert.equal(messageSession('mac:local', [local]), local);
assert.equal(messageSession('box-a:local', [local]), null, 'a remote label cannot open a local chat');
assert.equal(messageSession('sender', [remote]), remote, 'native ids still name same-machine messages');
console.log('message session identity: ok');
