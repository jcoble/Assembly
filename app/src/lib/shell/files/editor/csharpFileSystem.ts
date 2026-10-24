import {
  FileSystemProviderCapabilities,
  FileType,
  registerCustomProvider
} from '@codingame/monaco-vscode-files-service-override';
import { Event } from '@codingame/monaco-vscode-api/vscode/vs/base/common/event';
import type { URI } from '@codingame/monaco-vscode-api/vscode/vs/base/common/uri';
import type {
  IFileDeleteOptions,
  IFileOverwriteOptions,
  IFileSystemProvider,
  IFileWriteOptions,
  IStat,
  IWatchOptions
} from '@codingame/monaco-vscode-api/vscode/vs/platform/files/common/files';
import { readNativeCsharpFileFromTauri, writeSourceToTauri } from '$lib/tauriSource';

const encoder = new TextEncoder();
const decoder = new TextDecoder();
let activeRoot: string | null = null;
let registered = false;

function normalizedPath(path: string): string {
  const normalized = path.replaceAll('\\', '/').replace(/\/+$/, '');
  return normalized.length > 0 ? normalized : '/';
}

function rootFor(path: string): string | null {
  const candidate = normalizedPath(path);
  return activeRoot && (candidate === activeRoot || candidate.startsWith(`${activeRoot}/`))
    ? activeRoot
    : null;
}

function readOnlyError(): never {
  throw new Error('This filesystem operation is not supported by the editor.');
}

const provider: IFileSystemProvider = {
  capabilities: FileSystemProviderCapabilities.FileReadWrite,
  onDidChangeCapabilities: Event.None,
  onDidChangeFile: Event.None,
  watch(_resource: URI, _options: IWatchOptions) {
    return { dispose() {} };
  },
  async stat(resource: URI): Promise<IStat> {
    const path = normalizedPath(resource.fsPath);
    const root = rootFor(path);
    if (!root) throw new Error('C# Peek path is outside every active workspace root.');
    if (path === root) {
      return { type: FileType.Directory, ctime: 0, mtime: 0, size: 0 };
    }
    const preview = await readNativeCsharpFileFromTauri(root, path);
    if (!preview) throw new Error('C# Peek file is unavailable outside the desktop app.');
    return { type: FileType.File, ctime: 0, mtime: 0, size: preview.byteCount };
  },
  async readFile(resource: URI): Promise<Uint8Array> {
    const path = normalizedPath(resource.fsPath);
    const root = rootFor(path);
    if (!root) throw new Error('C# Peek path is outside every active workspace root.');
    const preview = await readNativeCsharpFileFromTauri(root, path);
    if (!preview) throw new Error('C# Peek file is unavailable outside the desktop app.');
    return encoder.encode(preview.content);
  },
  async readdir(_resource: URI): Promise<[string, FileType][]> {
    return [];
  },
  async writeFile(resource: URI, content: Uint8Array, _options: IFileWriteOptions) {
    const path = normalizedPath(resource.fsPath);
    const root = rootFor(path);
    if (!root) throw new Error('C# source path is outside every active workspace root.');
    const current = await readNativeCsharpFileFromTauri(root, path);
    if (!current) throw new Error('C# source file is unavailable outside the desktop app.');
    const saved = await writeSourceToTauri(current, decoder.decode(content), current.revision);
    if (!saved) throw new Error('C# source file could not be saved.');
  },
  async mkdir(_resource: URI) {
    readOnlyError();
  },
  async delete(_resource: URI, _options: IFileDeleteOptions) {
    readOnlyError();
  },
  async rename(_from: URI, _to: URI, _options: IFileOverwriteOptions) {
    readOnlyError();
  }
};

export function registerNativeCsharpFileSystem(root: string | null): void {
  activeRoot = root ? normalizedPath(root) : null;
  if (!activeRoot) return;
  if (registered) return;
  registered = true;
  registerCustomProvider('file', provider);
}

export function nativeCsharpPathIsWithinRoot(root: string, path: string): boolean {
  const normalizedRoot = normalizedPath(root);
  const normalizedCandidate = normalizedPath(path);
  return (
    normalizedCandidate === normalizedRoot || normalizedCandidate.startsWith(`${normalizedRoot}/`)
  );
}
