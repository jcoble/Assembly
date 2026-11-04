import { mkdtempSync, mkdirSync, writeFileSync, rmSync, realpathSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';

export function fixture(t: any) {
  const home = realpathSync(mkdtempSync(join(tmpdir(), 'workflow-test-')));
  t.after(() => rmSync(home, { recursive: true, force: true }));
  const cwd = join(home, 'Assembly');
  const bin = join(home, 'bin');
  mkdirSync(cwd); mkdirSync(bin);
  writeFileSync(join(bin, 'gh'), '#!/bin/sh\nexit 1\n', { mode: 0o755 });
  const env = { ...process.env, HOME: home, PATH: bin + ':' + process.env.PATH };
  function run(args: string[], input = '', extra = {}) {
    return spawnSync(process.execPath, [new URL('./cli.ts', import.meta.url).pathname, ...args], { cwd, input, encoding: 'utf8', env: { ...env, ...extra } });
  }
  function hook(event: any, provider = 'codex') {
    return run(['hook', provider], JSON.stringify({ cwd, session_id: 'parent', ...event }));
  }
  const binding = () => JSON.parse(run(['where', 'codex', 'parent']).stdout);
  return { home, cwd, bin, env, run, hook, binding };
}
