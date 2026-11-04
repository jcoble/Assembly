import { spawn } from 'node:child_process';
import { appendFileSync, closeSync, mkdirSync, openSync, readFileSync, writeFileSync, renameSync } from 'node:fs';
import { join } from 'node:path';

// Owned by app-dev until its cargo process exits. Process group is joined on shutdown; no recurring check.
export function startRemoteSync(root: string, home: string) {
  const directory = join(home, 'logs', 'remote-sync');
  mkdirSync(directory, { recursive: true });
  const log = join(directory, `${Date.now()}-${process.pid}.log`);
  const status = join(home, 'remote-sync-state.json');
  const publish = (reason: string | null) => {
    const temporary = `${status}.${process.pid}`;
    writeFileSync(temporary, JSON.stringify({ reason }), { mode: 0o600 });
    renameSync(temporary, status);
  };
  publish(null);
  const output = openSync(log, 'wx', 0o600);
  const worker = spawn('bash', ['dotnet/scripts/workbox.sh', 'serve'], { cwd: root, stdio: ['ignore', 'pipe', 'pipe'], detached: true, env: { ...process.env, ASSEMBLY_DEV_REMOTE_SYNC: '1' } });
  let lastReason: string | null = null;
  const consume = () => {
    let buffered = '';
    return (bytes: Buffer) => {
      appendFileSync(log, bytes);
      buffered += bytes;
      const lines = buffered.split('\n'); buffered = lines.pop()!;
      for (const line of lines) if (line.startsWith('ASSEMBLY_REMOTE_SYNC ')) {
        try { lastReason = JSON.parse(line.slice('ASSEMBLY_REMOTE_SYNC '.length)).reason; publish(lastReason); } catch { }
      }
    };
  };
  worker.stdout.on('data', consume()); worker.stderr.on('data', consume());
  closeSync(output);
  console.warn(`app:dev: checking this lane's Workbox Test backend in the background; log: ${log}`);
  let finished = false;
  const done = new Promise<void>(resolve => {
    worker.once('error', error => { lastReason = `Remote backend sync failed: ${error.message}`; publish(lastReason); console.warn(`app:dev: Workbox Test sync could not start: ${error.message}; log: ${log}`); });
    worker.once('close', code => {
      finished = true;
      const detail = readFileSync(log, 'utf8').trim().split('\n').filter(line => !line.startsWith('[dev-sync timing]')).at(-1) || `sync exited ${code}`;
      publish(code === 0 ? null : lastReason ?? `Remote backend sync failed: ${detail}`);
      console.warn(`app:dev: ${detail}; log: ${log}`);
      resolve();
    });
  });
  return async () => { if (!finished && worker.pid) { try { process.kill(-worker.pid, 'SIGTERM'); } catch { } } await done; };
}
