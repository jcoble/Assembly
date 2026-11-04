import { promises as fs } from 'node:fs';
import { createHash, randomUUID } from 'node:crypto';
import { join, dirname } from 'node:path';
import { command } from './dev-remote-process.ts';
import { backendInput } from './dev-remote-identity.ts';

export async function buildInputIdentity(root: string, kind: 'backend' | 'desktop') {
  const selected = (file: string) => kind === 'backend' ? backendInput(file) || file.startsWith('dotnet/Assembly.Desktop/') : file.startsWith('desktop/') && !file.startsWith('desktop/target/');
  const [indexed, dirty, untracked] = await Promise.all([
    command('git', ['ls-files', '--stage', '-z'], { cwd: root, raw: true }),
    command('git', ['diff', '--name-only', '-z'], { cwd: root, raw: true }),
    command('git', ['ls-files', '--others', '--exclude-standard', '-z'], { cwd: root, raw: true }),
  ]);
  const entries = new Map<string, string>();
  for (const item of indexed.split('\0').filter(Boolean)) {
    const tab = item.indexOf('\t'), file = item.slice(tab + 1); if (!selected(file)) continue;
    const [mode, blob, stage] = item.slice(0, tab).split(' ');
    if (stage !== '0') throw Error('unmerged build input: ' + file);
    entries.set(file, mode + ' ' + blob + '\t' + file);
  }
  for (const file of new Set([...dirty.split('\0'), ...untracked.split('\0')].filter(file => file && selected(file)))) {
    try {
      const info = await fs.lstat(join(root, file));
      if (!info.isFile()) throw Error('build input must be a regular file: ' + file);
      const blob = await command('git', ['hash-object', '--', file], { cwd: root });
      entries.set(file, (info.mode & 0o111 ? '100755' : '100644') + ' ' + blob + '\t' + file);
    } catch (error) { if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error; entries.delete(file); }
  }
  const hash = createHash('sha256').update(JSON.stringify([kind, process.platform, process.arch, 'debug=0;incremental=0']));
  for (const entry of [...entries.values()].sort()) hash.update(entry + '\0');
  return hash.digest('hex');
}
export async function ensureBuild(root: string, kind: 'backend' | 'desktop', outputs: string[], build: () => Promise<void>) {
  const key = await buildInputIdentity(root, kind), file = join(root, 'desktop/target', `dev-${kind}-build.json`);
  const snapshot = async () => {
    const closure = kind === 'backend' ? (await fs.readdir(dirname(outputs[0]), { recursive: true })).filter(file => file !== 'lane.json').map(file => join(dirname(outputs[0]), file)) : [];
    const files = await Promise.all([...new Set([...outputs, ...closure])].sort().map(async path => { const info = await fs.stat(path); return info.isFile() ? { path, bytes: info.size, modified: info.mtimeMs } : null; }));
    return files.filter(Boolean);
  };
  try { const prior = JSON.parse(await fs.readFile(file, 'utf8')); if (prior.key === key && JSON.stringify(prior.outputs) === JSON.stringify(await snapshot())) return false; } catch { }
  await fs.rm(file, { force: true });
  await build();
  const state = { key, outputs: await snapshot() };
  await fs.mkdir(dirname(file), { recursive: true });
  const temporary = file + '.' + randomUUID();
  await fs.writeFile(temporary, JSON.stringify(state), { flag: 'wx' }); await fs.rename(temporary, file);
  return true;
}
