// Settings → Plugins and Providers: what one provider row says, from its descriptor
// (`list_provider_descriptors`) and the same provider check the new-session picker runs.
import type { AgentConversationConfigState } from '../conversation/conversationConfig.ts';

/** One provider descriptor as `list_provider_descriptors` sends it (descriptors.rs). */
export type ProviderSummary = {
  id: string;
  displayName: string;
  adapterId: string;
  /** The installed adapter's version; null when it isn't installed, undefined when it couldn't be read. */
  adapterVersion?: string | null;
  readOnly: boolean;
  proofHome: boolean;
  permissionHandler: boolean;
};

/** The provider check's outcome; undefined while it runs. */
export type ProviderCheck = { config: AgentConversationConfigState } | { error: string } | undefined;

export type ProviderRow = {
  id: string;
  name: string;
  adapter: string;
  state: string;
  tone: 'neutral' | 'good' | 'attention' | 'bad';
  detail: string;
  models: string[];
  flags: string[];
};

// The app's own sign-in message (authentication.rs `sign_in_needed`).
const SIGN_IN_NEEDED = 'needs you to sign in';

export function providerRow(summary: ProviderSummary, check: ProviderCheck): ProviderRow {
  const row: ProviderRow = {
    id: summary.id,
    name: summary.displayName,
    adapter: summary.adapterVersion ? `${summary.adapterId} ${summary.adapterVersion}` : summary.adapterId,
    state: 'Checking…',
    tone: 'neutral',
    detail: '',
    models: [],
    flags: flags(summary)
  };
  if (summary.adapterVersion === null) return { ...row, state: 'Not installed' };
  if (!check) return row;
  if ('error' in check) {
    return check.error.includes(SIGN_IN_NEEDED)
      ? { ...row, state: 'Needs sign-in', tone: 'attention', detail: check.error }
      : { ...row, state: "Couldn't start", tone: 'bad', detail: check.error };
  }
  const labels = check.config.modelLabels ?? {};
  const models = check.config.availableModels.map((model) => labels[model] ?? model);
  return { ...row, state: 'Ready', tone: 'good', models, detail: models.length ? '' : 'No models listed' };
}

// Plan 0.2: an agent with no read-only setting can't prove, and one with no proof home is
// unavailable in proof mode. may_write and may_judge (and may_prove otherwise) are set by the
// provider matrix, which no descriptor records yet, so they are not shown.
function flags(summary: ProviderSummary): string[] {
  const shown: string[] = [];
  if (!summary.readOnly) shown.push("Can't prove: no read-only mode");
  else if (!summary.proofHome) shown.push("Can't prove: no separate proof login");
  shown.push(summary.permissionHandler ? "Tool approval: Assembly's permission handler" : 'Tool approval: its own launch settings');
  return shown;
}
