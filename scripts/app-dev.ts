import { spawnSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

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

// A new profile on a checkout ahead of the published adapters has nothing to install: the newest release can lack what
// this checkout requires, and only the default branch publishes. Dev builds the same bundle here. Only app:dev does
// this; the installer itself still fails clearly, so delivered packages and remote hosts never build on demand.
function buildAgents(): string {
  const packager = join(root, 'scripts/package-adapters.ts');
  const output = join(root, 'desktop/target/local-adapters');
  console.warn('app:dev: no adapter release could be installed; building one locally');
  const versions = spawnSync(process.execPath, [packager, 'resolve'], { cwd: root, env, encoding: 'utf8', stdio: ['ignore', 'pipe', 'inherit'] });
  if (versions.error) throw versions.error;
  if (versions.status !== 0) process.exit(versions.status ?? 1);
  run(process.execPath, [packager, 'build', `${process.platform}-${process.arch}`, versions.stdout.trim(), output]);
  return output;
}

// Use the same ports and data paths as the existing Assembly launcher.
const lane = spawnSync('bash', ['-c', `
source scripts/lane-env.sh || exit
printf '%s\\0' "$DESKTOP_PORT" "$DATA" "$TOKEN_FILE" "$BOX_PORT" "$BOX_TOKEN_FILE"
`], { cwd: root, env, encoding: 'utf8' });
if (lane.error) throw lane.error;
if (lane.status !== 0) {
  process.stderr.write(lane.stderr);
  process.exit(lane.status ?? 1);
}
const [port, data, tokenFile, remotePort, remoteTokenFile] = lane.stdout.split('\0');
const home = join(data, 'mac');

run('dotnet', [
  'publish', join(root, 'dotnet/Assembly.Desktop'), '--configuration', 'Debug',
  '--no-self-contained', '-p:UseAppHost=true', '--output', backend,
]);
mkdirSync(backend, { recursive: true });
writeFileSync(join(backend, 'lane.json'), JSON.stringify({
  port: Number(port), home, tokenFile, remotePort: Number(remotePort), remoteTokenFile,
}, null, 2) + '\n');
mkdirSync(home, { recursive: true });
const installer = ['dotnet/scripts/agents-file.sh', process.env.ADAPTERS ?? join(data, 'adapters'), join(home, 'agents.json')];
const installed = spawnSync('bash', installer, { cwd: root, env, stdio: 'inherit' });
if (installed.error) throw installed.error;
if (installed.status !== 0) run('bash', [...installer, buildAgents()]);
run('cargo', ['run', '--locked', '--', ...process.argv.slice(2)], join(root, 'desktop'));
