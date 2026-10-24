import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const { createExtensionApiProbeScmLease } = await import(
  '../src/lib/shell/extensions/extensionApiProbeScmAdapter.ts'
);

function file(relativePath, indexStatus, worktreeStatus, badge) {
  return {
    relativePath,
    indexStatus,
    worktreeStatus,
    status: worktreeStatus || indexStatus,
    badge
  };
}

function status(files = [file('src/main.rs', '', 'modified', 'M')]) {
  return {
    branch: 'main',
    ahead: 0,
    behind: 0,
    hasUpstream: true,
    files
  };
}

function deferred() {
  let resolve;
  const promise = new Promise((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

function createFakeProvider(initialRoot, initialStatus = status()) {
  let root = initialRoot;
  let scmGeneration = 1;
  let currentStatus = initialStatus;
  const owners = new Map();
  const calls = [];

  return {
    calls,
    ownerCount: () => owners.size,
    scmGeneration: () => scmGeneration,
    setRoot(nextRoot, nextStatus = currentStatus) {
      root = nextRoot;
      currentStatus = nextStatus;
      scmGeneration += 1;
      owners.clear();
    },
    setStatus(nextStatus) {
      currentStatus = nextStatus;
    },
    acquireOwner(context) {
      calls.push(['acquireOwner', context.ownedId, context.generation, context.root]);
      if (context.root !== root || !context.ownedId || !currentStatus) return null;
      const owner = {
        ownedId: context.ownedId,
        generation: context.generation,
        scmGeneration,
        root: context.root
      };
      owners.clear();
      owners.set(owner.ownedId, owner);
      return owner;
    },
    isOwnerCurrent(owner) {
      const active = owners.get(owner.ownedId);
      return Boolean(
        active &&
          active.root === owner.root &&
          active.generation === owner.generation &&
          active.scmGeneration === owner.scmGeneration &&
          owner.root === root &&
          owner.scmGeneration === scmGeneration
      );
    },
    releaseOwner(owner) {
      calls.push(['releaseOwner', owner.ownedId]);
      const active = owners.get(owner.ownedId);
      if (
        active?.generation === owner.generation &&
        active.scmGeneration === owner.scmGeneration &&
        active.root === owner.root
      ) {
        owners.delete(owner.ownedId);
      }
    },
    groups(owner) {
      calls.push(['groups', owner.ownedId]);
      if (!this.isOwnerCurrent(owner)) return [];
      const files = currentStatus.files;
      return [
        {
          id: 'staged',
          label: 'Staged Changes',
          files: files.filter((entry) => entry.indexStatus.trim() !== '' && entry.indexStatus !== '?')
        },
        {
          id: 'workingTree',
          label: 'Changes',
          files: files.filter((entry) => entry.worktreeStatus.trim() !== '' && entry.worktreeStatus !== '?')
        },
        {
          id: 'untracked',
          label: 'Untracked Files',
          files: files.filter((entry) => entry.indexStatus === '?' || entry.worktreeStatus === '?')
        }
      ].filter((group) => group.files.length > 0);
    }
  };
}

function createFakeService(root = '/repo') {
  const calls = [];
  return {
    calls,
    state: { root, status: status() },
    async selectFile(selected) {
      calls.push(['selectFile', selected.relativePath]);
    },
    async refreshStatus() {
      calls.push(['refreshStatus']);
    }
  };
}

const context = (ownedId = 'session-a', generation = 1, root = '/repo') => ({
  ownedId,
  generation,
  root
});

{
  const provider = createFakeProvider('/repo');
  const service = createFakeService('/repo/');
  const lease = createExtensionApiProbeScmLease(service, provider, context('session-a', 7, '/repo/'));

  assert.ok(lease, 'lease is issued for the active root');
  assert.equal(lease.root, '/repo');
  assert.equal(lease.ownedId, 'session-a');
  assert.equal(lease.generation, 7);
  assert.deepEqual(lease.readStatus()?.files.map((entry) => entry.relativePath), ['src/main.rs']);
  assert.deepEqual(
    lease.groups().map((group) => [group.id, group.files.map((entry) => entry.relativePath)]),
    [['workingTree', ['src/main.rs']]]
  );
  assert.equal(provider.ownerCount(), 1, 'the probe owner is tracked separately from SCM authority');
}

{
  const provider = createFakeProvider('/repo', status([file('src/main.rs', '', 'modified', 'M')]));
  const service = createFakeService('/repo');
  const lease = createExtensionApiProbeScmLease(service, provider, context());

  await lease.select('src/main.rs');
  assert.deepEqual(service.calls, [['selectFile', 'src/main.rs']]);
  await assert.rejects(() => lease.select('src/missing.rs'), /not present/);
}

{
  const provider = createFakeProvider('/repo');
  const service = createFakeService('/repo');
  const lease = createExtensionApiProbeScmLease(service, provider, context());

  provider.setRoot('/other');
  assert.throws(() => lease.readStatus(), /no longer current/);
  await assert.rejects(() => lease.refresh(), /no longer current/);
  assert.deepEqual(service.calls, [], 'stale owners fail before GitService calls');
}

{
  const provider = createFakeProvider('/repo');
  const service = createFakeService('/repo');
  const gate = deferred();
  service.refreshStatus = async () => {
    service.calls.push(['refreshStatus']);
    await gate.promise;
  };
  const lease = createExtensionApiProbeScmLease(service, provider, context('session-a', 1));
  const refreshing = lease.refresh();
  const replacement = createExtensionApiProbeScmLease(service, provider, context('session-b', 1));
  assert.ok(replacement, 'same-root owner switch creates a new current owner');
  gate.resolve();
  await assert.rejects(() => refreshing, /no longer current/);
  assert.deepEqual(service.calls, [['refreshStatus']], 'refresh delegates only refreshStatus');
  assert.deepEqual(replacement.readStatus()?.files.map((entry) => entry.relativePath), ['src/main.rs']);
}

{
  const provider = createFakeProvider('/repo');
  const service = createFakeService('/repo');
  const lease = createExtensionApiProbeScmLease(service, provider, context());

  lease.dispose();
  assert.equal(provider.ownerCount(), 0);
  assert.throws(() => lease.readStatus(), /no longer current/);
  lease.dispose();
  assert.equal(provider.ownerCount(), 0, 'dispose is idempotent');
}

{
  const provider = createFakeProvider('/repo');
  const service = createFakeService('/repo');
  const first = createExtensionApiProbeScmLease(service, provider, context('session-a', 1));
  const second = createExtensionApiProbeScmLease(service, provider, context('session-b', 1));

  assert.equal(provider.ownerCount(), 1);
  assert.throws(() => first.readStatus(), /no longer current/);
  await assert.rejects(() => first.select('src/main.rs'), /no longer current/);
  await assert.rejects(() => first.refresh(), /no longer current/);
  assert.deepEqual(service.calls, [], 'stale owner operations cannot call GitService');
  first.dispose();
  assert.equal(provider.ownerCount(), 1, 'disposing a stale owner cannot release the current owner');
  assert.deepEqual(second.readStatus()?.files.map((entry) => entry.relativePath), ['src/main.rs']);
  second.dispose();
  assert.equal(provider.ownerCount(), 0);
}

{
  const provider = createFakeProvider('/repo');
  const service = createFakeService('/repo');
  const first = createExtensionApiProbeScmLease(service, provider, context('session-a', 1));
  const second = createExtensionApiProbeScmLease(service, provider, context('session-a', 2));

  assert.equal(provider.ownerCount(), 1);
  assert.throws(() => first.readStatus(), /no longer current/);
  await assert.rejects(() => first.select('src/main.rs'), /no longer current/);
  await assert.rejects(() => first.refresh(), /no longer current/);
  assert.deepEqual(service.calls, [], 'old generation operations cannot call GitService');
  first.dispose();
  assert.equal(provider.ownerCount(), 1, 'disposing an old generation cannot release the new owner');
  assert.deepEqual(second.readStatus()?.files.map((entry) => entry.relativePath), ['src/main.rs']);
}

const adapterSource = readFileSync(
  new URL('../src/lib/shell/extensions/extensionApiProbeScmAdapter.ts', import.meta.url),
  'utf8'
);
const providerSource = readFileSync(
  new URL('../src/lib/shell/extensions/rustGitScmProvider.ts', import.meta.url),
  'utf8'
);

for (const source of [adapterSource, providerSource]) {
  assert.doesNotMatch(source, /child_process|Command::new|invoke\(|stagePaths|unstagePaths|commit\(|runRemoteAction|fetch|pull|push/);
}
assert.equal(
  (providerSource.match(/vscode\.scm\.createSourceControl/g) ?? []).length,
  0,
  'the bounded Rust Git provider must not call the unsupported VS Code SCM service'
);

console.log('extensionApiProbeScmAdapter.test.mjs: all checks passed');
