import { spawnSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { homedir } from 'node:os';
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
run('bash', [
  'dotnet/scripts/agents-file.sh',
  process.env.ADAPTERS ?? join(homedir(), 'spike-dotnet/adapters'),
  join(home, 'agents.json'),
]);
run('cargo', ['run', '--locked', '--', ...process.argv.slice(2)], join(root, 'desktop'));
