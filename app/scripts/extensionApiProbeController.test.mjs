import assert from 'node:assert/strict';

const controller = await import(
  '../src/lib/shell/files/editor/extensionApiProbeController.ts'
);

const log = [];
let terminalGeneration = 0;
const fakeService = {
  async createProbe(owned, host) {
    assert.equal(host.isConnected, true);
    assert.equal(owned.ptySessionId, null, 'probe never adopts a user terminal');
    const generation = ++terminalGeneration;
    let disposed = false;
    log.push(['terminal-create', owned.ownedId, owned.cwd]);
    return {
      ownedId: owned.ownedId,
      generation,
      show() {
        if (!disposed) log.push(['terminal-show', generation]);
      },
      async write(data) {
        if (disposed) return false;
        log.push(['terminal-write', generation, data]);
        return true;
      },
      async resize(cols, rows) {
        if (disposed) return false;
        log.push(['terminal-resize', generation, cols, rows]);
        return true;
      },
      async dispose() {
        if (!disposed) log.push(['terminal-dispose', generation]);
        disposed = true;
        return { successor: null, error: null };
      }
    };
  }
};

async function acquireScmLease(context) {
  log.push(['scm-acquire', context.ownedId, context.generation, context.root]);
  let disposed = false;
  return {
    ...context,
    readStatus() {
      if (disposed) throw new Error('disposed SCM lease');
      return { branch: 'main', ahead: 0, behind: 0, hasUpstream: true, files: [] };
    },
    groups() {
      if (disposed) throw new Error('disposed SCM lease');
      return [];
    },
    async select() {
      if (disposed) throw new Error('disposed SCM lease');
    },
    async refresh() {
      if (disposed) throw new Error('disposed SCM lease');
      return this.readStatus();
    },
    dispose() {
      if (!disposed) log.push(['scm-dispose', context.ownedId, context.generation]);
      disposed = true;
    }
  };
}

await controller.configureExtensionApiProbeRuntime({
  terminalService: fakeService,
  terminalHost: { isConnected: true },
  acquireScmLease
});

const first = await controller.setExtensionApiProbeWorkspace({
  ownedId: 'session-a',
  root: '/repo/'
});
const firstOwner = { ownedId: first.ownedId, generation: first.generation, root: first.activeRoot };
const terminal = await controller.createExtensionApiProbeTerminal({ ...firstOwner, cwd: '/repo' });
assert.match(terminal.ownedId, /^mcb-extension-probe:session-a:/);
assert.equal(controller.showExtensionApiProbeTerminal(firstOwner), true);
assert.equal(await controller.writeExtensionApiProbeTerminal(firstOwner, 'probe\r'), true);
assert.equal(await controller.resizeExtensionApiProbeTerminal(firstOwner, 100, 30), true);
assert.deepEqual(await controller.acquireExtensionApiProbeScm(firstOwner), firstOwner);
assert.equal(controller.readExtensionApiProbeScm(firstOwner)?.branch, 'main');

const second = await controller.setExtensionApiProbeWorkspace({
  ownedId: 'session-b',
  root: '/repo'
});
assert.equal(second.generation, first.generation + 1);
assert.deepEqual(
  log.filter(([kind]) => kind === 'terminal-dispose' || kind === 'scm-dispose'),
  [
    ['terminal-dispose', 1],
    ['scm-dispose', 'session-a', first.generation]
  ],
  'same-root session switch disposes both old leases before publishing the next generation'
);
await assert.rejects(
  () => controller.writeExtensionApiProbeTerminal(firstOwner, 'stale\r'),
  /stale or belongs to another session/
);
assert.throws(
  () => controller.readExtensionApiProbeScm(firstOwner),
  /stale or belongs to another session/
);

const secondOwner = {
  ownedId: second.ownedId,
  generation: second.generation,
  root: second.activeRoot
};
await assert.rejects(
  () => controller.createExtensionApiProbeTerminal({ ...secondOwner, cwd: '/outside' }),
  /outside the active root/
);
await controller.createExtensionApiProbeTerminal(secondOwner);
await controller.disposeExtensionApiProbeRuntime();
assert.equal(
  log.filter(([kind]) => kind === 'terminal-dispose').length,
  2,
  'runtime teardown closes the live disposable probe PTY'
);

console.log('extensionApiProbeController.test.mjs: all checks passed');
