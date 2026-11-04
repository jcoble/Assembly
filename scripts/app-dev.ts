import { spawn, spawnSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { basename, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { startRemoteSync } from './app-dev-remote.ts';
import { profileFor } from './seed-dev.ts';

const root = fileURLToPath(new URL('../', import.meta.url));
const backend = join(root, 'desktop/target/dev-backend');
const env = {
  ...process.env,
  DOTNET_CLI_TELEMETRY_OPTOUT: '1',
  DOTNET_NOLOGO: '1',
  MSBUILDDISABLENODEREUSE: '1',
  ASSEMBLY_BACKEND_DIR: backend,
};

async function run(command: string, args: string[], cwd = root): Promise<void> {
  const child = spawn(command, args, { cwd, env, stdio: 'inherit', detached: true });
  const stop = () => { if (child.pid) { try { process.kill(-child.pid, 'SIGTERM'); } catch {} } };
  process.once('SIGINT', stop);
  process.once('SIGTERM', stop);
  try {
    await new Promise<void>((resolve, reject) => {
      child.once('error', reject);
      child.once('close', code => code === 0 ? resolve() : reject(Object.assign(new Error(`${command} exited ${code}`), { exitCode: code ?? 1 })));
    });
  } finally { process.removeListener('SIGINT', stop); process.removeListener('SIGTERM', stop); }
}

// A new profile on a checkout ahead of the published adapters has nothing to install: the newest release can lack what
// this checkout requires, and only the default branch publishes. Dev builds the same bundle here. Only app:dev does
// this; the installer itself still fails clearly, so delivered packages and remote hosts never build on demand.
function buildAgents(): string | undefined {
  const packager = join(root, 'scripts/package-adapters.ts');
  const output = join(root, 'desktop/target/local-adapters');
  console.warn('app:dev: no adapter release could be installed; building one locally');
  const versions = spawnSync(process.execPath, [packager, 'resolve'], { cwd: root, env, encoding: 'utf8', stdio: ['ignore', 'pipe', 'inherit'] });
  if (versions.error || versions.status !== 0) return undefined;
  const built = spawnSync(process.execPath, [packager, 'build', `${process.platform}-${process.arch}`, versions.stdout.trim(), output], { cwd: root, env, stdio: 'inherit' });
  return built.error || built.status !== 0 ? undefined : output;
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
const [port, home, tokenFile, remotePort, remoteTokenFile, adapters, laneName] = lane.status === 0 ? lane.stdout.split('\0') : ownSettings();

let stopSync = async () => {};
try { if (lane.status === 0 && laneName) stopSync = startRemoteSync(root, home); }
catch (error) { console.warn(`app:dev: Workbox Test sync could not start: ${error instanceof Error ? error.message : String(error)}`); }
try {
  await run('dotnet', [
    'publish', join(root, 'dotnet/Assembly.Desktop'), '--configuration', 'Debug',
    '--no-self-contained', '-p:UseAppHost=true', '--output', backend,
  ]);
  mkdirSync(backend, { recursive: true });
  writeFileSync(join(backend, 'lane.json'), JSON.stringify({
    port: Number(port), home, tokenFile, remotePort: Number(remotePort), remoteTokenFile,
  }, null, 2) + '\n');
  mkdirSync(home, { recursive: true });
  const installer = ['dotnet/scripts/agents-file.sh', process.env.ADAPTERS ?? adapters, join(home, 'agents.json')];
  const installed = spawnSync('bash', installer, { cwd: root, env, stdio: 'inherit' });
  if (installed.error) throw installed.error;
  // The backend starts with an empty list when there's no agents file, so failing here doesn't stop the app.
  if (installed.status !== 0) {
    const built = buildAgents();
    const retried = built ? spawnSync('bash', [...installer, built], { cwd: root, env, stdio: 'inherit' }) : undefined;
    if (retried?.status !== 0) console.warn('app:dev: agents could not be updated; opening with the ones already installed, if any');
  }
  await run('cargo', ['run', '--locked', '--', ...process.argv.slice(2)], join(root, 'desktop'));
} catch (error) {
  console.error(error instanceof Error ? error.message : String(error));
  process.exitCode = (error as { exitCode?: number }).exitCode ?? 1;
} finally { await stopSync(); }
