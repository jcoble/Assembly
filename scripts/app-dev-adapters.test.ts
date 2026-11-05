import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, copyFileSync, readFileSync, realpathSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { test } from 'node:test';

for (const override of [false, true]) {
  test(override ? 'app:dev preserves an explicit adapter directory' : 'plain app:dev uses its lane private adapter directory', () => {
    const root = realpathSync(mkdtempSync(join(tmpdir(), 'assembly-adapter-path-')));
    try {
      for (const folder of ['scripts', 'desktop', 'dotnet/scripts', 'bin']) mkdirSync(join(root, folder), { recursive: true });
      copyFileSync(new URL('./app-dev.ts', import.meta.url), join(root, 'scripts/app-dev.ts'));
      copyFileSync(new URL('./seed-dev.ts', import.meta.url), join(root, 'scripts/seed-dev.ts'));
      copyFileSync(new URL('./app-dev-remote.ts', import.meta.url), join(root, 'scripts/app-dev-remote.ts'));
      writeFileSync(join(root, 'scripts/lane-env.sh'), 'DATA="$PWD/private lane"\nDESKTOP_PORT=18887\nTOKEN_FILE=test\nBOX_PORT=9887\nBOX_TOKEN_FILE=test\n');
      writeFileSync(join(root, 'dotnet/scripts/agents-file.sh'), 'printf "%s" "$1" > "$ADAPTER_LOG"\nprintf "[]" > "$2"\n');
      for (const tool of ['dotnet', 'cargo']) writeFileSync(join(root, 'bin', tool), '#!/bin/sh\nexit 0\n', { mode: 0o755 });
      const env: NodeJS.ProcessEnv = { ...process.env, PATH: join(root, 'bin') + ':' + process.env.PATH, ADAPTER_LOG: join(root, 'adapter-path') };
      delete env.ADAPTERS;
      const expected = override ? join(root, 'explicit adapters') : join(root, 'private lane/adapters');
      if (override) env.ADAPTERS = expected;
      const result = spawnSync(process.execPath, [join(root, 'scripts/app-dev.ts')], { cwd: root, env, encoding: 'utf8' });
      assert.equal(result.status, 0, result.stderr);
      assert.equal(readFileSync(join(root, 'adapter-path'), 'utf8'), expected);
    } finally { rmSync(root, { recursive: true, force: true }); }
  });
}
