import { spawnSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { basename, join } from 'node:path';
import { fileURLToPath } from 'node:url';
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

function run(command: string, args: string[], cwd = root): void {
  const result = spawnSync(command, args, { cwd, env, stdio: 'inherit' });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exit(result.status ?? 1);
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
printf '%s\\0' "$DESKTOP_PORT" "$DATA/mac" "$TOKEN_FILE" "$BOX_PORT" "$BOX_TOKEN_FILE" "$DATA/adapters"
`], { cwd: root, env, encoding: 'utf8' });
if (lane.error) throw lane.error;
const [port, home, tokenFile, remotePort, remoteTokenFile, adapters] = lane.status === 0 ? lane.stdout.split('\0') : ownSettings();

run('dotnet', [
  'publish', join(root, 'dotnet/Assembly.Desktop'), '--configuration', 'Debug',
  '--no-self-contained', '-p:UseAppHost=true', '--output', backend,
]);
mkdirSync(backend, { recursive: true });
writeFileSync(join(backend, 'lane.json'), JSON.stringify({
  port: Number(port), home, tokenFile, remotePort: Number(remotePort), remoteTokenFile,
}, null, 2) + '\n');
mkdirSync(home, { recursive: true });
// The backend starts with an empty list when there's no agents file, so a failed install doesn't stop the app.
const agents = spawnSync('bash', [
  'dotnet/scripts/agents-file.sh',
  process.env.ADAPTERS ?? adapters,
  join(home, 'agents.json'),
], { cwd: root, env, stdio: 'inherit' });
if (agents.error) throw agents.error;
if (agents.status !== 0) console.warn('app:dev: agents could not be updated; opening with the ones already installed, if any');
run('cargo', ['run', '--locked', '--', ...process.argv.slice(2)], join(root, 'desktop'));
