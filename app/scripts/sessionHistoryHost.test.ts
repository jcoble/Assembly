/**
 * sessionHistoryHost.test.ts — how History's scan fills the rail, machine by machine (TSK-1167).
 *
 * The host is plain TypeScript over the rail store, so the `$state` stand-in runs it here. `$lib/` resolves to
 * `src/lib`, and the native calls in `tauriSource.ts` are replaced by one stub whose scans the test answers by hand.
 */
import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';

(globalThis as { $state?: unknown }).$state = <T>(value: T): T => value;

type Scan = { machine: string; resolve(rows: unknown[]): void; reject(error: Error): void };
const scans: Scan[] = [];
(globalThis as { __historyScan?: unknown }).__historyScan = (machine: string) =>
  new Promise((resolve, reject) => scans.push({ machine, resolve, reject }));

const tauriSourceStub = `
  export const listAgentSessionsFromTauri = (machine) => globalThis.__historyScan(machine ?? 'local');
  export const listAgentSessionsFromLocalBridge = async () => null;
  export const listAgentSessionsForProjectFromTauri = async () => [];
  export const readAgentSessionDetailsFromTauri = async () => [];
  export const readRemoteSavedSessionDetailsFromTauri = async () => null;
`;

registerHooks({
  resolve(specifier, context, nextResolve) {
    const local = specifier.startsWith('$lib/') ? new URL(`../src/lib/${specifier.slice(5)}`, import.meta.url).href : specifier;
    const resolved = nextResolve(local, context);
    return resolved.url.endsWith('/src/lib/tauriSource.ts')
      ? { url: `data:text/javascript,${encodeURIComponent(tauriSourceStub)}`, shortCircuit: true }
      : resolved;
  }
});

const { registerSessionHistoryHost } = await import('../src/lib/shell/history/sessionHistoryHost.ts');
const { rail } = await import('../src/lib/shell/stores/sessionRailStore.svelte.ts');

function row(id: string, machine?: string): Record<string, unknown> {
  return { provider: 'claude', id, title: id, model: null, projectPath: '/work', lastActivity: null, resumeCommands: [], logPath: `/logs/${id}.jsonl`, ...(machine ? { machine } : {}) };
}

const listed = () => rail.available.map((session) => `${session.machine ?? 'local'}:${session.id}`).sort();

function reset(): void {
  scans.length = 0;
  rail.available = [row('old-local'), row('old-box', 'box')] as never;
  rail.remoteConnections = { box: 'connected', away: 'disconnected' };
  rail.scanningMachines = [];
  rail.runningElsewhere = [];
  rail.scanning = false;
}

function scanFor(machine: string, from = 0): Scan {
  const scan = scans.slice(from).find((candidate) => candidate.machine === machine);
  assert.ok(scan, `no scan was asked of ${machine}`);
  return scan;
}

const host = () => registerSessionHistoryHost({ openOwned: async () => {}, deleteOwned: async () => {} });

// A remote machine whose scan fails ends its own wait instead of holding its rows' resume forever.
{
  reset();
  const handle = host();
  const done = handle.rescan();
  scanFor('box').reject(new Error('connection dropped'));
  scanFor('local').resolve([row('new-local')]);
  const warn = console.warn;
  console.warn = () => {};
  try { await done; } finally { console.warn = warn; }
  assert.ok(listed().includes('local:new-local'));
  assert.deepEqual(rail.scanningMachines, []);
  assert.equal(rail.scanning, false);
  handle.release();
}

// Loading History again forgets which resumes were refused as running outside Assembly, so they can be tried again.
{
  reset();
  rail.runningElsewhere = ['claude:running'];
  const handle = host();
  const done = handle.rescan();
  assert.deepEqual(rail.runningElsewhere, []);
  for (const scan of scans) scan.resolve([]);
  await done;
  handle.release();
}

// A newer load wins: an older one that answers late changes nothing, and the newer one ends the scanning state.
{
  reset();
  const handle = host();
  const first = handle.rescan();
  const second = handle.rescan();
  const asked = scans.length / 2;
  scanFor('local', asked).resolve([row('newer-local')]);
  scanFor('box', asked).resolve([row('newer-box')]);
  await second;
  assert.deepEqual(listed(), ['box:newer-box', 'local:newer-local']);
  assert.equal(rail.scanning, false);
  scanFor('local').resolve([row('stale-local')]);
  scanFor('box').resolve([row('stale-box')]);
  await first;
  assert.deepEqual(listed(), ['box:newer-box', 'local:newer-local'], 'the older load is not shown');
  handle.release();
}

// A load whose owner stopped it shows nothing it reads afterwards.
{
  reset();
  const handle = host();
  const stop = new AbortController();
  const done = handle.rescan(stop.signal);
  stop.abort();
  for (const scan of scans) scan.resolve([row(`late-${scan.machine}`)]);
  await done;
  assert.deepEqual(listed(), ['box:old-box', 'local:old-local']);
  handle.release();
}

console.log('sessionHistoryHost: all tests passed');
