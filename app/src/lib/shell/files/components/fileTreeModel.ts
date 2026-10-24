/** Pure sorting, filtering, expansion and viewport maths for the Files panel. */
import {
  virtualizeSourceTreeRows,
  type SourceTreeRow
} from '../../../sourceData.ts';
import type { ExplorerTreeNode } from '../explorer/explorerStore.svelte.ts';

export type FileTreeNode = ExplorerTreeNode;

export const FILE_TREE_ROW_HEIGHT = 28;
export const FILE_TREE_OVERSCAN_ROWS = 8;
export const FILE_TREE_FALLBACK_VIEWPORT_HEIGHT = 420;

function compareNodes(left: FileTreeNode, right: FileTreeNode): number {
  if (left.isDirectory !== right.isDirectory) return left.isDirectory ? -1 : 1;
  return left.name.localeCompare(right.name, undefined, { numeric: true, sensitivity: 'base' });
}

export function filterFileTreeNodes(
  nodes: readonly FileTreeNode[],
  query: string
): FileTreeNode[] {
  const normalized = query.trim().toLowerCase();
  if (!normalized) return [...nodes];
  const matches = nodes.filter((node) => node.name.toLowerCase().includes(normalized));
  return nodes.filter(
    (node) =>
      matches.some((match) => match.path === node.path) ||
      (node.isDirectory && matches.some((match) => match.path.startsWith(`${node.path}/`))) ||
      matches.some((match) => match.isDirectory && node.path.startsWith(`${match.path}/`))
  );
}

export function visibleFileTreeNodes<T extends FileTreeNode>(
  nodes: readonly T[],
  expanded: ReadonlySet<string>,
  compare: (left: T, right: T) => number = compareNodes
): T[] {
  const byParent = new Map<string, T[]>();
  const roots: T[] = [];
  for (const node of nodes) {
    if (node.depth === 0) {
      roots.push(node);
      continue;
    }
    const siblings = byParent.get(node.parentPath);
    if (siblings) siblings.push(node);
    else byParent.set(node.parentPath, [node]);
  }

  const visible: T[] = [];
  function emit(list: T[]): void {
    for (const node of [...list].sort(compare)) {
      visible.push(node);
      if (!node.isDirectory || !expanded.has(node.path)) continue;
      const children = byParent.get(node.path);
      if (children) emit(children);
    }
  }
  emit(roots);
  return visible;
}

export function allDirectoryPaths(nodes: readonly FileTreeNode[]): Set<string> {
  return new Set(nodes.filter((node) => node.isDirectory).map((node) => node.path));
}

export function toggleDirectory(
  expanded: ReadonlySet<string>,
  node: FileTreeNode
): Set<string> {
  const next = new Set(expanded);
  if (!node.isDirectory) return next;
  if (next.has(node.path)) next.delete(node.path);
  else next.add(node.path);
  return next;
}

export interface FileTreeWindow<T extends FileTreeNode = FileTreeNode> {
  nodes: T[];
  topSpacerHeight: number;
  bottomSpacerHeight: number;
  totalHeight: number;
}

const WINDOW_CARRIER_ROW: SourceTreeRow = {
  node: { id: '', name: '', relativePath: '', file: null, children: [] },
  level: 0
};

export function windowFileTreeNodes<T extends FileTreeNode>(
  nodes: readonly T[],
  scrollTop: number,
  viewportHeight: number,
  rowHeight: number = FILE_TREE_ROW_HEIGHT,
  overscanRows: number = FILE_TREE_OVERSCAN_ROWS
): FileTreeWindow<T> {
  const virtual = virtualizeSourceTreeRows(
    new Array<SourceTreeRow>(nodes.length).fill(WINDOW_CARRIER_ROW),
    scrollTop,
    viewportHeight > 0 ? viewportHeight : FILE_TREE_FALLBACK_VIEWPORT_HEIGHT,
    rowHeight,
    overscanRows
  );

  return {
    nodes: nodes.slice(virtual.startIndex, virtual.endIndex),
    topSpacerHeight: virtual.topSpacerHeight,
    bottomSpacerHeight: virtual.bottomSpacerHeight,
    totalHeight: virtual.totalHeight
  };
}

/**
 * The file File History shows for the selected path. The tree may not have
 * loaded the path yet (a file opened in the editor from a chat link), so only
 * a loaded folder rules it out. `null` for no selection or a path outside `root`.
 */
export function fileHistoryTarget(
  root: string,
  selectedPath: string | null,
  nodes: readonly FileTreeNode[]
): { path: string; name: string; relativePath: string } | null {
  if (!root || !selectedPath?.startsWith(`${root}/`)) return null;
  if (nodes.some((node) => node.path === selectedPath && node.isDirectory)) return null;
  const relativePath = selectedPath.slice(root.length + 1);
  return { path: selectedPath, name: relativePath.slice(relativePath.lastIndexOf('/') + 1), relativePath };
}
