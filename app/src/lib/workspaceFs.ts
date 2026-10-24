import * as local from '@tauri-apps/plugin-fs';
import { invoke } from './workspaceInvoke.ts';
import { parseRemoteWorkspacePath, remoteWorkspaceRequest } from './workspacePaths.ts';
export async function exists(path: string): Promise<boolean> {
  return parseRemoteWorkspacePath(path) ? invoke('workspace_exists', { path }) : local.exists(path);
}
export async function mkdir(path: string): Promise<void> {
  return parseRemoteWorkspacePath(path) ? invoke('workspace_mkdir', { path }) : local.mkdir(path);
}
export async function writeTextFile(path: string, content: string): Promise<void> {
  return parseRemoteWorkspacePath(path) ? invoke('workspace_write_text', { path, content }) : local.writeTextFile(path, content);
}
export async function readDir(path: string): Promise<local.DirEntry[]> {
  return parseRemoteWorkspacePath(path) ? invoke('workspace_read_dir', { path }) : local.readDir(path);
}
export async function rename(source: string, target: string): Promise<void> {
  return remoteWorkspaceRequest({ source, target }) ? invoke('workspace_rename', { source, target }) : local.rename(source, target);
}
export async function copyFile(source: string, target: string): Promise<void> {
  return remoteWorkspaceRequest({ source, target }) ? invoke('workspace_copy_file', { source, target }) : local.copyFile(source, target);
}
