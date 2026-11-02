import { spawn, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { closeSync, constants, copyFileSync, existsSync, mkdirSync, openSync, readFileSync, realpathSync, renameSync, rmSync, unlinkSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:net';
import { basename, delimiter, dirname, join, resolve } from 'node:path';
import { homedir } from 'node:os';
import { fileURLToPath } from 'node:url';

const args = process.argv.slice(2);
let root = fileURLToPath(new URL('../', import.meta.url));
let build = true;
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--worktree' && args[i + 1]) root = resolve(args[++i]);
  else if (args[i] === '--no-build') build = false;
  else if (args[i] === '--help') {
    console.log('Usage: node scripts/app-dev.ts [--worktree PATH] [--no-build]\nBuild and open native Assembly with a persistent, isolated database for this worktree.\n--no-build opens the last successful build of the selected worktree.');
    process.exit(0);
  } else throw new Error(`Unknown or incomplete argument: ${args[i]}`);
}
root = realpathSync(root);
for (const path of ['desktop/Cargo.toml', 'dotnet/Assembly.Desktop/Assembly.Desktop.csproj']) {
  if (!existsSync(join(root, path))) throw new Error(`Not an Assembly worktree: ${root} (missing ${path})`);
}

const key = createHash('sha256').update(root).digest('hex').slice(0, 16);
const name = basename(root).replace(/[^a-zA-Z0-9-]/g, '-');
const home = join(homedir(), 'assembly-dev', 'worktrees', `${name}-${key}`);
const builds = join(root, 'desktop/target/dev-backend');
const currentFile = join(builds, 'current.json');
const backendName = process.platform === 'win32' ? 'Assembly.Desktop.exe' : 'Assembly.Desktop';
const env: NodeJS.ProcessEnv = {
  ...process.env,
  DOTNET_CLI_TELEMETRY_OPTOUT: '1',
  DOTNET_NOLOGO: '1',
  MSBUILDDISABLENODEREUSE: '1',
  CARGO_BUILD_JOBS: process.env.CARGO_BUILD_JOBS ?? '2',
};
// The native .NET apphost needs this on Macs whose SDK was installed outside the system search paths.
if (!env.DOTNET_ROOT) {
  const dotnet = (env.PATH ?? '').split(delimiter).map(folder => join(folder, 'dotnet')).find(existsSync);
  if (dotnet) env.DOTNET_ROOT = dirname(realpathSync(dotnet));
}
// An inherited shell variable must not connect a development database to a daily/test host.
delete env.ASSEMBLY_REMOTE;

function run(command: string, arguments_: string[], cwd = root): void {
  const result = spawnSync(command, arguments_, { cwd, env, stdio: 'inherit' });
  if (result.error) throw result.error;
  if (result.status !== 0) throw Object.assign(new Error(`${command} failed (${result.status ?? result.signal}).`), { exitCode: result.status ?? 1 });
}

function atomicJson(path: string, value: unknown): void {
  const temporary = `${path}.${process.pid}.tmp`;
  writeFileSync(temporary, JSON.stringify(value, null, 2) + '\n', { mode: 0o600 });
  renameSync(temporary, path);
}

function current(): { build: string; previous?: string } | undefined {
  if (!existsSync(currentFile)) return;
  const value = JSON.parse(readFileSync(currentFile, 'utf8'));
  if (!/^[0-9]+-[0-9]+$/.test(value.build)) throw new Error('Invalid development build receipt. Rebuild this worktree.');
  return value;
}

async function main(): Promise<void> {
mkdirSync(home, { recursive: true, mode: 0o700 });
const lock = join(home, 'launcher.lock');
let descriptor: number;
try { descriptor = openSync(lock, 'wx', 0o600); }
catch (error) {
  if ((error as NodeJS.ErrnoException).code !== 'EEXIST') throw error;
  throw new Error(`This worktree is already launching/running, or its previous launch left a lock: ${lock}. Close that app first; only remove a stale lock after its recorded processes have exited.`);
}
writeFileSync(descriptor, JSON.stringify({ launcher: process.pid, worktree: root }) + '\n');
closeSync(descriptor);
let staged: string | undefined;
let app: ReturnType<typeof spawn> | undefined;
const release = () => {
  if (existsSync(lock)) {
    const owner = JSON.parse(readFileSync(lock, 'utf8'));
    if (owner.launcher === process.pid) unlinkSync(lock);
  }
};
process.once('exit', release);
const interrupt = () => app ? app.kill('SIGINT') : process.exit(130);
const terminate = () => app ? app.kill('SIGTERM') : process.exit(143);
process.once('SIGINT', interrupt);
process.once('SIGTERM', terminate);
try {
mkdirSync(builds, { recursive: true });
const prior = current();
const buildId = `${Date.now()}-${process.pid}`;
let backend = prior ? join(builds, prior.build) : '';
if (!build && (!backend || !existsSync(join(backend, 'assembly-desktop')) || !existsSync(join(backend, backendName)))) {
  throw new Error('This worktree has no complete development build. Run without --no-build once.');
}
console.log(`Worktree: ${root}\nLocal database: ${join(home, 'assembly.db')}\nRemote: none selected; configure a host in Settings > Connections.`);

if (build) {
  staged = join(builds, `${buildId}.staging`);
  mkdirSync(staged);
  run('dotnet', [
    'publish', join(root, 'dotnet/Assembly.Desktop'), '--configuration', 'Debug',
    '--no-self-contained', '-p:UseAppHost=true', '-p:UseSharedCompilation=false', '-m:1', '--output', staged,
  ]);
  run('cargo', ['build', '--locked'], join(root, 'desktop'));
  const metadata = spawnSync('cargo', ['metadata', '--no-deps', '--format-version', '1'], {
    cwd: join(root, 'desktop'), env, encoding: 'utf8',
  });
  if (metadata.error) throw metadata.error;
  if (metadata.status !== 0) {
    process.stderr.write(metadata.stderr);
    throw Object.assign(new Error('Cargo metadata failed.'), { exitCode: metadata.status ?? 1 });
  }
  const target = JSON.parse(metadata.stdout).target_directory;
  if (typeof target !== 'string') throw new Error('Cargo did not report its target directory.');
  copyFileSync(join(target, 'debug', 'assembly-desktop'), join(staged, 'assembly-desktop'), constants.COPYFILE_FICLONE);
  if (!existsSync(join(staged, backendName))) throw new Error('Publish did not produce the desktop backend.');
  backend = join(builds, buildId);
  renameSync(staged, backend);
  staged = undefined;
  atomicJson(currentFile, { build: buildId, previous: prior?.build });
  // These are launcher-owned build artifacts, never profile data. Keep the current and preceding successful build.
  if (prior?.previous && /^[0-9]+-[0-9]+$/.test(prior.previous)) rmSync(join(builds, prior.previous), { recursive: true, force: true });
}

const agents = join(home, 'agents.json');
if (!existsSync(agents)) {
  const temporary = `${agents}.${process.pid}.tmp`;
  run('bash', [
    join(root, 'dotnet/scripts/agents-file.sh'),
    process.env.ADAPTERS ?? join(homedir(), 'assembly-dev/adapters'),
    temporary,
  ]);
  // A failed generator must never replace an existing configuration.
  JSON.parse(readFileSync(temporary, 'utf8'));
  renameSync(temporary, agents);
}

// Reserve a free loopback port for this launch rather than borrowing a lane or daily app's port.
const socket = createServer();
await new Promise<void>((done, reject) => {
  socket.once('error', reject);
  socket.listen(0, '127.0.0.1', done);
});
const address = socket.address();
if (!address || typeof address === 'string') throw new Error('No local backend port was allocated.');
const port = address.port;
await new Promise<void>((done, reject) => socket.close(error => error ? reject(error) : done()));
atomicJson(join(backend, 'lane.json'), {
  port, home, tokenFile: join(home, 'token.env'),
  remotePort: 9797, remoteTokenFile: '.config/assembly-dev/not-configured',
});
env.ASSEMBLY_BACKEND_DIR = backend;
app = spawn(join(backend, 'assembly-desktop'), [], { cwd: root, env, stdio: 'inherit' });
atomicJson(lock, { launcher: process.pid, app: app.pid, worktree: root });
const code = await new Promise<number>((done, reject) => {
  app!.once('error', reject);
  app!.once('close', code => done(code ?? 1));
});
process.exitCode = code;
} finally {
  if (staged) rmSync(staged, { recursive: true, force: true });
  release();
  process.removeListener('exit', release);
  process.removeListener('SIGINT', interrupt);
  process.removeListener('SIGTERM', terminate);
}
}

main().catch(error => {
  console.error(error.message);
  process.exitCode = error.exitCode ?? 1;
});
