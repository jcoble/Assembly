import type { AgentCapabilities, AgentConfigOption, AgentConfigValue } from './conversationTypes.ts';
import { configOptionPlacement, type AgentConfigOptionPlacement } from './conversationTypes.ts';

export interface ConversationControlView {
  option: AgentConfigOption;
  placement: AgentConfigOptionPlacement;
  value: AgentConfigValue;
  pending: boolean;
  reason: string | null;
}

function equalValue(left: AgentConfigValue, right: AgentConfigValue): boolean {
  return JSON.stringify(left) === JSON.stringify(right);
}

function unsupportedReason(capabilities: AgentCapabilities, option: AgentConfigOption): string | null {
  const category = `${option.category} ${option.id}`.toLowerCase();
  return (category.includes('permission') || category.includes('approval')) && !capabilities.interaction.permissions
    ? 'This provider does not support permission controls for the session'
    : null;
}

export function configValueIsAdvertised(option: AgentConfigOption, value: AgentConfigValue): boolean {
  return !option.choices || option.choices.some((choice) => equalValue(choice.value, value));
}

/** Only options in the authoritative snapshot become controls. */
export function conversationControlViews(
  capabilities: AgentCapabilities | null,
  confirmed: Record<string, AgentConfigValue> = {},
  pending: Record<string, AgentConfigValue> = {},
  errors: Record<string, string> = {}
): ConversationControlView[] {
  if (!capabilities) return [];
  return capabilities.configOptions.map((option) => {
    const candidate = pending[option.id] ?? confirmed[option.id] ?? option.value;
    const reason = unsupportedReason(capabilities, option)
      ?? (!configValueIsAdvertised(option, candidate)
      ? 'The provider did not advertise this value'
      : errors[option.id] ?? null);
    return {
      option,
      placement: configOptionPlacement(option.category),
      value: candidate,
      pending: option.id in pending,
      reason
    };
  });
}

export function controlsForPlacement(
  views: readonly ConversationControlView[],
  placement: AgentConfigOptionPlacement
): ConversationControlView[] {
  return views.filter((view) => view.placement === placement);
}
