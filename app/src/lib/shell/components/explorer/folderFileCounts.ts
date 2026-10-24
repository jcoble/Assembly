/**
 * folderFileCounts.ts — how many files each folder actually holds.
 *
 * The explorer used to badge a folder with `node.children.length`, which is the
 * number of things directly inside it. That reads as "files in here" and is wrong
 * the moment a folder holds other folders: `EdiEngine.Cli` showed 11 while the
 * `Commands` folder inside it held 23 on its own.
 *
 * This counts every file underneath a folder, however deep.
 *
 * Cost: ONE pass over the file list, touching each file's ancestor folders — not a
 * walk of the tree, and not anything per rendered row. The explorer builds it once
 * per view, next to the tree itself, and every row then reads its number from the
 * map for free.
 */
import { folderIdsForSourceRecord, type SourceRecord } from '../../../sourceData.ts';

/**
 * Files under each folder, keyed by the same `folder:<relative path>` id that
 * `buildSourceTree` gives its folder nodes. Folders with no files are absent;
 * callers read a missing folder as zero.
 */
export function folderFileCounts(records: SourceRecord[]): Map<string, number> {
  const counts = new Map<string, number>();

  for (const record of records) {
    for (const folderId of folderIdsForSourceRecord(record)) {
      counts.set(folderId, (counts.get(folderId) ?? 0) + 1);
    }
  }

  return counts;
}

/** The badge text for a folder — plain digits, and thousands grouped so a big
 * folder does not read as one long number. */
export function folderFileCountLabel(counts: Map<string, number>, folderId: string): string {
  return (counts.get(folderId) ?? 0).toLocaleString();
}

/** What the badge means, spelled out for the row's tooltip. */
export function folderFileCountTitle(counts: Map<string, number>, folderId: string): string {
  const count = counts.get(folderId) ?? 0;
  if (count === 0) return 'No files in this folder';
  if (count === 1) return '1 file in this folder, including everything in its subfolders';
  return `${count.toLocaleString()} files in this folder, including everything in its subfolders`;
}
