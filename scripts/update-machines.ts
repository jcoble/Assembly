// `npm run deploy:remote`: updates whatever is behind on this checkout's machine and its lane remote: adapters, their
// launcher and scripts, CLIs and the remote's backend. The desktop's update button runs this same command. A run with
// nothing behind builds nothing, and a running session is never killed: the remote finishes once its host goes idle.
// `--if-behind` is a dev launch: unless the remote's version number is lower it does nothing else at all, not even reading
// the network for tool versions, and when it's lower it does this same update. A remote on a higher number is never touched,
// by either: this checkout is the side that's behind.
import { spawn, spawnSync } from 'node:child_process';
import { existsSync, promises as fs } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { isManagedLane, managedTools } from './app-dev-managed.ts';
import type { LaneOwner } from './dev-lane-clean.ts';
import { remoteBehindExit } from './dev-remote-identity.ts';
import { lifetime } from './dev-remote-process.ts';
import { ensureTools, latestTools } from './dev-toolchain.ts';
import { seedCohort, trackedManifest } from './dev-seed-cohort.ts';
import { profileFor } from './seed-dev.ts';

const root = fileURLToPath(new URL('../', import.meta.url));
const owner = lifetime();
const say = (reason: string) => console.log('ASSEMBLY_REMOTE_SYNC ' + JSON.stringify({ reason }));

// The same folders app-dev uses: a managed lane's seeded profile, or this checkout's lane data.
function laneFolders() {
  const lane = spawnSync('bash', ['-c', 'source scripts/lane-env.sh || exit; printf "%s\\0" "$DATA/mac" "$DATA/adapters"'], { cwd: root, encoding: 'utf8' });
  if (lane.status !== 0) return { home: profileFor(root).home, adapters: join(profileFor(root).home, 'adapters') };
  const [home, adapters] = lane.stdout.split('\0');
  return { home, adapters: process.env.ADAPTERS ?? adapters };
}

async function managedLane() {
  const name = profileFor(root).root.split('/').at(-1)!;
  const laneOwner = JSON.parse(await fs.readFile(join(homedir(), 'assembly-dev/lanes', name + '.json'), 'utf8')) as LaneOwner;
  const manifest = process.env.ASSEMBLY_DEV_SEED_MANIFEST ?? trackedManifest(root);
  const config = await fs.readFile(join(root, '.lane'), 'utf8');
  return { cohort: await seedCohort(manifest), seedManifest: process.env.ASSEMBLY_DEV_REMOTE_SEED_MANIFEST,
    owner: laneOwner.remoteOwner ?? laneOwner, adopted: config.includes('LANE_ADOPTED=1') };
}

function remote(mode: string, request?: object) {
  return new Promise<number | null>((resolve, reject) => {
    const worker = spawn('bash', ['dotnet/scripts/workbox.sh', 'serve'], { cwd: root, stdio: 'inherit', env: {
      ...process.env, ASSEMBLY_DEV_REMOTE_SYNC: mode, ...(request ? { ASSEMBLY_DEV_SYNC_REQUEST: JSON.stringify(request) } : {}) } });
    owner.signal.addEventListener('abort', () => worker.kill('SIGTERM'), { once: true });
    worker.once('error', reject);
    worker.once('close', resolve);
  });
}

try {
  const managed = existsSync(join(root, '.lane')) && await isManagedLane(root);
  const { home, adapters } = managed ? { home: profileFor(root).home, adapters: join(profileFor(root).home, 'adapters') } : laneFolders();
  const lane = managed ? await managedLane() : undefined;
  const checked = process.argv.includes('--if-behind') ? await remote('check', lane) : remoteBehindExit;
  if (checked !== remoteBehindExit) { process.exitCode = checked ?? 1; process.exit(); }
  const request = lane && { ...await managedTools(root, home, owner.signal), ...lane };
  say('Updating this machine\'s adapters and CLIs.');
  const local = await ensureTools(root, adapters, join(home, 'agents.json'), request?.versions ?? await latestTools(owner.signal), owner.signal);
  console.log(local.changed ? 'this machine: adapters and CLIs updated' : 'this machine: adapters and CLIs already current');
  say('Checking the remote.');
  process.exitCode = await remote('1', request) ?? 1;
} catch (error) {
  console.error(`update: ${(error instanceof Error ? error.message : String(error)).replace(/\s+/g, ' ')}`);
  process.exitCode = 1;
} finally { owner.close(); }
