/**
 * Pins how the Files tree loads folders: through the machine that owns them,
 * what a folder deleted on disk takes with it, and that a listing for a
 * tree the user left is dropped.
 *
 * Run: node --experimental-strip-types scripts/explorerService.test.ts
 */
import assert from 'node:assert/strict';
import test from 'node:test';

(globalThis as unknown as { $state: unknown }).$state = Object.assign(<T>(value: T): T => value, { raw: <T>(value: T): T => value });
type Entry = { path: string; name: string; isDirectory: boolean; excluded: boolean };
type Call = { command: string; args: Record<string, unknown> };
const calls: Call[] = [];
/** Each folder's listing, by native path on its machine. */
let disk: Record<string, Entry[]> = {};
let held: Array<() => void> | null = null;
let listingStarted: (() => void) | null = null;
(globalThis as unknown as { window: unknown }).window = {
  __TAURI_INTERNALS__: {
    async invoke(command: string, args: Record<string, unknown>): Promise<unknown> {
      calls.push({ command, args });
      const listing = command === 'list_source_directory' ? args
        : command === 'remote_workspace' && args.operation === 'list_source_directory' ? args.args as Record<string, unknown>
          : null;
      if (!listing) return null;
      const entries = disk[listing.directory as string] ?? [];
      if (held) await new Promise<void>((resolve) => { held!.push(resolve); listingStarted?.(); });
      return entries;
    }
  }
};
const service = await import('../src/lib/shell/files/explorer/explorerService.ts');
const store = await import('../src/lib/shell/files/explorer/explorerStore.svelte.ts');

const file = (path: string): Entry => ({ path, name: path.slice(path.lastIndexOf('/') + 1), isDirectory: false, excluded: false });
const folder = (path: string): Entry => ({ ...file(path), isDirectory: true });
const treePaths = (): string[] => store.explorerNodes().map((node) => node.path).sort();

test('a remote project tree is listed by its own machine and its rows keep that machine', async () => {
  calls.length = 0;
  disk = { '/home/me/repo': [folder('/home/me/repo/src'), file('/home/me/repo/README.md')] };
  await service.scanRoot('assembly-remote://workbox-test/home/me/repo');
  assert.deepEqual(calls.filter((call) => call.command === 'remote_workspace').map((call) => ({ profileId: call.args.profileId, operation: call.args.operation, root: (call.args.args as Record<string, unknown>).root })), [
    { profileId: 'workbox-test', operation: 'list_source_directory', root: '/home/me/repo' }
  ]);
  assert.equal(calls.some((call) => call.command === 'list_source_directory'), false, 'nothing is listed on the Mac');
  assert.deepEqual(treePaths(), [
    'assembly-remote://workbox-test/home/me/repo/README.md',
    'assembly-remote://workbox-test/home/me/repo/src'
  ]);
});

test('a folder deleted on disk takes its loaded children and the selection inside it', async () => {
  disk = {
    '/repo': [folder('/repo/src'), file('/repo/README.md')],
    '/repo/src': [file('/repo/src/a.ts')]
  };
  await service.scanRoot('/repo');
  await service.loadDirectory('/repo/src', 1);
  store.selectPath('/repo/src/a.ts');
  disk['/repo'] = [file('/repo/README.md')];
  assert.equal(await service.loadDirectory('/repo', 0), true);
  assert.deepEqual(treePaths(), ['/repo/README.md']);
  assert.equal(store.explorer.selectedPath, null);
  assert.equal(store.loadedExplorerDirectoryDepth('/repo/src'), null, 'a later refresh does not list the deleted folder');
});

test('a listing that arrives after the user switched to another tree is dropped', async () => {
  disk = { '/old': [file('/old/stale.ts')], '/new': [file('/new/fresh.ts')] };
  held = [];
  const oldStarted = new Promise<void>((resolve) => { listingStarted = resolve; });
  const leaving = service.scanRoot('/old');
  await oldStarted;
  const newStarted = new Promise<void>((resolve) => { listingStarted = resolve; });
  const arriving = service.scanRoot('/new');
  await newStarted;
  listingStarted = null;
  assert.equal(held.length, 2, 'both listings are waiting on their explicit response gates');
  for (const release of held) release();
  held = null;
  await Promise.all([leaving, arriving]);
  assert.equal(store.explorer.root, '/new');
  assert.deepEqual(treePaths(), ['/new/fresh.ts']);
});
