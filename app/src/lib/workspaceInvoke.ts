import { invoke as nativeInvoke, type InvokeArgs, type InvokeOptions } from '@tauri-apps/api/core';
import { cancelAgentConversationRequestInBackground, createAgentConversationRequestId } from './tauriSource.ts';
import { qualifyWorkspaceResult, remoteWorkspaceRequest } from './workspacePaths.ts';

/**
 * Preserve the existing bridge; only machine-qualified filesystem requests use the server.
 * A remote call given a signal sends a request id, and an abort cancels that request on the
 * remote machine. Local calls ignore the signal; their callers drop a late result.
 */
export async function invoke<T>(command: string, args?: InvokeArgs, options?: InvokeOptions, signal?: AbortSignal): Promise<T> {
  if (!args || args instanceof ArrayBuffer || ArrayBuffer.isView(args) || Array.isArray(args)) return nativeInvoke<T>(command, args, options);
  const remote = remoteWorkspaceRequest(args, command);
  if (!remote) return nativeInvoke<T>(command, args, options);
  signal?.throwIfAborted();
  const requestId = signal ? createAgentConversationRequestId() : null;
  const cancel = (): void => { if (requestId !== null) void cancelAgentConversationRequestInBackground(requestId); };
  signal?.addEventListener('abort', cancel, { once: true });
  try {
    const result = await nativeInvoke<unknown>('remote_workspace', { profileId: remote.profileId, operation: command, args: remote.args, requestId });
    return qualifyWorkspaceResult(result, remote.profileId) as T;
  } finally {
    signal?.removeEventListener('abort', cancel);
  }
}
