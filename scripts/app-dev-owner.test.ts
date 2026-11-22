import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { pathToFileURL } from 'node:url';

test('a second launcher waits for the recorded same-home launcher to exit', async () => {
  const home = mkdtempSync(join(tmpdir(), 'assembly-launch-owner-'));
  const script = join(home, 'app-dev.ts');
  const module = pathToFileURL(join(import.meta.dirname, 'app-dev-owner.ts')).href;
  writeFileSync(script, `import { claimHome } from ${JSON.stringify(module)};
const release = await claimHome(process.argv[2], new AbortController().signal);
process.on('SIGTERM', () => { release(); process.exit(0); });
console.log('ready'); setInterval(() => {}, 1000);`);
  const children: ReturnType<typeof spawn>[] = [];
  try {
    const start = async () => {
      const child = spawn(process.execPath, [script, home], { stdio: ['ignore', 'pipe', 'inherit'] });
      children.push(child);
      await Promise.race([once(child.stdout!, 'data'), once(child, 'exit').then(() => { throw Error('launcher exited before ready'); })]);
      return child;
    };
    const first = await start();
    const exited = once(first, 'exit');
    const second = await start();
    await exited;
    assert.equal(first.exitCode, 0);
    assert.equal(second.exitCode, null);
  } finally {
    for (const child of children) if (child.exitCode === null && child.signalCode === null) {
      const closed = once(child, 'exit'); child.kill('SIGTERM'); await closed;
    }
    rmSync(home, { recursive: true, force: true });
  }
});
