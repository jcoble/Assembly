import { spawn } from 'node:child_process';
import { closeSync, mkdirSync, openSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

// Owned by app-dev until its cargo process exits. No detached worker, recurring check or writable status mirror.
export function startRemoteSync(root: string, home: string) {
  const directory = join(home, 'logs', 'remote-sync');
  mkdirSync(directory, { recursive: true });
  const log = join(directory, `${Date.now()}-${process.pid}.log`);
  const output = openSync(log, 'wx', 0o600);
  const worker = spawn('bash', ['dotnet/scripts/workbox.sh', 'serve'], { cwd: root, stdio: ['ignore', output, output] });
  closeSync(output);
  console.warn(`app:dev: checking this lane's Workbox Test backend in the background; log: ${log}`);
  let finished = false;
  const done = new Promise<void>(resolve => {
    worker.once('error', error => { console.warn(`app:dev: Workbox Test sync could not start: ${error.message}; log: ${log}`); });
    worker.once('close', code => {
      finished = true;
      const detail = readFileSync(log, 'utf8').trim().split('\n').at(-1) || `sync exited ${code}`;
      console.warn(`app:dev: ${detail}; log: ${log}`);
      resolve();
    });
  });
  return async () => { if (!finished) worker.kill('SIGTERM'); await done; };
}
