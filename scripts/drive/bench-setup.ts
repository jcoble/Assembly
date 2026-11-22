// The socket half of `scripts/bench.sh up`: the local backend runs this checkout's backend, Workbox Test is saved and
// connected and its host's backend is this checkout's too, a small repo is a project on each machine, and the logins
// are checked. Writes the bench state that journeys read (BENCH_STATE), atomically.
// The served commit can differ from the source commit: a commit that changes no backend input isn't redeployed, so
// what is checked is the backend identity (wait-welcome.ts), and both commits are recorded.
import { execFile, spawn } from 'node:child_process';
import { once } from 'node:events';
import { appendFileSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import { createServer, type AddressInfo } from 'node:net';
import { promisify } from 'node:util';
import { waitForWelcome } from '../wait-welcome.ts';
import type { BenchState, Machine } from './journey.ts';
import { connectSocket } from './socket.ts';

const exec = promisify(execFile);
const env = (name: string) => { const value = process.env[name]; if (!value) throw new Error(`${name} is not set`); return value; };
const run = async (tool: string, args: string[]) => (await exec(tool, args, { encoding: 'utf8' })).stdout.trim();
const quote = (text: string) => `'${text.replaceAll("'", "'\\''")}'`;
// A script and its arguments on Workbox Test, each quoted once for the remote shell.
const remote = (script: string, ...args: string[]) =>
  run('ssh', ['-o', 'BatchMode=yes', 'workbox-test', ['bash', '-lc', script, '_', ...args].map(quote).join(' ')]);

async function freePort(): Promise<number> {
  const server = createServer();
  await new Promise<void>((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  const port = (server.address() as AddressInfo).port;
  await new Promise<void>(resolve => server.close(() => resolve()));
  return port;
}

async function throughTunnel<T>(boxPort: number, use: (base: string) => Promise<T>): Promise<T> {
  const port = await freePort();
  const ssh = spawn('ssh', ['-N', '-n', '-o', 'BatchMode=yes', '-o', 'ExitOnForwardFailure=yes',
    '-L', `127.0.0.1:${port}:127.0.0.1:${boxPort}`, 'workbox-test'], { stdio: 'ignore' });
  appendFileSync(env('BENCH_PIDS'), `tunnel=${ssh.pid}\n`);
  const exited = once(ssh, 'exit');
  try { return await use(`http://127.0.0.1:${port}`); }
  finally { if (ssh.exitCode === null && ssh.signalCode === null) { ssh.kill(); await exited; } }
}

const api = `http://127.0.0.1:${env('DESKTOP_PORT')}`;
const tokenFile = env('TOKEN_FILE');
const token = readFileSync(tokenFile, 'utf8').match(/^ASSEMBLY_TOKEN=(.*)$/m)?.[1];
if (!token) throw new Error(`no ASSEMBLY_TOKEN in ${tokenFile}`);
const sourceCommit = await run('git', ['rev-parse', 'HEAD']);

console.log(`local backend: ${await waitForWelcome({ name: 'the local backend', base: api, commit: sourceCommit, seconds: 60 }, token)}`);
const control = new AbortController();
const socket = await connectSocket(api, token, { signal: control.signal });
try {
  const box = { name: 'workbox-test', ssh: 'workbox-test', port: Number(env('BOX_PORT')), tokenFile: env('BOX_TOKEN_FILE') };
  await socket.call('machines', 'set', box);
  const connected = await socket.call('machines', 'connect', { machine: box.name });
  if (!connected?.reachable) throw new Error(`Workbox Test is unreachable: ${connected?.reason ?? 'no reason given'}`);
  // The host is checked through a tunnel of our own, recorded in the bench's pids as it starts and stopped here.
  const boxToken = await remote('sed -n "s/^ASSEMBLY_TOKEN=//p" ~/"$1"', box.tokenFile);
  const servedCommit = await throughTunnel(box.port, base => waitForWelcome(
    { name: 'the Workbox Test host (through the tunnel)', base, commit: sourceCommit, seconds: 60 }, boxToken));
  console.log(`Workbox Test host: ${servedCommit}`);

  // A tiny repo to work in, on each machine, made once.
  const makeRepo = '[ -d "$1/.git" ] || { mkdir -p "$1" && cd "$1" && git init -q && echo notes > README && git add README && git -c user.name=bench -c user.email=bench@example.invalid commit -qm notes; }';
  const localRepo = `${env('DATA')}/fixtures/notes-repo`;
  await run('bash', ['-c', makeRepo, '_', localRepo]);
  const boxRepo = `${await remote('echo $HOME')}/${env('BOX_DATA')}/fixtures/notes-repo`;
  await remote(makeRepo, boxRepo);
  const projects = {} as BenchState['projects'];
  for (const [machine, path] of [['local', localRepo], ['workbox-test', boxRepo]] as [Machine, string][]) {
    const added = await socket.call('projects', 'add', { machine, path });
    projects[machine] = { id: added.project.id, path };
    console.log(`project on ${machine}: ${path}`);
  }

  // Logins: a failure is recorded per case, and the journey reports that case BLOCKED: provider.
  const blocked: Record<string, string> = {};
  const checks = { claude: 'claude auth status', codex: 'codex login status' };
  for (const [agent, check] of Object.entries(checks)) {
    for (const machine of ['local', 'workbox-test'] as Machine[]) {
      try { machine === 'local' ? await run('bash', ['-c', check]) : await remote(check); }
      catch (error) {
        const said = String((error as { stdout?: string; stderr?: string }).stderr || (error as { stdout?: string }).stdout || error).trim().split('\n')[0];
        blocked[`${agent}@${machine}`] = `login check failed: ${said}`;
        console.log(`BLOCKED: provider: ${agent}@${machine}: ${said}`);
      }
    }
  }

  const state: BenchState = {
    sourceCommit, servedCommit, api, tokenFile, display: env('BENCH_DISPLAY'), dbusAddress: env('BENCH_DBUS'),
    appLog: env('BENCH_APP_LOG'), projects, blocked
  };
  const file = env('BENCH_STATE');
  writeFileSync(`${file}.new`, JSON.stringify(state, null, 2) + '\n');
  renameSync(`${file}.new`, file);
} finally {
  await socket.close();
}
