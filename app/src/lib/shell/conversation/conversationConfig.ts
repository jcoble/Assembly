import { invokeConversationCommand as invoke } from './conversationInvoke.ts';
import {
  cancelAgentConversationRequestFromTauri,
  createAgentConversationRequestId
} from '../../tauriSource.ts';

export type AgentConversationConfigState = {
  model: string | null;
  availableModels: string[];
  modelLabels?: Record<string, string>;
  reasoningEffort: string | null;
  availableEfforts: string[];
  modelEfforts?: Record<string, string[]>;
  modelDefaultEfforts?: Record<string, string>;
  approvalPolicy: string | null;
  availableApprovalPolicies: string[];
};

export type AgentConversationConfigRequest = {
  ownedId: string;
  generation: number;
  model?: string;
  reasoningEffort?: string;
  approvalPolicy?: string;
};

export type AgentConversationConfigField = 'model' | 'reasoningEffort' | 'approvalPolicy';

export const emptyAgentConversationConfigState = (): AgentConversationConfigState => ({
  model: null,
  availableModels: [],
  reasoningEffort: null,
  availableEfforts: [],
  approvalPolicy: null,
  availableApprovalPolicies: []
});

/** Freeze provider-advertised choices for one open menu while events keep streaming. */
export function snapshotAgentConversationConfig(
  state: AgentConversationConfigState
): AgentConversationConfigState {
  return {
    model: state.model,
    availableModels: [...state.availableModels],
    modelLabels: { ...state.modelLabels },
    reasoningEffort: state.reasoningEffort,
    availableEfforts: [...state.availableEfforts],
    approvalPolicy: state.approvalPolicy,
    availableApprovalPolicies: [...state.availableApprovalPolicies]
  };
}

/** Whether the composer has any current value or advertised choice to present. */
export function hasAgentConversationConfig(state: AgentConversationConfigState): boolean {
  return Boolean(
    state.model ||
      state.reasoningEffort ||
      state.approvalPolicy ||
      state.availableModels.length ||
      state.availableEfforts.length ||
      state.availableApprovalPolicies.length
  );
}

export async function readAgentConversationConfig(
  ownedId: string,
  signal?: AbortSignal
): Promise<AgentConversationConfigState | null> {
  if (signal?.aborted) return null;
  const requestId = createAgentConversationRequestId();
  const cancel = (): void => {
    void cancelAgentConversationRequestFromTauri(requestId);
  };
  signal?.addEventListener('abort', cancel, { once: true });
  try {
    if (signal?.aborted) {
      await cancelAgentConversationRequestFromTauri(requestId);
      return null;
    }
    const state = await invoke<AgentConversationConfigState>('read_agent_conversation_config', {
      ownedId,
      requestId
    });
    return signal?.aborted ? null : state;
  } catch (error) {
    if (signal?.aborted) return null;
    throw error;
  } finally {
    signal?.removeEventListener('abort', cancel);
  }
}

/**
 * Ask this conversation's agent what it offers, once, and stop it again.
 *
 * For a conversation resumed from a past transcript: it has never run, so the
 * models, effort levels and approval policies a handshake would have reported
 * are nowhere yet, and the composer has nothing to show. What comes back is
 * stored, so every read after this one is an ordinary read.
 */
export async function warmAgentConversationConfig(
  ownedId: string,
  generation: number
): Promise<AgentConversationConfigState> {
  const state = await invoke<AgentConversationConfigState>('warm_agent_conversation_config', {
    ownedId,
    generation
  });
  return state;
}

export async function setAgentConversationConfig(
  request: AgentConversationConfigRequest
): Promise<AgentConversationConfigState> {
  const state = await invoke<AgentConversationConfigState>('set_agent_conversation_config', { request });
  return state;
}
