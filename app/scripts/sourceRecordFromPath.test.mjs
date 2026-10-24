import assert from 'node:assert/strict';
import {
  fileNameFromPath,
  sourceLanguageForOpenPath,
  sourceRecordFromPath
} from '../src/lib/shell/files/editor/sourceRecordFromPath.ts';

const root = '/Users/dev/project';

// a file inside the project keeps a project-relative path
{
  const record = sourceRecordFromPath(root, `${root}/src/lib/app.ts`);
  assert.deepEqual(record, {
    path: '/Users/dev/project/src/lib/app.ts',
    relativePath: 'src/lib/app.ts',
    fileName: 'app.ts',
    language: 'typescript',
    byteCount: 0
  });
}

// a trailing slash on the project root does not leak into the relative path
{
  const record = sourceRecordFromPath(`${root}/`, `${root}/src/app.svelte`);
  assert.equal(record.relativePath, 'src/app.svelte');
  assert.equal(record.language, 'svelte');
}

// a file OUTSIDE the project falls back to just its name
{
  const record = sourceRecordFromPath(root, '/usr/local/share/other.rs');
  assert.equal(record.relativePath, 'other.rs');
  assert.equal(record.fileName, 'other.rs');
  assert.equal(record.language, 'rust');
}

// no project root at all is tolerated (a file opened before the shell knew one)
{
  const record = sourceRecordFromPath(null, '/tmp/scratch/notes.md');
  assert.equal(record.relativePath, 'notes.md');
  assert.equal(record.language, 'markdown');
  assert.equal(sourceRecordFromPath(undefined, '/tmp/a.json').language, 'json');
  assert.equal(sourceRecordFromPath('   ', '/tmp/a.json').relativePath, 'a.json');
}

// the path that IS the project root degrades to its own folder name
{
  const record = sourceRecordFromPath(root, root);
  assert.equal(record.relativePath, 'project');
  assert.equal(record.fileName, 'project');
}

// an already-scanned record wins: its real size and relative path survive
{
  const scanned = {
    path: `${root}/src/app.ts`,
    relativePath: 'src/app.ts',
    fileName: 'app.ts',
    language: 'typescript',
    byteCount: 4096
  };
  const record = sourceRecordFromPath(root, `${root}/src/app.ts/`, [scanned]);
  assert.equal(record, scanned, 'the scanned record itself is returned');
  assert.equal(record.byteCount, 4096);
}

// a scan that does not contain the file leaves the synthesized record alone
{
  const scanned = {
    path: `${root}/src/other.ts`,
    relativePath: 'src/other.ts',
    fileName: 'other.ts',
    language: 'typescript',
    byteCount: 10
  };
  const record = sourceRecordFromPath(root, `${root}/src/app.ts`, [scanned]);
  assert.equal(record.byteCount, 0);
  assert.equal(record.relativePath, 'src/app.ts');
}

// language mapping: the cases the shared helper gets wrong are the point
{
  assert.equal(sourceLanguageForOpenPath('/a/b.mjs'), 'javascript');
  assert.equal(sourceLanguageForOpenPath('/a/b.css'), 'css');
  assert.equal(sourceLanguageForOpenPath('/a/b.html'), 'html');
  assert.equal(sourceLanguageForOpenPath('/a/B.TSX'), 'tsx');
  assert.equal(sourceLanguageForOpenPath('/a/b.zsh'), 'shell');
  assert.equal(sourceLanguageForOpenPath('/a/b.lock'), 'plain');
  assert.equal(sourceLanguageForOpenPath('/a/Makefile'), 'plain');
}

// file names come off the last segment, with a sane answer for junk input
{
  assert.equal(fileNameFromPath('/a/b/c.ts'), 'c.ts');
  assert.equal(fileNameFromPath('c.ts'), 'c.ts');
  assert.equal(fileNameFromPath('///'), 'file');
}

// a file on a remote machine keeps its machine in the path the editor reads and
// saves through, and is named relative to that machine's project root (725896a73)
{
  const remoteRoot = 'assembly-remote://workbox-test/home/me/repo/';
  const record = sourceRecordFromPath(remoteRoot, 'assembly-remote://workbox-test/home/me/repo/src/main.rs');
  assert.equal(record.path, 'assembly-remote://workbox-test/home/me/repo/src/main.rs');
  assert.equal(record.relativePath, 'src/main.rs');
  assert.equal(record.language, 'rust');
}

console.log('sourceRecordFromPath tests passed');
