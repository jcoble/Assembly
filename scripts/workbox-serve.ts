// The single dev serve path, also used by app:dev. Source is always selected from an explicit commit.
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { adapterSources, backendIdentity, committedVersion, remoteBehindExit, requireCleanBackend, lane, sourcesDigest, type Lane } from './dev-remote-identity.ts';
import { command, lifetime, quote, sshOptions } from './dev-remote-process.ts';
import { probeSource, type Probe } from './dev-remote-probe.ts';
import { toolKey } from './dev-toolchain.ts';
import { stage } from './dev-remote-timing.ts';

const root = fileURLToPath(new URL('../', import.meta.url));
const owner = lifetime();
const total = stage('total');
const identityCheck = stage('identity');
try {
  const [name, number, selected = 'HEAD', mainGuard] = process.argv.slice(2);
  const main = !name && number === '0' && mainGuard === 'clean-origin-main';
  const target: Lane = main ? { name: '', number: 0, unit: 'assembly-dotnet', data: 'assembly-dotnet', port: 9797 } : lane(name ?? '', Number(number));
  const git = (...args: string[]) => command('git', args, { cwd: root, signal: owner.signal });
  const commit = await git('rev-parse', '--verify', `${selected}^{commit}`);
  // Recheck the manual main guard here too: the public TS CLI cannot bypass workbox.sh's protection.
  if (main) {
    await git('fetch', '-q', 'origin', 'main');
    if (commit !== await git('rev-parse', 'origin/main') || commit !== await git('rev-parse', 'HEAD') || await git('status', '--porcelain'))
      throw new Error('main host requires a clean checkout of origin/main');
  }
  // "check" compares the one version number (desktop/Cargo.toml) and changes nothing: a launch runs it first. "1" is the one update, which waits
  // for a busy host to go idle. The serve command itself refuses a busy host. Both of those match the build exactly.
  const mode = process.env.ASSEMBLY_DEV_REMOTE_SYNC;
  if (mode !== 'check') await requireCleanBackend(root, owner.signal);
  if (commit !== await git('rev-parse', 'HEAD')) throw new Error('serve must select this checkout commit');
  const identity = await backendIdentity(root, commit, owner.signal);
  const managed = process.env.ASSEMBLY_DEV_SYNC_REQUEST ? JSON.parse(process.env.ASSEMBLY_DEV_SYNC_REQUEST) : undefined;
  if (managed && (managed.owner?.name !== target.name || managed.owner?.number !== target.number)) throw Error('managed checkout and remote lane identity differ; no remote operation performed');
  const launcher = createHash('sha256').update(await command('git', ['show', `${commit}:scripts/adapters/claude-launcher.ts`], { cwd: root, signal: owner.signal, raw: true })).digest('hex');
  const sources = await sourcesDigest(adapterSources, file => command('git', ['show', `${commit}:${file}`], { cwd: root, signal: owner.signal, raw: true }).catch(() => { owner.signal.throwIfAborted(); return undefined; }));
  const read = `export PATH="$HOME/.local/bin:$HOME/.dotnet:/usr/local/bin:/usr/bin:/bin"; node --input-type=module -e ${quote(probeSource)} ${quote(JSON.stringify(target))}`;
  const probe = JSON.parse(await command('ssh', [...sshOptions, 'workbox-test', read], { signal: owner.signal })) as Probe;
  if (mode === 'check') {
    const version = await committedVersion(root, commit, owner.signal);
    if (!probe.missing && probe.version === version && (!managed || probe.seedCohort === managed.cohort))
      console.log(`Workbox Test ${target.unit} is on version ${version} (host commit ${probe.commit}); nothing to do`);
    else {
      console.log(`Workbox Test ${target.unit} is on version ${probe.missing ? 'none, not installed' : probe.version ?? 'unknown'}; this checkout is on ${version}`);
      process.exitCode = remoteBehindExit;
    }
    process.exit();
  }
  const actual = probe.missing ? 'not installed' : probe.backendIdentity ?? 'unknown';
  const wanted = identity;
  identityCheck();
  const adaptersCurrent = probe.adapterOK && probe.launcherSha === launcher && (managed ? probe.toolsKey === toolKey(managed.versions, sources, 'linux-x64') : probe.adapterSources === sources);
  if (actual === wanted && adaptersCurrent && (!managed || probe.seedCohort === managed.cohort)) {
    console.log(`Workbox Test ${target.unit} already matches ${wanted} (host commit ${probe.commit}); no deploy`);
  } else {
    if (managed?.adopted) throw Error('adopted/shared lane needs its creator to update the remote; no deployment performed');
    if (managed?.offline) throw Error('remote update check unavailable offline; current remote was preserved');
    console.log(`Workbox Test ${target.unit} backend changed: ${actual} -> ${wanted}; building and deploying ${commit}`);
    const transfer = stage('transfer');
    await command('git', ['push', '-q', 'ssh://workbox-test/~/assembly.git', `+${commit}:refs/heads/dev-sync-${name || 'main'}`], { cwd: root, signal: owner.signal, live: true });
    transfer({ payload: 'git changed source objects', binaryBytes: 0 });
    const request = JSON.stringify({ lane: target, commit, identity, launcher, sources, probe, waitForIdle: mode === '1', managed });
    // Kernel-owned lock covers probe, build, package and activation across Mac/Workbox launches. No stale PID lock.
    const checkout = `${target.data}/checkouts/build`;
    const script = `export PATH="$HOME/.local/bin:$HOME/.dotnet:/usr/local/bin:/usr/bin:/bin"; set -eu; `
      + `test -d "$HOME/${checkout}" || git -C "$HOME/assembly.git" worktree add -q --detach "$HOME/${checkout}" ${quote(commit)}; `
      + `cd "$HOME/${checkout}"; test -z "$(git status --porcelain)" || { echo 'lane build checkout has edits; preserving it' >&2; exit 1; }; `
      + `git checkout -q --detach ${quote(commit)}; exec node scripts/workbox-serve-host.ts ${quote(request)}`;
    const remote = `flock -n -E 75 "$HOME/${target.data}/dev-sync.lock" bash -c ${quote(script)}`;
    await command('ssh', [...sshOptions, 'workbox-test', remote], { signal: owner.signal, live: true });
  }
} catch (error) {
  console.error(`Workbox Test sync: ${(error instanceof Error ? error.message : String(error)).replace(/\s+/g, ' ')}`);
  process.exitCode = 1;
} finally { owner.close(); total(); }
