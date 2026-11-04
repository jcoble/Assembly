import { promises as fs, createReadStream } from 'node:fs';
import { createHash, randomUUID } from 'node:crypto';
import { dirname, isAbsolute, join, resolve } from 'node:path';
import { safePath } from './seed-dev.ts';

// The checkout's recorded sessions until a safe everyday backup is designated. Never a live database.
export const trackedManifest = (root: string) => join(root, 'scripts/dev-seed-manifest.json');
// Pinned recordings or a closed backup. No fallback to a live database or synthetic fixture.
export async function seedCohort(manifestPath: string) {
  manifestPath = safePath(manifestPath);
  const manifest = JSON.parse(await fs.readFile(manifestPath, 'utf8'));
  if (manifest.version !== 1 || manifest.kind !== 'real-recorded' || !manifest.originId || !manifest.sources?.length)
    throw Error('lane seed requires a real-recorded manifest');
  for (const source of manifest.sources) {
    if (!['acp', 'database'].includes(source.format) || !/^[a-f0-9]{64}$/.test(source.sha256 ?? '')) throw Error('lane seed accepts only SHA-pinned recordings or database backups');
    const file = safePath(isAbsolute(source.path) ? source.path : resolve(dirname(manifestPath), source.path));
    const info = await fs.stat(file);
    if (!info.isFile() || info.nlink !== 1 || info.size > 64 * 1024 * 1024) throw Error('seed backup is not a bounded regular safe file');
    for (const suffix of ['-wal', '-shm']) {
      try { await fs.access(file + suffix); throw Error('seed backup must be closed, without WAL/SHM sidecars'); }
      catch (error) { if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error; }
    }
    const hash = createHash('sha256'); for await (const bytes of createReadStream(file)) hash.update(bytes);
    if (hash.digest('hex') !== source.sha256) throw Error('safe seed backup SHA differs from manifest');
  }
  const sources = manifest.sources.map(({ path: ignored, ...source }: Record<string, unknown>) => source);
  return createHash('sha256').update(JSON.stringify({ originId: manifest.originId, kind: manifest.kind, sources })).digest('hex');
}
export async function seedCurrent(home: string, cohort: string) {
  try { return JSON.parse(await fs.readFile(join(home, 'dev-seed-cohort.json'), 'utf8')).cohort === cohort && (await fs.stat(join(home, 'assembly.db'))).isFile(); }
  catch { return false; }
}
export async function recordSeed(home: string, cohort: string) {
  const file = join(home, 'dev-seed-cohort.json'), temporary = file + '.' + randomUUID();
  await fs.writeFile(temporary, JSON.stringify({ cohort }), { flag: 'wx', mode: 0o600 });
  await fs.rename(temporary, file);
}
