// The one lane resource lifecycle, called by lane.sh and workbox.sh. Existing unmarked folders are never adopted/deleted.
import { promises as fs } from 'node:fs';
import { hostname, homedir } from 'node:os';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import { lane, type Lane } from './dev-remote-identity.ts';
import { command, quote, sshOptions } from './dev-remote-process.ts';
import { profileFor, safePath } from './seed-dev.ts';

export type LaneOwner = { version: 1; name: string; number: number; owner: string; host: string; worktree: string; remoteOwner?: LaneOwner };
export function validateOwner(owner: LaneOwner, root: string, home: string) {
  const target = lane(owner.name, owner.number);
  if (owner.version !== 1 || !/^[a-f0-9-]{36}$/.test(owner.owner) || !owner.host
    || owner.worktree !== join(home, 'dev/work/worktrees/Assembly', owner.name) || root !== owner.worktree)
    throw Error('lane ownership is not derived from this checkout; copied lanes cannot clean shared resources');
  return target;
}
async function atomic(file: string, value: unknown) {
  safePath(file, false);
  await fs.mkdir(dirname(file), { recursive: true });
  const temporary = file + '.' + randomUUID();
  await fs.writeFile(temporary, JSON.stringify(value) + '\n', { mode: 0o600, flag: 'wx' });
  await fs.rename(temporary, file);
}
// Self-contained so SSH executes this same implementation without installing another cleanup script remotely.
export async function hostResources(owner: LaneOwner, operation: 'reserve' | 'inspect' | 'clean' | 'identity', userHome?: string) {
  const fs = await import('node:fs/promises');
  const { join, dirname } = await import('node:path');
  const { hostname } = await import('node:os');
  const { promisify } = await import('node:util');
  const exec = promisify((await import('node:child_process')).execFile);
  const run = async (tool: string, args: string[]) => (await exec(tool, args, { encoding: 'utf8' })).stdout.trim();
  const home = userHome ?? process.env.HOME!;
  const target = lane(owner.name, owner.number);
  if (owner.version !== 1 || !/^[a-f0-9-]{36}$/.test(owner.owner)) throw Error('invalid lane ownership');
  const root = join(home, target.data), marker = join(root, 'lane-owner.json');
  const unit = join(home, '.config/systemd/user', target.unit + '.service');
  const tokenDirectory = join(home, '.config', target.data);
  async function exists(path: string) { try { await fs.lstat(path); return true; } catch (error) { if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error; return false; } }
  async function physical(path: string) {
    for (let at = path; ; at = dirname(at)) {
      if (await exists(at) && await fs.realpath(at) !== at) throw Error('lane path is redirected: ' + at);
      if (dirname(at) === at) break;
    }
  }
  await physical(root); await physical(unit); await physical(tokenDirectory);
  if (operation === 'reserve') {
    if (await exists(root)) throw Error('lane folder already exists; existing leftovers are preserved');
    for (const entry of await fs.readdir(home)) if (entry.startsWith('assembly-dotnet-')) {
      try { if ((await fs.readFile(join(home, entry, 'lane-number'), 'utf8')).trim() === String(target.number)) throw Error('lane number is already reserved'); }
      catch (error) { if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error; }
    }
    await fs.mkdir(root, { mode: 0o700 });
    await fs.writeFile(marker, JSON.stringify(owner) + '\n', { mode: 0o600, flag: 'wx' });
    await fs.writeFile(join(root, 'lane-number'), String(target.number) + '\n', { mode: 0o600, flag: 'wx' });
    return { host: hostname(), reserved: true };
  }
  if (!await exists(root)) return { host: hostname(), missing: true };
  if (operation === 'identity') {
    const creator = JSON.parse(await fs.readFile(marker, 'utf8'));
    if (creator.name !== owner.name || creator.number !== owner.number) throw Error('lane reservation differs');
    return { host: hostname(), owner: creator };
  }
  // A reservation number is not ownership: historic leftovers and copied .lane files fail closed.
  if (!await exists(marker) || JSON.stringify(JSON.parse(await fs.readFile(marker, 'utf8'))) !== JSON.stringify(owner)
    || (await fs.readFile(join(root, 'lane-number'), 'utf8')).trim() !== String(target.number))
    throw Error('lane has no matching managed owner; existing leftovers/shared reservations are preserved');
  const show = await run('systemctl', ['--user', 'show', target.unit, '-p', 'MainPID', '-p', 'ControlGroup']);
  const values = Object.fromEntries(show.split('\n').map(line => line.split('=')));
  const pid = values.MainPID;
  if (pid && pid !== '0') {
    const args = (await fs.readFile('/proc/' + pid + '/cmdline', 'utf8')).split('\0');
    if (!args[1]?.startsWith(root + '/bin/') || !args.includes('serve') || args[args.indexOf('--port') + 1] !== String(target.port))
      throw Error('lane unit does not own the derived backend/port');
    const group = values.ControlGroup;
    if (!group?.endsWith('/' + target.unit + '.service')) throw Error('lane cgroup differs');
    const members = new Set<string>();
    for (const file of await fs.readdir('/sys/fs/cgroup' + group, { recursive: true }))
      if (file === 'cgroup.procs' || file.endsWith('/cgroup.procs'))
        for (const member of (await fs.readFile(join('/sys/fs/cgroup' + group, file), 'utf8')).trim().split('\n')) if (member) members.add(member);
    if (members.size !== 1 || !members.has(pid)) throw Error('lane is busy with a running session; no cleanup performed');
  } else {
    try {
      if (await run('lsof', ['-nP', '-iTCP:' + target.port, '-sTCP:LISTEN', '-Fp'])) throw Error('lane port is busy; no cleanup performed');
    } catch (error) { if ((error as { code?: number }).code !== 1) throw error; }
  }
  const checkout = join(root, 'checkouts/build');
  if (await exists(join(checkout, '.git')) && await run('git', ['-C', checkout, 'status', '--porcelain']))
    throw Error('lane build checkout has edits; no cleanup performed');
  if (operation === 'inspect') return { host: hostname(), missing: false };
  if (pid && pid !== '0') await run('systemctl', ['--user', 'stop', target.unit]);
  if (await exists(join(checkout, '.git'))) await run('git', ['-C', join(home, 'assembly.git'), 'worktree', 'remove', checkout]);
  await fs.rm(unit, { force: true });
  await run('systemctl', ['--user', 'daemon-reload']);
  await fs.rm(root, { recursive: true });
  await fs.rm(tokenDirectory, { recursive: true, force: true });
  if (await exists(join(home, 'assembly.git'))) {
    // Any other checkouts under the folder just went with it. The deploy's branch is the lane's last trace on the box.
    await run('git', ['-C', join(home, 'assembly.git'), 'worktree', 'prune']);
    await run('git', ['-C', join(home, 'assembly.git'), 'update-ref', '-d', 'refs/heads/dev-sync-' + owner.name]);
  }
  return { host: hostname(), removed: true };
}
async function remote(owner: LaneOwner, operation: 'reserve' | 'inspect' | 'clean') {
  const source = `const lane=${lane.toString()}; const result=await (${hostResources.toString()})(${JSON.stringify(owner)},${JSON.stringify(operation)}); console.log(JSON.stringify(result));`;
  const invocation = `export PATH="$HOME/.local/bin:$HOME/.dotnet:/usr/local/bin:/usr/bin:/bin"; mkdir -p "$HOME/.cache/assembly-dev"; flock "$HOME/.cache/assembly-dev/lane-reservations.lock" node --input-type=module -e ${quote(source)}`;
  return JSON.parse(await command('ssh', [...sshOptions, 'workbox-test', invocation]));
}
const recordPath = (name: string, home: string) => join(home, 'assembly-dev/lanes', name + '.json');
async function removeLocalOutputs(root: string, paths: string[]) {
  for (const path of [...paths, join(root, 'desktop/target')]) {
    safePath(path, false); await fs.rm(path, { recursive: true, force: true });
  }
  try {
    for (const entry of await fs.readdir(join(root, 'dotnet'), { withFileTypes: true }))
      if (entry.isDirectory() && entry.name.startsWith('Assembly.')) for (const output of ['bin', 'obj']) {
        const path = join(root, 'dotnet', entry.name, output); safePath(path, false); await fs.rm(path, { recursive: true, force: true });
      }
  } catch (error) { if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error; }
}
export async function cleanLane(root: string, staleStart = false, home = homedir()) {
  root = safePath(resolve(root), !staleStart);
  const name = root.split('/').at(-1)!;
  let owner: LaneOwner;
  try { owner = JSON.parse(await fs.readFile(safePath(recordPath(name, home)), 'utf8')); }
  catch { throw Error(`lane ${name} has no ownership record on this machine (made by hand, copied or from before cleanup existed); nothing was touched`); }
  const target = validateOwner(owner, root, home);
  if (owner.host !== hostname()) throw Error('only the lane creator can clean its remote; adopted/shared lane is preserved');
  if (!staleStart) {
    const config = await fs.readFile(join(root, '.lane'), 'utf8');
    if (!config.includes(`LANE_NAME=${name}\n`) || !config.includes(`LANE_NUMBER=${owner.number}\n`) || !config.includes(`LANE_OWNER=${owner.owner}\n`) || config.includes('LANE_ADOPTED=1'))
      throw Error('copied/adopted lane cannot clean a shared reservation');
    const worktrees = await command('git', ['worktree', 'list', '--porcelain'], { cwd: root });
    for (const line of worktrees.split('\n')) if (line.startsWith('worktree ')) {
      const other = line.slice(9); if (other === root) continue;
      try { const config = await fs.readFile(join(other, '.lane'), 'utf8'); if (new RegExp(`^LANE_(NAME=${name}|NUMBER=${owner.number})$`, 'm').test(config)) throw Error('lane reservation is shared by ' + other); }
      catch (error) { if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error; }
    }
  }
  const profile = staleStart ? join(home, 'assembly-dev/worktrees', name + '-' + (await import('node:crypto')).createHash('sha256').update(root).digest('hex').slice(0, 16)) : profileFor(root, home).home;
  const data = join(home, target.data), token = join(home, '.config', target.data);
  for (const file of ['launcher.lock', 'backend.lock.pid']) {
    try {
      const text = await fs.readFile(join(profile, file), 'utf8');
      const pid = file === 'launcher.lock' ? JSON.parse(text).launcher : Number(text.trim());
      if (!Number.isInteger(pid) || pid < 1) throw Error('cannot prove lane lock owner is stopped');
      try { process.kill(pid, 0); throw Error('lane is busy; no cleanup performed'); }
      catch (error) { if ((error as NodeJS.ErrnoException).code !== 'ESRCH') throw error; }
    } catch (error) { if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error; }
  }
  const exec = (await import('node:util')).promisify((await import('node:child_process')).execFile);
  for (const port of [18797 + target.number * 10, 1430 + target.number, target.port + 10000]) {
    try {
      if ((await exec('lsof', ['-nP', '-iTCP:' + port, '-sTCP:LISTEN', '-Fp'], { encoding: 'utf8' })).stdout.trim())
        throw Error('lane local port is busy; no cleanup performed');
    } catch (error) { if ((error as { code?: number }).code !== 1) throw error; }
  }
  // Existing lane.sh remove performs the checkout/process and merge guards before this point.
  const cwdFiles = await command('lsof', ['-a', '-u', String(process.getuid!()), '-d', 'cwd', '-Fn']);
  for (const line of cwdFiles.split('\n')) if (line.startsWith('n')) {
    const held = line.slice(1);
    if ([profile, data].some(path => held === path || held.startsWith(path + '/'))) throw Error('lane is busy; no cleanup performed');
  }
  for (const path of [profile, data, token, join(root, 'desktop/target')]) safePath(path, false);
  await remote(owner, 'inspect');
  await remote(owner, 'clean');
  await removeLocalOutputs(root, [profile, data, token]);
  await fs.rm(recordPath(name, home));
  console.log(`lane ${name} resources cleaned; source worktree preserved`);
}
if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const [mode, root, rawNumber] = process.argv.slice(2);
  try {
    if (mode === 'init' || mode === 'adopt') {
      const home = homedir(), name = root.split('/').at(-1)!;
      const owner: LaneOwner = { version: 1, name, number: Number(rawNumber), owner: randomUUID(), host: hostname(), worktree: resolve(root) };
      validateOwner(owner, owner.worktree, home);
      if (mode === 'adopt') {
        const creator = (await remote(owner, 'identity')).owner;
        if (!creator) throw Error('legacy reservation has no managed owner; preserved');
        await atomic(recordPath(name, home), { ...owner, owner: creator.owner, remoteOwner: creator });
        console.log(creator.owner);
      } else {
        await atomic(recordPath(name, home), owner);
        await remote(owner, 'reserve'); console.log(owner.owner);
      }
    } else if (mode === 'local') {
      const home = homedir(), name = root.split('/').at(-1)!;
      const binding = JSON.parse(await fs.readFile(recordPath(name, home), 'utf8'));
      validateOwner(binding, resolve(root), home);
      if (!(await fs.readFile(join(root, '.lane'), 'utf8')).includes('LANE_ADOPTED=1')) throw Error('local-only cleanup is for an adopted checkout');
      const profile = profileFor(root, home).home;
      try { await fs.access(join(profile, 'launcher.lock')); throw Error('adopted lane is busy or has an unresolved launcher lock'); }
      catch (error) { if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error; }
      await removeLocalOutputs(root, [profile]); await fs.rm(recordPath(name, home));
      console.log('adopted checkout cleaned locally; creator-owned remote and shared data preserved');
    } else if (mode === 'stale') {
      const name = root.split('/').at(-1)!;
      try { await fs.access(recordPath(name, homedir())); }
      catch (error) { if ((error as NodeJS.ErrnoException).code === 'ENOENT') process.exit(0); throw error; }
      await cleanLane(root, true);
    } else if (mode === 'clean') await cleanLane(root);
    else throw Error('usage: dev-lane-clean.ts init|stale|clean <derived worktree> [number]');
  } catch (error) { console.error(error instanceof Error ? error.message : String(error)); process.exitCode = 1; }
}
