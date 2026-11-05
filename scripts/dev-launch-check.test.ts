import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtempSync, mkdirSync, writeFileSync, rmSync, readFileSync, realpathSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { test } from 'node:test';
import { ensureBuild } from './dev-build-cache.ts';
import { fileURLToPath } from 'node:url';

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
test('local and remote safe-backup manifests select the same cohort despite different source paths; hot/changed backups are refused', async () => {
  const f = fixture();
  try {
    const bytes = 'artificial test backup bytes', sha = createHash('sha256').update(bytes).digest('hex');
    const manifests = ['local', 'remote'].map(host => {
      const backup = join(f.root, host, 'safe.db'); f.write(backup, bytes);
      const manifest = join(f.root, host, 'manifest.json');
      f.write(manifest, JSON.stringify({ version: 1, kind: 'real-recorded', originId: 'same-safe-source', sources: [{ format: 'database', path: backup, sha256: sha }] }));
      return manifest;
    });
    const cohort = await seedCohort(manifests[0]); assert.equal(await seedCohort(manifests[1]), cohort);
    const home = join(f.root, 'profile'); f.write(join(home, 'assembly.db'), bytes);
    await recordSeed(home, cohort); assert.equal(await seedCurrent(home, cohort), true);
    f.write(join(f.root, 'remote/safe.db-wal'), 'active'); await assert.rejects(seedCohort(manifests[1]), /closed/);
    rmSync(join(f.root, 'remote/safe.db-wal')); f.write(join(f.root, 'remote/safe.db'), 'changed');
    await assert.rejects(seedCohort(manifests[1]), /SHA differs/);
  } finally { f.close(); }
});
test('every lane can seed from the tracked recordings: the manifest pins exactly what is checked in', async () => {
  const root = fileURLToPath(new URL('../', import.meta.url));
  assert.match(await seedCohort(trackedManifest(root)), /^[a-f0-9]{64}$/);
  const manifest = JSON.parse(readFileSync(trackedManifest(root), 'utf8'));
  assert.ok(manifest.sources.every((source: { path: string }) => source.path.startsWith('../')), 'paths stay relative so every host reads its own checkout');
});
