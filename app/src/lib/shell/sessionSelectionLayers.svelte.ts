import { rail } from './stores/sessionRailStore.svelte';
import { warmAgentConversationConfig } from './conversation/conversationConfig';
import { sessionWorkspaceRoot } from '../workspacePaths.ts';
import { projectRegistry } from './projects/projectRegistry.svelte.ts';
/**
 * The projections rebuilt when a person selects a session in the rail.
 *
 * Keep this sequence explicit. Each large surface gets one small loader here
 * so it can be added, measured, and removed without waking the other surfaces.
 */
import { readAgentConversationWorkspaceFromTauri, validateProjectRootFromTauri } from '../tauriSource';
import {
  loadConversationSessionDraft
} from './conversation/conversationService';
import { disposeSelectedConversationChat, selectConversationChat, selectedConversationChatReady } from './conversation/conversationConnection';
import {
  ensureConversationSession,
  evictInactiveConversationSessions,
  setConversationMode,
  setConversationAgentConfigState,
  setConversationAgentConfigError,
  getConversationSession
} from './conversation/conversationStore.svelte';
import type { AgentConversationProvider } from './conversation/conversationTypes';
import { countInvoke } from './devInvokeCounter.svelte';
import { normalizeProvider, type OwnedSession } from './ownedSessions';

export type SessionSelectionOwner = {
  generation: number;
  signal: AbortSignal;
};

/**
 * A remote machine can stop answering before the app hears that it went away.
 * Its folder, workspace and draft reads then fail quietly (undefined), so the
 * saved chat still opens from the Mac's copy. Local failures still throw.
 */
async function unlessUnreachable<T>(session: OwnedSession, read: Promise<T>): Promise<T | undefined> {
  try {
    return await read;
  } catch (error) {
    if (session.executionEnvironment !== 'remote') throw error;
    return undefined;
  }
}

export class SessionSelectionLayers {
  treeOwnedId = $state<string | null>(null);
  treeRoot = $state('');
  /** The root the last tree fill asked for, kept even when that folder was missing. */
  treeRequestedRoot = $state('');
  hasTreeProjection = $state(false);
  /** The last tree fill could not check the folder, so the session's files were never shown. */
  treeCheckFailed = false;
  chatOwnedId = $state<string | null>(null);
  hasChatProjection = $state(false);
  private selectionGeneration = 0;
  private requestedChatOwnedId: string | null = null;

  async selectSession(
    session: OwnedSession,
    displayedChatOwnedId: string | null,
    owner: SessionSelectionOwner
  ): Promise<void> {
    this.selectionGeneration = owner.generation;
    this.requestedChatOwnedId = session.ownedId;
    await Promise.all([
      this.fillTreeView(session, owner),
      this.fillChatHistory(session, displayedChatOwnedId, owner)
    ]);
  }

  async fillTreeView(
    session: OwnedSession,
    owner: SessionSelectionOwner
  ): Promise<void> {
    if (!this.isCurrent(owner)) return;
    const root = sessionWorkspaceRoot(session, projectRegistry.projects);
    this.treeRequestedRoot = root;
    this.treeCheckFailed = false;

    if (!root || (session.executionEnvironment === 'remote'
      && rail.remoteConnections[session.remoteProfileId ?? ''] !== 'connected')) {
      if (!this.isCurrent(owner)) return;
      this.hasTreeProjection = true;
      this.treeOwnedId = session.ownedId;
      this.treeRoot = '';
      return;
    }

    countInvoke('validate_project_root');
    const validation = await unlessUnreachable(session, validateProjectRootFromTauri(root, owner.signal));
    if (!this.isCurrent(owner)) return;
    this.treeCheckFailed = validation === undefined;
    if (validation === undefined || (validation && (!validation.exists || !validation.isDirectory))) {
      this.hasTreeProjection = true;
      this.treeOwnedId = session.ownedId;
      this.treeRoot = '';
      return;
    }

    this.hasTreeProjection = true;
    this.treeOwnedId = session.ownedId;
    this.treeRoot = root;
  }

  async fillChatHistory(
    session: OwnedSession,
    displayedChatOwnedId: string | null,
    owner: SessionSelectionOwner
  ): Promise<void> {
    if (!this.isCurrent(owner)) return;
    const departingOwnedId = this.chatOwnedId ?? displayedChatOwnedId;

    const provider = this.providerFor(session);
    if (!provider) {
      if (!this.isCurrent(owner)) return;
      if (departingOwnedId) disposeSelectedConversationChat(departingOwnedId);
      this.hasChatProjection = false;
      this.chatOwnedId = null;
      evictInactiveConversationSessions(null);
      return;
    }

    if (departingOwnedId && departingOwnedId !== session.ownedId) {
      disposeSelectedConversationChat(departingOwnedId);
    }

    const conversation = ensureConversationSession(session.ownedId, provider);
    const offline = session.executionEnvironment === 'remote'
      && rail.remoteConnections[session.remoteProfileId ?? ''] !== 'connected';
    if (!offline) {
      countInvoke('read_agent_conversation_workspace');
      const workspace = await unlessUnreachable(session, readAgentConversationWorkspaceFromTauri(session.ownedId));
      if (!this.isCurrent(owner)) return;
      conversation.viewByHistoryId = workspace?.conversation?.viewByHistoryId ?? {};
    }
    setConversationMode(session.ownedId, 'structured');
    selectConversationChat(session.ownedId, session.ownedId, owner.signal);
    await selectedConversationChatReady(session.ownedId);
    if (!this.isCurrent(owner)) {
      if (this.requestedChatOwnedId !== session.ownedId) disposeSelectedConversationChat(session.ownedId);
      return;
    }
    if (!offline) await unlessUnreachable(session, loadConversationSessionDraft(session.ownedId));
    if (!this.isCurrent(owner)) {
      if (this.requestedChatOwnedId !== session.ownedId) disposeSelectedConversationChat(session.ownedId);
      return;
    }
    this.hasChatProjection = true;
    this.chatOwnedId = session.ownedId;
    if (!offline && session.executionEnvironment === 'remote' && session.nativeSessionId) {
      const generation = getConversationSession(session.ownedId)?.generation;
      if (generation !== undefined) void this.warmRemoteConfig(session.ownedId, generation, owner);
    }
    if (departingOwnedId && departingOwnedId !== session.ownedId) {
      disposeSelectedConversationChat(departingOwnedId);
    }
    evictInactiveConversationSessions(session.ownedId);
  }

  // The warm-up itself is not cancelled; a selection that moved on drops its answer.
  private async warmRemoteConfig(ownedId: string, generation: number, owner: SessionSelectionOwner): Promise<void> {
    try {
      const config = await warmAgentConversationConfig(ownedId, generation);
      if (this.isCurrent(owner)) setConversationAgentConfigState(ownedId, config);
    } catch (error) {
      if (this.isCurrent(owner)) setConversationAgentConfigError(ownedId, error instanceof Error ? error.message : String(error));
    }
  }

  abandonSelection(owner: SessionSelectionOwner): void {
    if (owner.generation === this.selectionGeneration) this.selectionGeneration += 1;
  }

  clearTreeView(): void {
    this.hasTreeProjection = false;
    this.treeOwnedId = null;
    this.treeRoot = '';
  }

  clearChatHistory(): void {
    this.selectionGeneration += 1;
    this.requestedChatOwnedId = null;
    if (this.chatOwnedId) disposeSelectedConversationChat(this.chatOwnedId);
    this.hasChatProjection = false;
    this.chatOwnedId = null;
    evictInactiveConversationSessions(null);
  }

  private providerFor(session: OwnedSession): AgentConversationProvider | null {
    const { agent } = normalizeProvider(session.agent);
    return agent === 'claude' || agent === 'codex' || agent === 'antigravity' ? agent : null;
  }

  private isCurrent(owner: SessionSelectionOwner): boolean {
    return !owner.signal.aborted && owner.generation === this.selectionGeneration;
  }

}
