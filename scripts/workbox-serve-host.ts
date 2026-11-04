// Workbox Test only. flock owns the whole invocation; SSH stdin EOF cancels this owner and its build children.
import { promises as fs } from 'node:fs';
import { createHash, randomUUID } from 'node:crypto';
import { join } from 'node:path';
import { spawn } from 'node:child_process';
import { installedRelease, prepareBackend, pruneReleases } from './dev-remote-build.ts';
import { sameBackendFiles } from './remote-backend-package.ts';
import { backendIdentity, lane, type Lane } from './dev-remote-identity.ts';
import { command, lifetime } from './dev-remote-process.ts';
import { probeSource, type Probe } from './dev-remote-probe.ts';
import { ensureTools, latestTools, toolKey } from './dev-toolchain.ts';
import { waitForIdle } from './dev-remote-idle.ts';
import { stage } from './dev-remote-timing.ts';

const owner = lifetime(true);
const request = JSON.parse(process.argv[2]);
const target: Lane = request.lane.number === 0 ? { name: '', number: 0, unit: 'assembly-dotnet', data: 'assembly-dotnet', port: 9797 } : lane(request.lane.name, request.lane.number);
if (JSON.stringify(target) !== JSON.stringify(request.lane) || !/^[a-f0-9]{40}$/.test(request.commit)) throw Error('invalid lane request');
const root = join(process.env.HOME!, target.data), unitFile = join(process.env.HOME!, '.config/systemd/user', target.unit + '.service');
const run = (tool: string, args: string[], live = false) => command(tool, args, { signal: owner.signal, live });
const probe = async (adaptersOnly = false) => JSON.parse(await run(process.execPath, ['--input-type=module', '-e', probeSource, JSON.stringify(target), ...(adaptersOnly ? ['--adapters'] : [])])) as Probe;
const digest = (value: string | Buffer) => createHash('sha256').update(value).digest('hex');
async function atomic(file: string, text: string) {
  const temporary = file + '.' + randomUUID();
  await fs.writeFile(temporary, text, { mode: 0o600, flag: 'wx' });
  await fs.rename(temporary, file);
}
async function members(pid: string) {
  const group = await run('systemctl', ['--user', 'show', target.unit, '-p', 'ControlGroup', '--value']);
  if (!group.endsWith('/' + target.unit + '.service')) throw Error('unit control group differs');
  const members = new Set<string>();
  const directory = '/sys/fs/cgroup' + group;
  for (const file of await fs.readdir(directory, { recursive: true }))
    if (file === 'cgroup.procs' || file.endsWith('/cgroup.procs'))
      for (const member of (await fs.readFile(join(directory, file), 'utf8')).trim().split('\n')) if (member) members.add(member);
  return [...members];
}
async function idle(pid: string) {
  await waitForIdle(() => members(pid), pid, root, request.waitForIdle === true, owner.signal);
}
async function ready(pid: string) {
  // Journal replay for this exact new PID avoids a connection retry loop and startup races.
  await new Promise<void>((resolve, reject) => {
    const journal = spawn('journalctl', ['--user', '-u', target.unit, `_PID=${pid}`, '--since', '1 minute ago', '--follow', '-o', 'cat'], { stdio: ['ignore', 'pipe', 'pipe'] });
    let text = '', failure: Error | undefined;
    const stop = () => { journal.kill('SIGTERM'); };
    const abort = () => { failure = Error('sync cancelled before host readiness'); stop(); };
    owner.signal.addEventListener('abort', abort, { once: true });
    const deadline = setTimeout(() => { failure = Error('new host did not announce readiness within 30 seconds'); stop(); }, 30_000);
    journal.stdout.on('data', bytes => { text += bytes; if (text.includes('listening on http://127.0.0.1:' + target.port)) stop(); });
    journal.on('error', error => { failure = error; stop(); });
    journal.on('close', () => { clearTimeout(deadline); owner.signal.removeEventListener('abort', abort); failure ? reject(failure) : text.includes('listening on ') ? resolve() : reject(Error('host journal ended before readiness')); });
    if (owner.signal.aborted) abort();
  });
}
let stopped = false, candidateStarted = false, originalUnit: string | undefined;
try {
  if (process.env.HOME !== '/home/blackcolours') throw Error('unexpected Workbox Test account');
  for (const part of ['', 'bin', 'serve']) {
    const directory = join(root, part);
    try { if (await fs.realpath(directory) !== directory) throw Error('lane directories must not be symlinks'); }
    catch (error) { if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error; }
  }
  if (request.managed && JSON.stringify(JSON.parse(await fs.readFile(join(root, 'lane-owner.json'), 'utf8'))) !== JSON.stringify(request.managed.owner))
    throw Error('managed lane reservation belongs to another checkout; no update performed');
  const before = await probe();
  const identity = await backendIdentity(process.cwd(), request.commit, owner.signal);
  if (identity !== request.identity) throw Error('source identity differs after transfer');
  const seedTools = request.managed ? await import('./dev-seed-cohort.ts') : undefined;
  const seedChanged = Boolean(request.managed && !await seedTools!.seedCurrent(join(root, 'serve'), request.managed.cohort));
  const seedManifest = request.managed && (request.managed.seedManifest ?? seedTools!.trackedManifest(process.cwd()));
  if (request.managed && await seedTools!.seedCohort(seedManifest) !== request.managed.cohort)
    throw Error('remote seed manifest differs from the local safe-backup cohort');
  const desiredTools = request.managed ? toolKey(request.managed.versions, request.sources, 'linux-x64') : undefined;
  const adaptersCurrent = before.adapterOK && before.launcherSha === request.launcher && (request.managed ? before.toolsKey === desiredTools : before.adapterSources === request.sources);
  const current = before.backendIdentity === identity;
  if (current && adaptersCurrent && !seedChanged) {
    console.log(`Workbox Test ${target.unit} already matches after acquiring the lane lock; no deploy`);
  } else {
    if (!before.missing) await idle(before.pid);
    // Built from this checkout's launcher: a published release can only match the launcher it was packaged with.
    let toolsChanged = false;
    if (request.managed || before.missing || !adaptersCurrent) {
      await fs.mkdir(join(root, 'serve'), { recursive: true });
      ({ changed: toolsChanged } = await ensureTools(process.cwd(), join(root, 'adapters'), join(root, 'serve/agents.json'), request.managed?.versions ?? await latestTools(owner.signal), owner.signal));
    }
    const adapterState = before.missing || request.managed || !adaptersCurrent ? await probe(true) : before;
    if (!adapterState.adapterOK || adapterState.launcherSha !== request.launcher)
      throw Error('lane ACP 2 adapter bundle still differs from this checkout launcher after updating it');
    if (!before.missing) await idle(before.pid);
    // A running host read its adapters when it started, so new ones need the restart; an idle host loses nothing.
    if (current && !seedChanged && !toolsChanged) {
      console.log('Workbox Test remote is right; toolchain checked, no backend build or restart');
    } else {
    const previous = before.missing ? undefined : await installedRelease(before.dll);
    const prepared = current && previous ? { identity: before.backendIdentity, commit: before.commit, directory: previous.directory }
      : await prepareBackend(root, request, before.missing ? undefined : before.dll, owner.signal);
    const destination = prepared.directory;
    const candidateManifest = JSON.parse(await fs.readFile(join(destination, 'manifest.json'), 'utf8'));
    const previousManifest = previous?.manifest;
    if (previousManifest && sameBackendFiles(previousManifest.files, candidateManifest.files) && !seedChanged && !toolsChanged) {
      console.log('Workbox Test compiled backend files are unchanged; no restart');
    } else {
      const latest = await probe();
      if (latest.missing !== before.missing || latest.pid !== before.pid || latest.unitSha !== before.unitSha) throw Error('lane host changed during build; no restart performed');
      if (!before.missing) await idle(before.pid);
      const unit = before.missing ? `[Unit]
Description=Assembly Workbox Test development host
[Service]
Type=simple
UMask=0077
WorkingDirectory=%h/${target.data}/serve
Environment=PATH=%h/.dotnet:%h/.local/bin:/usr/local/bin:/usr/bin:/bin
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
Environment=DOTNET_NOLOGO=1
ExecStart=%h/.dotnet/dotnet INITIAL_BACKEND serve --port ${target.port} --home ${root}/serve --agents ${root}/serve/agents.json --token-file ${process.env.HOME}/.config/${target.data}/env
KillMode=control-group
TimeoutStopSec=30
` : await fs.readFile(unitFile, 'utf8');
      if (!before.missing) {
        originalUnit = unit;
        if (digest(unit) !== before.unitSha) throw Error('unit changed before activation');
      }
      const dll = join(destination, 'backend/Assembly.Remote.dll');
      const updated = unit.replace(before.missing ? 'INITIAL_BACKEND' : (before.unitDll ?? before.dll), dll);
      if (updated === unit && !current || updated.split(dll).length !== 2) throw Error('ambiguous unit backend path');
      owner.signal.throwIfAborted();
      const restartTime = stage('restart');
      if (!before.missing) {
        await run('systemctl', ['--user', 'stop', target.unit]);
        stopped = true;
      }
      if (seedChanged) {
        const seeder = await import('./seed-dev.ts');
        await seeder.main(['--worktree', process.cwd(), '--manifest', seedManifest, '--lane-remote', target.name, String(target.number)]);
        owner.signal.throwIfAborted();
        await seedTools!.recordSeed(join(root, 'serve'), request.managed.cohort);
      }
      await fs.mkdir(join(process.env.HOME!, '.config/systemd/user'), { recursive: true });
      await atomic(unitFile, updated);
      await run('systemctl', ['--user', 'daemon-reload']);
      // Candidate startup may migrate the DB; never roll code back automatically after this point.
      candidateStarted = true;
      await run('systemctl', ['--user', 'start', target.unit]);
      const pid = await run('systemctl', ['--user', 'show', target.unit, '-p', 'MainPID', '--value']);
      restartTime();
      const answerTime = stage('answer');
      await ready(pid);
      const after = await probe();
      if (after.backendIdentity !== prepared.identity || after.commit !== prepared.commit || (!before.missing && after.engine !== before.engine) || after.dll !== dll || request.managed && (after.toolsKey !== desiredTools || after.seedCohort !== request.managed.cohort)) throw Error('new host identity could not be verified; inspect lane host before retrying');
      answerTime();
      // Disk only: a failed cleanup never fails a host that is already verified.
      await pruneReleases(root, [destination, ...(previous ? [previous.directory] : [])])
        .catch(error => console.warn(`Workbox Test old release cleanup failed: ${error instanceof Error ? error.message : String(error)}`));
      console.log(`Workbox Test ${target.unit} deployed backend ${prepared.identity}; authenticated host commit ${after.commit}; PID ${after.pid}; engine preserved ${after.engine}`);
    }
    }
  }
} catch (error) {
  if (stopped && !candidateStarted && originalUnit) {
    try {
      await atomic(unitFile, originalUnit);
      await command('systemctl', ['--user', 'daemon-reload']);
      await command('systemctl', ['--user', 'start', target.unit]);
    } catch { console.error('previous host could not be restored before candidate startup; inspect the lane unit'); }
  }
  console.log('ASSEMBLY_REMOTE_SYNC ' + JSON.stringify({ reason: candidateStarted
    ? 'Remote backend failed to start. Host may be down; inspect it before retrying. No automatic rollback was attempted because startup may have migrated the database.'
    : `Remote backend build or deploy failed: ${error instanceof Error ? error.message : String(error)}` }));
  console.error(`Workbox Test sync failed${candidateStarted ? ' after candidate startup (host state needs inspection)' : ''}: ${error instanceof Error ? error.message : String(error)}`);
  process.exitCode = 1;
}
finally { owner.close(); }
