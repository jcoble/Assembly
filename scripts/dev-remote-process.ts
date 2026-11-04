// Every subprocess has one owner. Closing SSH stdin cancels the remote owner and its build group.
import { spawn } from 'node:child_process';

export const quote = (value: string) => "'" + value.replaceAll("'", "'\\''") + "'";
export const sshOptions = ['-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes', '-o', 'ConnectTimeout=8'];
export function command(command: string, args: string[], options: { cwd?: string; signal?: AbortSignal; live?: boolean; raw?: boolean; env?: NodeJS.ProcessEnv } = {}): Promise<string> {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd: options.cwd, detached: true, stdio: ['pipe', 'pipe', 'pipe'],
      env: { ...process.env, ...options.env, MSBUILDDISABLENODEREUSE: '1', DOTNET_NOLOGO: '1', DOTNET_CLI_TELEMETRY_OPTOUT: '1' } });
    let out = '', error = '';
    const stop = () => { if (child.pid) { try { process.kill(-child.pid, 'SIGTERM'); } catch {} } };
    options.signal?.addEventListener('abort', stop, { once: true });
    if (options.signal?.aborted) stop();
    child.stdout.on('data', bytes => { out += bytes; if (options.live) process.stdout.write(bytes); });
    child.stderr.on('data', bytes => { error += bytes; if (options.live) process.stderr.write(bytes); });
    child.on('error', reject);
    child.on('close', code => {
      options.signal?.removeEventListener('abort', stop);
      child.stdin.destroy();
      if (options.signal?.aborted) reject(new Error('remote sync cancelled with its launcher'));
      else if (code === 75) reject(new Error('another launch is already syncing this lane'));
      else if (code !== 0) reject(new Error(`${command} failed (${code}): ${error.trim().slice(-1800) || out.trim().slice(-1800)}`));
      else resolve(options.raw ? out : out.trim());
    });
  });
}
export function lifetime(remote = false) {
  const owner = new AbortController();
  const stop = () => owner.abort();
  for (const event of ['SIGTERM', 'SIGINT', 'SIGHUP'] as const) process.once(event, stop);
  if (remote) { process.stdin.resume(); process.stdin.once('end', stop); }
  return { signal: owner.signal, close() { if (remote) process.stdin.pause(); } };
}
