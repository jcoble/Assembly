/**
 * Lazy directory loader for the /next Files panel.
 *
 * Activation lists only the project root. A folder click lists only that
 * folder, and collapsing it releases its descendants. There is no recursive
 * project scan or cross-project explorer cache in this path.
 */
import {
  cancelSourceScanFromTauri,
  isNativeTauriRuntime,
  listSourceDirectoryFromTauri
} from '../../../tauriSource.ts';
import { countInvoke } from '../../devInvokeCounter.svelte.ts';
import {
  applyDirectoryResult,
  beginScan,
  canonicalPath,
  discardDirectory,
  endScan,
  explorer,
  explorerScanGeneration,
  failScan,
  loadedExplorerDirectories,
  loadedExplorerDirectoryDepth,
  isExplorerDirectoryPresent,
  isExplorerPathAtOrBelow,
  markCheckoutDeleted,
  resetExplorer,
  setExplorerError
} from './explorerStore.svelte.ts';

export const SCANNER_UNAVAILABLE_MESSAGE =
  'The file browser is not available here. Open this window in the CommandBar app to browse project files.';

let nextRequestId = 0;
const directoryRequests = new Map<string, string>();
let activeExplorerSignal: AbortSignal | undefined;

function cancelDirectoryRequests(atOrBelow?: string): void {
  for (const [path, scanId] of directoryRequests) {
    if (atOrBelow && !isExplorerPathAtOrBelow(path, atOrBelow)) continue;
    directoryRequests.delete(path);
    void cancelSourceScanFromTauri(scanId);
  }
}

function isCurrentDirectoryRequest(
  directory: string,
  root: string,
  generation: number,
  requestId: string
): boolean {
  return directoryRequests.get(directory) === requestId &&
    explorerScanGeneration() === generation &&
    canonicalPath(explorer.root ?? '') === root &&
    isExplorerDirectoryPresent(directory);
}

function describeError(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function checkoutWasDeleted(message: string): boolean {
  const normalized = message.toLowerCase();
  return (normalized.includes('could not read source root metadata:') ||
    normalized.includes('could not read source directory metadata:')) &&
    (normalized.includes('no such file or directory') || normalized.includes('os error 2'));
}

export async function loadDirectory(
  directory: string,
  depth: number,
  signal?: AbortSignal
): Promise<boolean> {
  const ownerSignal = signal ?? activeExplorerSignal;
  if (ownerSignal?.aborted) return false;
  const root = canonicalPath(explorer.root ?? '');
  const target = canonicalPath(directory);
  if (!root || !target) return false;
  const generation = explorerScanGeneration();
  const previousRequestId = directoryRequests.get(target);
  if (previousRequestId) void cancelSourceScanFromTauri(previousRequestId);
  const requestId = `directory:${++nextRequestId}`;
  directoryRequests.set(target, requestId);
  setExplorerError(null);

  try {
    countInvoke(isNativeTauriRuntime() ? 'list_source_directory' : 'bridge:list-source-directory');
    const entries = await listSourceDirectoryFromTauri(
      root,
      target,
      explorer.includeExcluded,
      requestId,
      ownerSignal
    );
    if (ownerSignal?.aborted) return false;
    if (!isCurrentDirectoryRequest(target, root, generation, requestId)) return false;
    if (!entries) {
      if (target === root) failScan(SCANNER_UNAVAILABLE_MESSAGE);
      else setExplorerError(SCANNER_UNAVAILABLE_MESSAGE);
      return false;
    }
    applyDirectoryResult(target, depth, entries);
    return true;
  } catch (error) {
    if (!isCurrentDirectoryRequest(target, root, generation, requestId)) return false;
    const detail = describeError(error);
    const message = `Could not list this folder: ${detail}`;
    if (target === root && checkoutWasDeleted(detail)) {
      failScan('This session’s checkout/worktree no longer exists.', 'checkout-deleted');
    } else if (target === root) failScan(message);
    else setExplorerError(message);
    return false;
  } finally {
    if (directoryRequests.get(target) === requestId) directoryRequests.delete(target);
  }
}

/** Load the root and each missing directory on the path to a file, in order. */
export async function revealExplorerPath(path: string, signal?: AbortSignal): Promise<string[]> {
  const ownerSignal = signal ?? activeExplorerSignal;
  if (ownerSignal?.aborted) return [];
  const root = canonicalPath(explorer.root ?? '');
  const target = canonicalPath(path);
  if (!root || !target || !isExplorerPathAtOrBelow(target, root) || target === root) return [];
  const generation = explorerScanGeneration();

  const relative = target.slice(root.length).replace(/^\/+/, '');
  const components = relative.split('/').filter(Boolean).slice(0, -1);
  const directories: string[] = [];
  let directory = root;
  for (const component of components) {
    directory = `${directory}/${component}`;
    directories.push(directory);
  }

  const loaded: string[] = [];
  for (const [index, candidate] of [root, ...directories].entries()) {
    if (ownerSignal?.aborted) break;
    if (generation !== explorerScanGeneration() || canonicalPath(explorer.root ?? '') !== root) break;
    const depth = index;
    if (loadedExplorerDirectoryDepth(candidate) === null && !(await loadDirectory(candidate, depth, ownerSignal))) {
      break;
    }
    if (candidate !== root) loaded.push(candidate);
  }
  return loaded;
}

export function unloadDirectory(directory: string): void {
  const target = canonicalPath(directory);
  cancelDirectoryRequests(target);
  discardDirectory(target);
}

export async function scanRoot(root: string, signal?: AbortSignal): Promise<void> {
  const ownerSignal = signal ?? activeExplorerSignal;
  if (ownerSignal?.aborted) return;
  const target = canonicalPath(root);
  if (!target) return;
  cancelDirectoryRequests();
  resetExplorer();
  beginScan(target);
  const generation = explorerScanGeneration();
  try {
    await loadDirectory(target, 0, ownerSignal);
  } finally {
    if (explorer.root === target && explorerScanGeneration() === generation) endScan();
  }
}

export function activate(
  root: string | null,
  checkoutDeleted = false,
  signal?: AbortSignal
): void {
  const target = canonicalPath(root ?? '');
  activeExplorerSignal = signal;
  if (!target || signal?.aborted) {
    cancelDirectoryRequests();
    if (checkoutDeleted) markCheckoutDeleted();
    else resetExplorer();
    return;
  }
  if (explorer.activated && explorer.root === target && explorer.error === null) return;
  void scanRoot(target, signal);
}

export function refresh(): void {
  if (activeExplorerSignal?.aborted) return;
  const directories = loadedExplorerDirectories();
  if (directories.length === 0 && explorer.root) {
    void loadDirectory(explorer.root, 0, activeExplorerSignal);
    return;
  }
  for (const directory of directories) void loadDirectory(directory.path, directory.depth, activeExplorerSignal);
}

export function refreshChangedPaths(paths: readonly string[]): void {
  const root = canonicalPath(explorer.root ?? '');
  if (!root) return;
  const directories = new Map(
    loadedExplorerDirectories().map(({ path, depth }) => [canonicalPath(path), depth])
  );
  const refreshTargets = new Map<string, number>();
  for (const path of paths) {
    let target = canonicalPath(path);
    while (isExplorerPathAtOrBelow(target, root)) {
      const depth = directories.get(target);
      if (depth !== undefined) {
        refreshTargets.set(target, depth);
        break;
      }
      if (target === root) break;
      const cut = target.lastIndexOf('/');
      target = cut <= 0 ? root : target.slice(0, cut);
    }
  }
  for (const [directory, depth] of refreshTargets) {
    void loadDirectory(directory, depth, activeExplorerSignal);
  }
}

export function refreshChangedPath(path: string): void {
  refreshChangedPaths([path]);
}

export function stopScan(ownerSignal?: AbortSignal): void {
  if (ownerSignal && activeExplorerSignal !== ownerSignal) return;
  cancelDirectoryRequests();
  activeExplorerSignal = undefined;
  endScan();
}
