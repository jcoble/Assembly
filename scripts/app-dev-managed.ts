// One managed lane launch check, with one upstream version resolution shared with the remote worker.
import { promises as fs } from 'node:fs';
import { createHash } from 'node:crypto';
import { join } from 'node:path';
import { homedir, hostname } from 'node:os';
import { validateOwner, type LaneOwner } from './dev-lane-clean.ts';
import { profileFor, takeLauncherLock, main as seed } from './seed-dev.ts';
import { seedCohort, seedCurrent, recordSeed, trackedManifest } from './dev-seed-cohort.ts';
import { checkoutSources, ensureTools, latestTools, toolKey, toolsCurrent } from './dev-toolchain.ts';

// A lane made by hand or with a copied .lane has no record binding it to this checkout. It still launches, the old
// way: only the checks and cleanup that could touch a shared reservation stay off.
export async function isManagedLane(root: string) {
  const physical = profileFor(root).root;
  try {
    const owner = JSON.parse(await fs.readFile(join(homedir(), 'assembly-dev/lanes', physical.split('/').at(-1) + '.json'), 'utf8')) as LaneOwner;
    return (await fs.readFile(join(physical, '.lane'), 'utf8')).includes(`LANE_OWNER=${owner.owner}\n`);
  } catch (error) { if ((error as NodeJS.ErrnoException).code === 'ENOENT') return false; throw error; }
}
export async function prepareManagedLane(root: string, signal: AbortSignal) {
  const profile = profileFor(root); root = profile.root;
  const name = root.split('/').at(-1)!;
  const owner = JSON.parse(await fs.readFile(join(homedir(), 'assembly-dev/lanes', name + '.json'), 'utf8')) as LaneOwner;
  const target = validateOwner(owner, root, homedir());
  const config = await fs.readFile(join(root, '.lane'), 'utf8');
  if (!config.includes(`LANE_NAME=${owner.name}\n`) || !config.includes(`LANE_NUMBER=${owner.number}\n`) || !config.includes(`LANE_OWNER=${owner.owner}\n`) || owner.host !== hostname())
    throw Error('managed launch needs its creator-owned lane; copied/adopted reservations cannot update shared resources');
  const home = profile.home, release = takeLauncherLock(home);
  try {
    const manifest = process.env.ASSEMBLY_DEV_SEED_MANIFEST ?? trackedManifest(root);
    const cohort = await seedCohort(manifest);
    let versions, offline = false;
    try { versions = await latestTools(signal); }
    catch (error) {
      signal.throwIfAborted();
      const cached = JSON.parse(await fs.readFile(join(home, 'adapters/dev-tools.json'), 'utf8'));
      if (!cached.versions || !await toolsCurrent(join(home, 'adapters'), join(home, 'agents.json'), toolKey(cached.versions, await checkoutSources(root), process.platform + '-' + process.arch))) throw error;
      versions = cached.versions; offline = true;
      console.warn('app:dev: update check unavailable; using verified cached tools for offline use');
    }
    await ensureTools(root, join(home, 'adapters'), join(home, 'agents.json'), versions, signal);
    return { target, home, owner, release, versions, cohort, manifest, offline, adopted: config.includes('LANE_ADOPTED=1'),
      remoteManifest: process.env.ASSEMBLY_DEV_REMOTE_SEED_MANIFEST,
      seed: async () => {
        if (!await seedCurrent(home, cohort)) {
          signal.throwIfAborted();
          await seed(['--worktree', root, '--manifest', manifest], true);
          signal.throwIfAborted();
          await recordSeed(home, cohort);
        }
      } };
  } catch (error) { release(); throw error; }
}
