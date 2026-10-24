import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

import {
  filesPanelActions,
  type FilesPanelActionId
} from '../src/lib/shell/files/components/filesPanelActions.ts';
import {
  fileHistoryTarget,
  type FileTreeNode
} from '../src/lib/shell/files/components/fileTreeModel.ts';
import { fileIconForName } from '../src/lib/shell/components/explorer/fileIcons.ts';

const filesPanel = readFileSync(
  new URL('../src/lib/shell/files/components/FilesPanel.svelte', import.meta.url),
  'utf8'
);
const fileHistoryPane = readFileSync(
  new URL('../src/lib/shell/files/components/FileHistoryPane.svelte', import.meta.url),
  'utf8'
);
const gitHistoryView = readFileSync(
  new URL('../src/lib/shell/git/components/GitHistoryView.svelte', import.meta.url),
  'utf8'
);
const shellRoute = readFileSync(new URL('../src/routes/+page.svelte', import.meta.url), 'utf8');

function node(path: string, isDirectory: boolean): FileTreeNode {
  return {
    path,
    name: path.slice(path.lastIndexOf('/') + 1),
    depth: 0,
    isDirectory,
    childCount: isDirectory ? 2 : 0,
    ignored: false
  };
}

function ids(target: FileTreeNode): FilesPanelActionId[] {
  return filesPanelActions(target).map((action) => action.id);
}

// A folder can hold something new, so it offers the two ways of putting
// something in it before the four every row has.
assert.deepEqual(ids(node('/repo/src', true)), [
  'new-file',
  'new-folder',
  'rename',
  'delete',
  'reveal-in-finder',
  'copy-path'
]);

// A file holds nothing, so the two create actions are absent rather than
// present-and-off: an item that can never apply teaches nothing.
assert.deepEqual(ids(node('/repo/src/App.ts', false)), [
  'rename',
  'delete',
  'reveal-in-finder',
  'copy-path'
]);

// The words the menu draws are the words a person reads, so they are part of
// the contract and not free for a caller to restyle.
assert.deepEqual(
  filesPanelActions(node('/repo/src', true)).map((action) => action.label),
  ['New file', 'New folder', 'Rename', 'Delete', 'Reveal in Finder', 'Copy path']
);

// Deleting moves the path to the Trash, which is recoverable; nothing else in
// the list changes what is on disk, so Delete is the only destructive one.
assert.deepEqual(
  filesPanelActions(node('/repo/src/App.ts', false))
    .filter((action) => action.destructive)
    .map((action) => action.id),
  ['delete']
);

assert.match(filesPanel, /<FileIcon fileName=\{node\.name\} size=\{15\} \/>/);
assert.match(filesPanel, /<FolderOpen size=\{15\}/);
assert.match(filesPanel, /<Folder size=\{15\}/);
assert.match(filesPanel, /<FileHistoryPane/);
assert.match(filesPanel, /id === "open-timeline"/);
assert.match(filesPanel, /fileHistoryOpen = true/);
assert.match(
  filesPanel,
  /open-timeline[\s\S]{0,240}gitService\.showFileHistory/,
  'the Files timeline primes Source Control without opening its panel'
);
assert.match(filesPanel, /id === "git-file-history"/);
assert.match(filesPanel, /await openFileTimeline/);
assert.match(fileHistoryPane, /const stop = new AbortController\(\)/);
assert.match(fileHistoryPane, /service\.releaseHistorySurface\(\)/);
assert.match(fileHistoryPane, /await service\.loadMoreHistory\(\)/);
assert.match(fileHistoryPane, /\{#if open\}[\s\S]*<Collapsible\.Content>/);
assert.match(shellRoute, /historyPath=\{gitPanel\.historyPath\}/);
assert.match(gitHistoryView, /showFileHistory\(targetRoot, targetPath\)/);
assert.equal(
  (filesPanel.match(/<ContextMenu\.Root/g) ?? []).length,
  1,
  'the virtualized tree owns one context menu, not retained state for every row'
);
assert.match(filesPanel, /listRepositoryCheckoutsFromTauri\(roots\)/);
assert.match(filesPanel, /<Select\.Item value=\{option\.path\} label=\{option\.label\} \/>/);
assert.doesNotMatch(filesPanel, /class="files-context-menu"/);
assert.deepEqual(fileIconForName('PreviewIntegrationConfigurationContractTests.cs'), {
  kind: 'sharp',
  tone: 'csharp',
  label: 'C#'
});
assert.deepEqual(fileIconForName('FilesPanel.svelte'), {
  kind: 'flame',
  tone: 'svelte',
  label: 'Svelte component'
});

// File History shows the selected file even when the tree has not loaded it,
// as when the file was opened in the editor from a chat link (TSK-1359).
assert.deepEqual(fileHistoryTarget('/repo', '/repo/docs/guide/README.md', []), {
  path: '/repo/docs/guide/README.md',
  name: 'README.md',
  relativePath: 'docs/guide/README.md'
});
assert.deepEqual(fileHistoryTarget('/repo', '/repo/src/App.ts', [node('/repo/src/App.ts', false)]), {
  path: '/repo/src/App.ts',
  name: 'App.ts',
  relativePath: 'src/App.ts'
});
// A folder has no file history, a path outside the root is not this tree's,
// and nothing selected still asks for a file.
assert.equal(fileHistoryTarget('/repo', '/repo/src', [node('/repo/src', true)]), null);
assert.equal(fileHistoryTarget('/repo', '/other/README.md', []), null);
assert.equal(fileHistoryTarget('/repo', '/repository/README.md', []), null);
assert.equal(fileHistoryTarget('', '/repo/README.md', []), null);
assert.equal(fileHistoryTarget('/repo', null, []), null);

console.log('filesPanelActions tests passed');
