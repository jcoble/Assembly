import { spawn, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, renameSync, writeFileSync } from 'node:fs';
import { basename, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { startRemoteUpdate } from './app-dev-remote.ts';
import { profileFor, seedIfEmpty } from './seed-dev.ts';

const root = fileURLToPath(new URL('../', import.meta.url));
const backend = join(root, 'desktop/target/dev-backend');
const env: NodeJS.ProcessEnv = {
  ...process.env,
  DOTNET_CLI_TELEMETRY_OPTOUT: '1',
  DOTNET_NOLOGO: '1',
  MSBUILDDISABLENODEREUSE: '1',
  ASSEMBLY_BACKEND_DIR: backend,
  CARGO_PROFILE_DEV_DEBUG: '0', CARGO_INCREMENTAL: '0',
};

const launch = new AbortController();
const interrupt = () => launch.abort();
process.once('SIGINT', interrupt);
process.once('SIGTERM', interrupt);

async function attempt(command: string, args: string[], cwd = root, capture = false): Promise<{ status: number | null; stdout: string }> {
  launch.signal.throwIfAborted();
  const child = spawn(command, args, { cwd, env, stdio: ['inherit', capture ? 'pipe' : 'inherit', 'inherit'], detached: true });
  let stdout = '';
  child.stdout?.on('data', bytes => { stdout += bytes; });
  const stop = () => { if (child.pid) { try { process.kill(-child.pid, 'SIGTERM'); } catch {} } };
  launch.signal.addEventListener('abort', stop, { once: true });
  if (launch.signal.aborted) stop();
  try {
    const status = await new Promise<number | null>((resolve, reject) => {
      child.once('error', reject);
      child.once('close', resolve);
    });
    launch.signal.throwIfAborted();
    return { status, stdout };
  } finally { launch.signal.removeEventListener('abort', stop); }
}
async function run(command: string, args: string[], cwd = root): Promise<void> {
  const { status } = await attempt(command, args, cwd);
  if (status !== 0) throw Object.assign(new Error(`${command} exited ${status}`), { exitCode: status ?? 1 });
}

// A new profile on a checkout ahead of the published adapters has nothing to install: the newest release can lack what
// this checkout requires, and only the default branch publishes. Dev builds the same bundle here. Only app:dev does
// this; the installer itself still fails clearly, so delivered packages and remote hosts never build on demand.
async function buildAgents(): Promise<string | undefined> {
  const packager = join(root, 'scripts/package-adapters.ts');
  const output = join(root, 'desktop/target/local-adapters');
  console.warn('app:dev: no adapter release could be installed; building one locally');
  const versions = await attempt(process.execPath, [packager, 'resolve'], root, true);
  if (versions.status !== 0) return undefined;
  const built = await attempt(process.execPath, [packager, 'build', `${process.platform}-${process.arch}`, versions.stdout.trim(), output]);
  return built.status !== 0 ? undefined : output;
}

// Main and a lane worktree keep the settings they already have. Every other checkout runs on its own, so building
// never depends on setting anything up: its data sits in its seed-dev profile and its ports follow from that profile.
function ownSettings(): [port: string, home: string, tokenFile: string, remotePort: string, remoteTokenFile: string, adapters: string] {
  const home = profileFor(root).home;
  const offset = Number.parseInt(home.slice(-8), 16) % 9000;
  return [
    String(20000 + offset), home, join(home, 'env'), String(30000 + offset),
    `.config/assembly-dev-${basename(home)}/env`, join(home, 'adapters'),
  ];
}
const lane = spawnSync('bash', ['-c', `
source scripts/lane-env.sh || exit
printf '%s\\0' "$DESKTOP_PORT" "$DATA/mac" "$TOKEN_FILE" "$BOX_PORT" "$BOX_TOKEN_FILE" "$DATA/adapters" "$LANE_NAME"
`], { cwd: root, env, encoding: 'utf8' });
if (lane.error) throw lane.error;
let [port, configuredHome, tokenFile, remotePort, remoteTokenFile, adapters, laneName] = lane.status === 0 ? lane.stdout.split('\0') : ownSettings();

let home = configuredHome;
const isManaged = existsSync(join(root, '.lane')) && await (await import('./app-dev-managed.ts')).isManagedLane(root);
if (existsSync(join(root, '.lane')) && !isManaged) console.warn('app:dev: this checkout does not own its lane (made by hand or a copied .lane), so dependency checks, seed data and cleanup are off');
let managed: Awaited<ReturnType<typeof import('./app-dev-managed.ts').prepareManagedLane>> | undefined;

// The backend's update check compares against this checkout, and its update button runs scripts/update-machines.ts here.
env.ASSEMBLY_DEV_CHECKOUT = root;
// The one update runs alongside the builds, never after them: a remote on another version number never waits for the desktop.
let stopUpdate = async () => {};
const startUpdate = () => {
  env.ASSEMBLY_DEV_REMOTE_SYNC_STATUS = join(home, 'remote-sync-state.json');
  try { stopUpdate = startRemoteUpdate(root, home, env); }
  catch (error) { console.warn(`app:dev: the remote update could not start: ${error instanceof Error ? error.message : String(error)}`); }
};
if (!isManaged && lane.status === 0 && laneName) startUpdate();
try {
  if (isManaged) {
    managed = await (await import('./app-dev-managed.ts')).prepareManagedLane(root, launch.signal);
    home = managed.home;
    port = String(18797 + managed.target.number * 10);
    remotePort = String(managed.target.port);
    tokenFile = join(process.env.HOME!, '.config', managed.target.data, 'env');
    remoteTokenFile = `.config/${managed.target.data}/env`;
    env.ASSEMBLY_REMOTE = 'workbox-test';
    startUpdate();
  }
  const cache = existsSync(join(root, '.git')) ? await import('./dev-build-cache.ts') : undefined;
  const publish = async () => run('dotnet', [
    'publish', join(root, 'dotnet/Assembly.Desktop'), '--configuration', 'Debug',
    '--no-self-contained', '-p:UseAppHost=true', '--output', backend,
  ]);
  if (cache) await cache.ensureBuild(root, 'backend', [join(backend, 'Assembly.Desktop.dll'), join(backend, 'Assembly.Infrastructure.dll')], publish);
  else await publish();
  // A checkout on its own profile opens with data. Main and a hand-made lane keep theirs and are never written to.
  if (managed || lane.status !== 0) {
    try { await seedIfEmpty(root, Boolean(managed)); }
    catch (error) {
      launch.signal.throwIfAborted();
      console.warn(`app:dev: opening without seed data: ${error instanceof Error ? error.message : String(error)}`);
    }
  }
  mkdirSync(backend, { recursive: true });
  const laneFile = join(backend, 'lane.json');
  const temporaryLane = `${laneFile}.${process.pid}`;
  writeFileSync(temporaryLane, JSON.stringify({
    port: Number(port), home, tokenFile, remotePort: Number(remotePort), remoteTokenFile,
  }, null, 2) + '\n');
  renameSync(temporaryLane, laneFile);
  mkdirSync(home, { recursive: true });
  // No agents at all means nothing to run. Otherwise the update above, the button and `npm run deploy:remote` install them.
  if (!managed && !existsSync(join(home, 'agents.json'))) {
  const installer = ['dotnet/scripts/agents-file.sh', process.env.ADAPTERS ?? adapters, join(home, 'agents.json')];
  const installed = await attempt('bash', installer);
  // The backend starts with an empty list when there's no agents file, so failing here doesn't stop the app.
  if (installed.status !== 0) {
    const built = await buildAgents();
    const retried = built ? await attempt('bash', [...installer, built]) : undefined;
    if (retried?.status !== 0) console.warn('app:dev: agents could not be updated; opening with the ones already installed, if any');
  }
  }
  if (cache) {
    const executable = join(root, 'desktop/target/debug/assembly-desktop');
    await cache.ensureBuild(root, 'desktop', [executable], () => run('cargo', ['build', '--locked'], join(root, 'desktop')));
    console.warn('app:dev: lane dependencies checked; desktop and backend are current');
    await run(executable, process.argv.slice(2));
  } else await run('cargo', ['run', '--locked', '--', ...process.argv.slice(2)], join(root, 'desktop'));
} catch (error) {
  console.error(error instanceof Error ? error.message : String(error));
  process.exitCode = (error as { exitCode?: number }).exitCode ?? 1;
} finally {
  await stopUpdate();
  managed?.release();
  process.removeListener('SIGINT', interrupt);
  process.removeListener('SIGTERM', interrupt);
}
