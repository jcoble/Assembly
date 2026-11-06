import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { existsSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';

// update-machines forwards the SIGTERM its group already got, so its worker can be signalled twice. A second signal
// that kills the worker before it has stopped its ssh leaves the remote build running, holding its lock.
test('a second signal cannot kill an owner that is still stopping its work', { timeout: 20_000 }, async () => {
  const root = mkdtempSync(join(tmpdir(), 'assembly-lifetime-'));
  try {
    const [stopping, stopped] = [join(root, 'stopping'), join(root, 'stopped')];
    const script = join(root, 'owner.ts');
    writeFileSync(script, `import fs from 'node:fs';
import { lifetime } from ${JSON.stringify(new URL('./dev-remote-process.ts', import.meta.url).pathname)};
const owner = lifetime();
owner.signal.addEventListener('abort', () => {
  fs.writeFileSync(${JSON.stringify(stopping)}, '');
  // Long enough for the second signal to land while this is still going.
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 1000);
  fs.writeFileSync(${JSON.stringify(stopped)}, '');
  process.exit(0);
});
setInterval(() => {}, 1000);
console.log('ready');
`);
    const child = spawn(process.execPath, [script], { stdio: ['ignore', 'pipe', 'inherit'] });
    const exited = new Promise<number | null>(resolve => child.on('exit', resolve));
    await new Promise<void>(resolve => child.stdout.once('data', () => resolve()));
    child.kill('SIGTERM');
    while (!existsSync(stopping)) await new Promise(resolve => setTimeout(resolve, 5));
    child.kill('SIGTERM');
    assert.equal(await exited, 0);
    assert.ok(existsSync(stopped), 'it finished stopping');
  } finally { rmSync(root, { recursive: true, force: true }); }
});
