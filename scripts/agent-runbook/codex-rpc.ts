import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';

export async function withCodex<T>(binary: string, action: (request: (method: string, params: any) => Promise<any>) => Promise<T>, env = process.env, onNotification: (message: any) => void = () => {}): Promise<T> {
  const child = spawn(binary, ['app-server', '--listen', 'stdio://'], { env, stdio: ['pipe', 'pipe', 'pipe'] });
  let next = 0;
  let errorText = '';
  child.stderr.on('data', data => { errorText = (errorText + data).slice(-1000); });
  const pending = new Map<number, { resolve: (value: any) => void; reject: (error: Error) => void; timer: ReturnType<typeof setTimeout> }>();
  const fail = (error: Error) => { for (const p of pending.values()) { clearTimeout(p.timer); p.reject(error); } pending.clear(); };
  child.on('error', fail);
  const exited = new Promise<void>(resolve => child.on('close', () => { fail(Error('Codex app-server exited: ' + errorText)); resolve(); }));
  const lines = createInterface({ input: child.stdout });
  lines.on('line', line => {
    let message: any;
    try { message = JSON.parse(line); } catch { return; }
    const p = pending.get(message.id);
    if (!p) { onNotification(message); return; }
    clearTimeout(p.timer); pending.delete(message.id);
    message.error ? p.reject(Error(JSON.stringify(message.error))) : p.resolve(message.result);
  });
  function request(method: string, params: any) {
    return new Promise<any>((resolve, reject) => {
      const id = ++next;
      const timer = setTimeout(() => { pending.delete(id); reject(Error('Codex request timed out: ' + method)); }, 20000);
      pending.set(id, { resolve, reject, timer });
      child.stdin.write(JSON.stringify({ id, method, params }) + '\n');
    });
  }
  try {
    await request('initialize', { clientInfo: { name: 'agent-runbook', version: '1' }, capabilities: { experimentalApi: true } });
    child.stdin.write(JSON.stringify({ method: 'initialized' }) + '\n');
    return await action(request);
  } finally {
    child.stdin.end();
    const timer = setTimeout(() => child.kill('SIGTERM'), 3000);
    try { await exited; } finally { clearTimeout(timer); lines.close(); }
  }
}
