// The single dev serve path, also used by app:dev. Source is always selected from an explicit commit.
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { backendIdentity, requireCleanBackend, lane, type Lane } from './dev-remote-identity.ts';
import { command, lifetime, quote, sshOptions } from './dev-remote-process.ts';
import { probeSource, type Probe } from './dev-remote-probe.ts';
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
  await requireCleanBackend(root, owner.signal);
  if (commit !== await git('rev-parse', 'HEAD')) throw new Error('serve must select this checkout commit');
  const identity = await backendIdentity(root, commit, owner.signal);
  const launcher = createHash('sha256').update(await command('git', ['show', `${commit}:scripts/adapters/claude-launcher.ts`], { cwd: root, signal: owner.signal, raw: true })).digest('hex');
  const read = `export PATH="$HOME/.local/bin:$HOME/.dotnet:/usr/local/bin:/usr/bin:/bin"; node --input-type=module -e ${quote(probeSource)} ${quote(JSON.stringify(target))}`;
  const probe = JSON.parse(await command('ssh', [...sshOptions, 'workbox-test', read], { signal: owner.signal })) as Probe;
  let actual = probe.missing ? 'not installed' : 'unknown';
  try { if (!probe.missing) actual = await backendIdentity(root, probe.commit, owner.signal); } catch { owner.signal.throwIfAborted(); }
  identityCheck();
  if (actual === identity && probe.adapterOK && probe.launcherSha === launcher) {
    console.log(`Workbox Test ${target.unit} already runs backend ${identity} (host commit ${probe.commit}); no deploy`);
  } else {
    console.log(`Workbox Test ${target.unit} backend changed: ${actual} -> ${identity}; building and deploying ${commit}`);
    const transfer = stage('transfer');
    await command('git', ['push', '-q', 'ssh://workbox-test/~/assembly.git', `+${commit}:refs/heads/dev-sync-${name || 'main'}`], { cwd: root, signal: owner.signal, live: true });
    transfer({ payload: 'git changed source objects', binaryBytes: 0 });
    const request = JSON.stringify({ lane: target, commit, identity, launcher, probe });
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
