/**
 * agentConfigMemory.ts — what was last chosen for each agent.
 *
 * A model, effort or access picked in any composer is remembered per
 * provider, and a new session opens on it. Kept in settings so it survives a
 * restart, which is when it was most obviously being forgotten.
 */
import { settings, updateSettings, type AgentConfigChoice } from '../settings/settingsStore.svelte';

/** Remember a choice for `provider`. Fields left undefined keep what they had. */
export function rememberAgentConfigChoice(
  provider: string,
  choice: Partial<AgentConfigChoice>
): void {
  const current = settings.agents.lastChoiceByProvider[provider] ?? {
    model: null,
    reasoningEffort: null,
    approvalPolicy: null
  };
  const next: AgentConfigChoice = {
    model: choice.model === undefined ? current.model : choice.model,
    reasoningEffort:
      choice.reasoningEffort === undefined ? current.reasoningEffort : choice.reasoningEffort,
    approvalPolicy: choice.approvalPolicy === undefined ? current.approvalPolicy : choice.approvalPolicy
  };
  updateSettings('agents', {
    lastChoiceByProvider: { ...settings.agents.lastChoiceByProvider, [provider]: next }
  });
}

/** What was last chosen for `provider`, or nothing. */
export function rememberedAgentConfigChoice(provider: string): AgentConfigChoice | null {
  return settings.agents.lastChoiceByProvider[provider] ?? null;
}
