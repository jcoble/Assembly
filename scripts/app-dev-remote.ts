import { spawn } from 'node:child_process';
import { appendFileSync, closeSync, mkdirSync, openSync, readFileSync, writeFileSync, renameSync } from 'node:fs';
import { join } from 'node:path';

// Owned by app-dev until it exits; closing the app stops it, and the next launch picks up again. No recurring check.
// Its progress and any failure reach the desktop through the status file (DevRemoteSyncStatus).
export function startRemoteUpdate(root: string, home: string, env: NodeJS.ProcessEnv) {
  const directory = join(home, 'logs', 'remote-sync');
  mkdirSync(directory, { recursive: true });
  const log = join(directory, `${Date.now()}-${process.pid}.log`);
  const status = join(home, 'remote-sync-state.json');
  const publish = (updating: boolean, reason: string | null) => {
    const temporary = `${status}.${process.pid}`;
    writeFileSync(temporary, JSON.stringify({ updating, reason }), { mode: 0o600 });
    renameSync(temporary, status);
  };
  publish(false, null);
  closeSync(openSync(log, 'wx', 0o600));
  const worker = spawn(process.execPath, [join(root, 'scripts/update-machines.ts'), '--if-behind'], { cwd: root, stdio: ['ignore', 'pipe', 'pipe'], detached: true, env });
  let lastReason: string | null = null;
  const consume = () => {
    let buffered = '';
    return (bytes: Buffer) => {
      appendFileSync(log, bytes);
      buffered += bytes;
      const lines = buffered.split('\n'); buffered = lines.pop()!;
      for (const line of lines) if (line.startsWith('ASSEMBLY_REMOTE_SYNC ')) {
        try { lastReason = JSON.parse(line.slice('ASSEMBLY_REMOTE_SYNC '.length)).reason; publish(true, lastReason); } catch { }
      }
    };
  };
  worker.stdout.on('data', consume()); worker.stderr.on('data', consume());
  console.warn(`app:dev: checking this lane's remote version in the background; log: ${log}`);
  let finished = false;
  const done = new Promise<void>(resolve => {
    worker.once('error', error => { finished = true; publish(false, `The remote update failed: ${error.message}`); console.warn(`app:dev: the remote update could not start: ${error.message}; log: ${log}`); resolve(); });
    worker.once('close', code => {
      finished = true;
      const detail = readFileSync(log, 'utf8').trim().split('\n').filter(line => !line.startsWith('[dev-sync timing]') && !line.startsWith('ASSEMBLY_REMOTE_SYNC ')).at(-1) || `update exited ${code}`;
      publish(false, code === 0 ? null : lastReason?.startsWith('Remote backend') ? lastReason : `The remote update failed: ${detail}`);
      console.warn(`app:dev: ${detail}; log: ${log}`);
      resolve();
    });
  });
  return async () => { if (!finished && worker.pid) { try { process.kill(-worker.pid, 'SIGTERM'); } catch { } } await done; };
}
