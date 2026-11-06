import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, writeFileSync, rmSync, realpathSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { test } from 'node:test';
import { ensureBuild } from './dev-build-cache.ts';

function fixture() {
  const root = mkdtempSync(join(realpathSync(tmpdir()), 'dev-launch-check-'));
  const write = (path: string, text: string) => { mkdirSync(dirname(path), { recursive: true }); writeFileSync(path, text); };
  execFileSync('git', ['init', '-q'], { cwd: root });
  write(join(root, 'scripts/adapters/claude-launcher.ts'), 'launcher');
  write(join(root, 'dotnet/Assembly.Remote/Assembly.Remote.csproj'), '<Project/>');
  write(join(root, 'desktop/src/main.rs'), 'native');
  execFileSync('git', ['add', '.'], { cwd: root });
  return { root, write, close: () => rmSync(root, { recursive: true, force: true }) };
}
test('second unchanged launch does no backend or desktop build; changed inputs rebuild only their piece', async () => {
  const f = fixture(); let backend = 0, desktop = 0;
  try {
    const backendOut = join(f.root, 'desktop/target/dev-backend/Assembly.Desktop.dll'), desktopOut = join(f.root, 'desktop/target/debug/assembly-desktop');
    const check = async () => {
      await ensureBuild(f.root, 'backend', [backendOut], async () => { backend++; f.write(backendOut, 'backend'); });
      await ensureBuild(f.root, 'desktop', [desktopOut], async () => { desktop++; f.write(desktopOut, 'desktop'); });
    };
    await check(); f.write(join(f.root, 'desktop/target/dev-backend/lane.json'), '{}'); await check(); assert.equal(backend, 1); assert.equal(desktop, 1);
    f.write(join(f.root, 'desktop/src/main.rs'), 'native changed'); await check();
    assert.equal(backend, 1); assert.equal(desktop, 2);
    f.write(join(f.root, 'dotnet/Assembly.Remote/Assembly.Remote.csproj'), '<Project>changed</Project>'); await check();
    assert.equal(backend, 2); assert.equal(desktop, 2);
  } finally { f.close(); }
});
