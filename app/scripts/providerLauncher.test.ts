import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { promisify } from 'node:util';
import test from 'node:test';
import { launcher } from './prepareReleaseAdapters.ts';

const exec = promisify(execFile);
for (const [cli, environment] of [['codex', 'CODEX_PATH'], ['claude', 'CLAUDE_CODE_EXECUTABLE']] as const) {
  test(`${cli} follows current PATH despite an executable cached CLI`, async () => {
    const root = await mkdtemp(join(tmpdir(), 'assembly-cli-resolution-'));
    try {
      const home = join(root, 'home');
      const cache = join(home, '.mac-command-bar/cli-paths');
      const old = join(root, 'old');
      const current = join(root, 'current');
      const next = join(root, 'next');
      await Promise.all([cache, old, current, next].map(path => mkdir(path, { recursive: true })));
      for (const [directory, version] of [[old, 'old'], [current, 'current'], [next, 'next']]) {
        await writeFile(join(directory, cli), `#!/bin/sh\nprintf '%s\\n' '${version}'\n`, { mode: 0o755 });
      }
      const cachedValue = `${join(old, cli)}\n`;
      await writeFile(join(cache, cli), cachedValue);
      const wrapper = join(root, 'adapter');
      await writeFile(wrapper, launcher({ cli, environment, displayName: cli, runtime: 'runtime' }), { mode: 0o755 });
      await writeFile(join(root, 'runtime'), `#!/bin/sh\nexec "$${environment}" "$@"\n`, { mode: 0o755 });
      for (const [directory, version] of [[current, 'current'], [next, 'next']]) {
        const result = await exec(wrapper, [], { env: { ...process.env, HOME: home, PATH: `${directory}:/usr/bin:/bin` } });
        assert.equal(result.stdout.trim(), version);
        assert.equal(await readFile(join(cache, cli), 'utf8'), cachedValue, 'launch must not rewrite a path cache');
      }
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });
}
