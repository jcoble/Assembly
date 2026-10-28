import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, copyFileSync, readFileSync, realpathSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { test } from 'node:test';

test('app:dev prepares the backend and launches GPUI without Tauri or Vite', () => {
  const root = realpathSync(mkdtempSync(join(tmpdir(), 'assembly-dev-')));
  try {
    for (const folder of ['scripts', 'desktop', 'dotnet/scripts', 'bin']) {
      mkdirSync(join(root, folder), { recursive: true });
    }
    copyFileSync(new URL('./app-dev.ts', import.meta.url), join(root, 'scripts/app-dev.ts'));
    writeFileSync(join(root, 'scripts/lane-env.sh'), `
DATA="$PWD/test-data"
DESKTOP_PORT=18807
TOKEN_FILE="$PWD/test-token"
BOX_PORT=9807
BOX_TOKEN_FILE=test-remote-token
`);
    writeFileSync(join(root, 'dotnet/scripts/agents-file.sh'), 'printf "[]" > "$2"\n');
    for (const tool of ['dotnet', 'cargo']) {
      writeFileSync(join(root, 'bin', tool), `#!/bin/sh
printf '${tool} %s\\n' "$*" >> "$COMMAND_LOG"
${tool === 'cargo' ? 'test -f "$ASSEMBLY_BACKEND_DIR/lane.json" || exit 9' : ''}
exit ${tool === 'dotnet' ? '${DOTNET_EXIT:-0}' : '0'}
`, { mode: 0o755 });
    }
    const log = join(root, 'commands');
    const result = spawnSync(process.execPath, [join(root, 'scripts/app-dev.ts')], {
      cwd: root,
      encoding: 'utf8',
      env: { ...process.env, PATH: `${join(root, 'bin')}:${process.env.PATH}`, COMMAND_LOG: log },
    });
    assert.equal(result.status, 0, result.stderr);
    const commands = readFileSync(log, 'utf8');
    assert.match(commands, /dotnet publish/);
    assert.match(commands, /cargo run --locked/);
    assert.ok(commands.indexOf('dotnet publish') < commands.indexOf('cargo run'));
    const config = JSON.parse(readFileSync(join(root, 'desktop/target/dev-backend/lane.json'), 'utf8'));
    assert.equal(config.port, 18807);
    assert.equal(config.home, join(root, 'test-data/mac'));
    assert.equal(config.remotePort, 9807);
    assert.equal(readFileSync(join(root, 'test-data/mac/agents.json'), 'utf8'), '[]');
    writeFileSync(log, '');
    const failed = spawnSync(process.execPath, [join(root, 'scripts/app-dev.ts')], {
      cwd: root,
      encoding: 'utf8',
      env: {
        ...process.env, PATH: `${join(root, 'bin')}:${process.env.PATH}`,
        COMMAND_LOG: log, DOTNET_EXIT: '7',
      },
    });
    assert.equal(failed.status, 7, failed.stderr);
    assert.doesNotMatch(readFileSync(log, 'utf8'), /cargo/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
