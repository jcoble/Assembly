/**
 * Pure rules for the thread-first session draft.
 *
 * The pane owns the interaction and the route owns the side effects. This
 * module is deliberately plain data only: it validates the draft, groups the
 * provider configuration already known to the shell, and assembles the one
 * request sent on the first message.
 */

import { sessionTitleFromPrompt } from '../../sessionStrip.ts';
import type { ExecutionEnvironment } from '../../../tauriSource.ts';

export type ThreadStartProvider = 'codex' | 'claude' | 'antigravity';

export type ThreadStartProviderConfig = {
  provider: string;
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

export type ThreadStartModel = {
  id: string;
  label: string;
  hint: string;
  available: boolean;
  unavailableReason: string | null;
};

export type ThreadStartModelGroup = {
  provider: ThreadStartProvider;
  label: string;
  models: ThreadStartModel[];
};

export type ThreadStartPickerState = {
  prompt: string;
  executionEnvironment: ExecutionEnvironment;
  remoteProfileId: string | null;
  provider: ThreadStartProvider;
  model: string;
  effort: string;
  access: string;
  /** The registry project, or null for "No project". */
  projectId: string | null;
  projectPath: string;
  cwd: string;
  /** Why the project folder can't be used: a bare repository, or the folder
   * check's own error. Empty when the folder is fine or not yet checked. */
  rootProblem: string;
};

export type ThreadStartProblem = {
  field: 'prompt' | 'project';
  message: string;
};

export type ThreadStartRequest = {
  prompt: string;
  executionEnvironment: ExecutionEnvironment;
  remoteProfileId: string | null;
  provider: ThreadStartProvider;
  model: string | null;
  reasoningEffort: string | null;
  approvalPolicy: string | null;
  projectId: string | null;
  projectPath: string | null;
  cwd: string;
  title: string;
};

export const BARE_ROOT_MESSAGE =
  'This folder is a bare Git repository with no files to work on. Remove this project and add a checked-out folder instead.';

const PROVIDER_ORDER: readonly ThreadStartProvider[] = ['codex', 'claude', 'antigravity'];

const PROVIDER_LABELS: Record<ThreadStartProvider, string> = {
  codex: 'OpenAI',
  claude: 'Anthropic',
  antigravity: 'Antigravity'
};

/**
 * Placeholders keep the draft menus nonempty until a live catalogue arrives.
 * They never extend a live model or effort list, even an empty effort list.
 * Claude 0.88.0/0.93.0 and Codex 1.13.1 report both controls; raw receipts:
 * reports/lanes-2026-10-19/tsk-1508-model-effort-receipts/.
 */
const FALLBACK_MODELS: Record<ThreadStartProvider, readonly string[]> = {
  codex: ['gpt-5.6-luna', 'gpt-5.6-sol', 'gpt-5.6-terra'],
  claude: ['default', 'sonnet', 'haiku', 'opus', 'claude-opus-5', 'claude-fable-5'],
  // Antigravity names a model by the display name its own `models` command
  // prints, and its speed tiers are separate models rather than an effort
  // setting. The first is the one it starts on when nothing is chosen.
  antigravity: [
    'Gemini 3.7 Flash (High)',
    'Gemini 3.7 Flash (Medium)',
    'Gemini 3.7 Flash (Low)',
    'Gemini 3.1 Pro (High)',
    'Gemini 3.1 Pro (Low)'
  ]
};

const MODEL_HINTS: Record<string, string> = {
  'gpt-5.6-luna': 'balanced build work',
  'gpt-5.6-sol': 'deep review',
  'gpt-5.6-terra': 'fast iteration',
  opus: 'complex work',
  sonnet: 'everyday tasks',
  haiku: 'quick lookups'
};

const FALLBACK_EFFORTS: Record<ThreadStartProvider, string> = {
  codex: 'max',
  claude: 'medium',
  antigravity: ''
};

const FALLBACK_EFFORT_CHOICES: Record<ThreadStartProvider, readonly string[]> = {
  codex: ['low', 'medium', 'high', 'xhigh', 'max'],
  claude: ['low', 'medium', 'high', 'max'],
  antigravity: []
};

// Historical placeholders only; a live mode catalogue owns its IDs and current value.
const FALLBACK_ACCESS: Record<ThreadStartProvider, string> = {
  // Codex read-only uses on-request internally (AgentMode.ReadOnly, ACP 1.13.1).
  codex: 'on-request',
  // Claude ACP 0.88.0/0.93.0 advertises the native acceptEdits mode.
  claude: 'acceptEdits',
  // Placeholder spelling; Antigravity 1.2.1 calls full access yolo (config_options.py).
  antigravity: 'bypassPermissions'
};

const FALLBACK_ACCESS_CHOICES: Record<ThreadStartProvider, readonly string[]> = {
  codex: ['untrusted', 'on-request', 'never'],
  claude: ['default', 'acceptEdits', 'plan', 'dontAsk', 'bypassPermissions'],
  antigravity: ['bypassPermissions']
};

function providerFor(value: string): value is ThreadStartProvider {
  return PROVIDER_ORDER.includes(value as ThreadStartProvider);
}

function tidy(value: string | null | undefined): string {
  return typeof value === 'string' ? value.trim() : '';
}

function unique(values: readonly string[]): string[] {
  return [...new Set(values.map(tidy).filter(Boolean))];
}

/** A project's session runs in the project's root folder. A bare repository
 * has no files to work on, so it gives no folder at all. */
export function projectCwd(rootPath: string, rootIsBare: boolean): string {
  return rootIsBare ? '' : rootPath;
}

function modelLabel(model: string): string {
  const parts = model
    .split(/[-_\s]+/)
    .filter(Boolean)
    .map((part) => (/^\d/.test(part) ? part : `${part[0].toUpperCase()}${part.slice(1)}`));
  if (parts.length > 1 && /^(gpt|claude|anthropic|openai)$/i.test(parts[0])) parts.shift();
  return parts.join(' ') || model;
}

function modelHint(model: string): string {
  const key = model.toLowerCase();
  return MODEL_HINTS[key]
    ?? (key.includes('fast') || key.includes('haiku') ? 'quick lookups' : 'general build work');
}

/**
 * Convert the config snapshots already loaded for existing sessions into the
 * provider-grouped menu used by a fresh draft. Current values are included
 * even when a provider's advertised list has changed between sessions.
 */
export function groupProviderModels(
  configs: readonly ThreadStartProviderConfig[]
): ThreadStartModelGroup[] {
  const byProvider = new Map<ThreadStartProvider, string[]>();
  const currentByProvider = new Map<ThreadStartProvider, string>();
  for (const provider of PROVIDER_ORDER) byProvider.set(provider, []);

  for (const config of configs) {
    if (!providerFor(config.provider)) continue;
    const models = byProvider.get(config.provider) ?? [];
    const currentModel = tidy(config.model);
    if (currentModel) currentByProvider.set(config.provider, currentModel);
    const next = unique([...(currentModel ? [currentModel] : []), ...config.availableModels]);
    byProvider.set(config.provider, unique([...models, ...next]));
  }

  return PROVIDER_ORDER.map((provider) => {
    const configured = byProvider.get(provider) ?? [];
    const current = currentByProvider.get(provider);
    const snapshots = configs.filter((config) => config.provider === provider);
    const advertised = new Set(snapshots.flatMap((config) => unique(config.availableModels)));
    const availabilityKnown = advertised.size > 0;
    // A remembered choice with no session behind it is one model, not a list:
    // the known list goes with it until a session reports its own.
    const models = availabilityKnown
      ? unique([current ?? '', ...configured])
      : unique([current ?? '', ...configured, ...FALLBACK_MODELS[provider]]);
    return {
      provider,
      label: PROVIDER_LABELS[provider],
      models: models.map((id) => {
        const available = !availabilityKnown || advertised.has(id);
        return {
          id,
          label: snapshots.find((config) => config.modelLabels?.[id])?.modelLabels?.[id] ?? modelLabel(id),
          hint: modelHint(id),
          available,
          unavailableReason: available ? null : 'Unavailable in the current session service.'
        };
      })
    };
  });
}

function configForProvider(
  provider: ThreadStartProvider,
  configs: readonly ThreadStartProviderConfig[]
): ThreadStartProviderConfig | null {
  return configs.find((config) => config.provider === provider && config.availableModels.length > 0)
    ?? configs.find((config) => config.provider === provider) ?? null;
}

function firstConfiguredModel(
  provider: ThreadStartProvider,
  configs: readonly ThreadStartProviderConfig[]
): string {
  const config = configForProvider(provider, configs);
  const available = unique(config?.availableModels ?? []);
  const current = tidy(config?.model);
  return (current && (!available.length || available.includes(current)) ? current : '')
    || available[0]
    || current
    || FALLBACK_MODELS[provider][0]
    || '';
}

/** The values painted when the pane first opens. No session is created here. */
export function defaultThreadStartState(input: {
  projectId?: string | null;
  projectPath: string;
  cwd?: string;
  provider?: ThreadStartProvider;
  providerConfigs?: readonly ThreadStartProviderConfig[];
}): ThreadStartPickerState {
  const configs = input.providerConfigs ?? [];
  const provider: ThreadStartProvider = input.provider ?? 'codex'; // Codex is the product default.
  const config = configForProvider(provider, configs);
  const accessConfig = configs.find((config) => config.provider === provider && config.availableApprovalPolicies.length > 0)
    ?? config;
  const hasLiveCatalog = (config?.availableModels.length ?? 0) > 0;
  const hasLiveAccessCatalog = (accessConfig?.availableApprovalPolicies.length ?? 0) > 0
    || (accessConfig?.availableModels.length ?? 0) > 0;
  const effortChoices = effortChoicesFor(provider, configs);
  const accessChoices = accessChoicesFor(provider, configs);
  return {
    prompt: '',
    executionEnvironment: 'local',
    remoteProfileId: null,
    provider,
    model: firstConfiguredModel(provider, configs),
    // A remembered value the provider does not know — a spelling an earlier
    // build made up, or another provider's — is not offered back: it would be
    // refused at the first send and start nothing.
    effort: choiceAmong(config?.reasoningEffort, effortChoices)
      || (hasLiveCatalog ? (effortChoices[0] ?? '') : FALLBACK_EFFORTS[provider]),
    access: choiceAmong(accessConfig?.approvalPolicy, accessChoices)
      || (hasLiveAccessCatalog ? (accessChoices[0] ?? '') : FALLBACK_ACCESS[provider]),
    projectId: input.projectId ?? null,
    projectPath: tidy(input.projectPath),
    cwd: tidy(input.cwd) || tidy(input.projectPath),
    rootProblem: ''
  };
}

/** `value` when it is one of `choices`, else nothing. */
function choiceAmong(value: string | null | undefined, choices: readonly string[]): string {
  const wanted = tidy(value);
  return wanted && choices.includes(wanted) ? wanted : '';
}

export function effortChoicesFor(
  provider: ThreadStartProvider,
  configs: readonly ThreadStartProviderConfig[]
): string[] {
  const config = configForProvider(provider, configs);
  const available = unique(config?.availableEfforts ?? []);
  if ((config?.availableModels.length ?? 0) > 0) return available;
  return available.length ? available : [...FALLBACK_EFFORT_CHOICES[provider]];
}

/**
 * The efforts a draft offers for one model, and the one it opens on: the
 * wanted effort if the model has it, else the model's own default, else the
 * probe's current effort, else the first offered. A provider that sends no
 * per-model lists keeps its provider-wide list; a model those lists leave out
 * has no effort control.
 */
export function draftEffortFor(
  config: Pick<ThreadStartProviderConfig, 'reasoningEffort' | 'availableEfforts' | 'modelEfforts' | 'modelDefaultEfforts'>,
  model: string,
  wanted: string | null | undefined
): { efforts: string[]; effort: string } {
  const perModel = config.modelEfforts && Object.keys(config.modelEfforts).length ? config.modelEfforts : null;
  const efforts = perModel ? perModel[model] ?? [] : config.availableEfforts;
  const effort = [wanted, config.modelDefaultEfforts?.[model], config.reasoningEffort]
    .find((choice) => choice && efforts.includes(choice)) ?? efforts[0] ?? '';
  return { efforts, effort };
}

export function accessChoicesFor(
  provider: ThreadStartProvider,
  configs: readonly ThreadStartProviderConfig[]
): string[] {
  const config = configs.find((config) => config.provider === provider && config.availableApprovalPolicies.length > 0)
    ?? configForProvider(provider, configs);
  const available = unique(config?.availableApprovalPolicies ?? []);
  if ((config?.availableModels.length ?? 0) > 0) return available;
  return available.length ? available : [...FALLBACK_ACCESS_CHOICES[provider]];
}

/**
 * The first line becomes the rail title.
 *
 * Shaped by the one function that decides what a prompt's first line looks like
 * as a name, because the backend writes exactly the same string when the
 * message goes out. The rail saves this row back seconds later, and a title
 * that differs by a character reads as a rename there — which would stop the
 * session from ever being given a better name after its first turn.
 */
export function titleFromPrompt(prompt: string, projectPath: string): string {
  const fallback = tidy(projectPath).split('/').filter(Boolean).at(-1);
  if (!fallback) return sessionTitleFromPrompt(tidy(prompt)) ?? 'New conversation';
  return sessionTitleFromPrompt(tidy(prompt)) ?? `Build in ${fallback}`;
}

/** Validate only what can be settled before a backend call. */
export function validateThreadStart(state: ThreadStartPickerState, hasAttachments = false): ThreadStartProblem[] {
  const problems: ThreadStartProblem[] = [];
  if (!tidy(state.prompt) && !hasAttachments) {
    problems.push({ field: 'prompt', message: 'Write a message or attach an image.' });
  }
  if (tidy(state.projectPath) && !tidy(state.projectPath).startsWith('/')) {
    problems.push({ field: 'project', message: 'Choose a project workspace first.' });
  }
  if (!tidy(state.cwd).startsWith('/')) {
    const message = !tidy(state.projectPath)
      ? 'Choose an existing checkout first.'
      : state.rootProblem || 'Checking the project folder. Try again in a moment.';
    problems.push({ field: 'project', message });
  }
  if (state.executionEnvironment === 'remote' && !tidy(state.remoteProfileId)) {
    problems.push({ field: 'project', message: 'Choose a remote machine first.' });
  }
  return problems;
}

/** Assemble the route-owned spawn request, or null while the draft is invalid. */
export function buildThreadStartRequest(
  state: ThreadStartPickerState,
  hasAttachments = false
): ThreadStartRequest | null {
  if (validateThreadStart(state, hasAttachments).length > 0) return null;
  return {
    prompt: tidy(state.prompt),
    executionEnvironment: state.executionEnvironment,
    remoteProfileId: state.remoteProfileId,
    provider: state.provider,
    model: tidy(state.model) || null,
    reasoningEffort: tidy(state.effort) || null,
    approvalPolicy: tidy(state.access) || null,
    projectId: state.projectId ?? null,
    projectPath: tidy(state.projectPath) || null,
    cwd: tidy(state.cwd),
    title: titleFromPrompt(state.prompt, state.projectPath)
  };
}

export function displayEffort(value: string | null | undefined): string {
  const normalized = tidy(value).toLowerCase();
  if (normalized === 'xhigh') return 'Extra high';
  if (normalized === 'max') return 'Max';
  if (!normalized) return 'Default';
  return normalized[0].toUpperCase() + normalized.slice(1);
}

export function displayAccess(value: string | null | undefined): string {
  const normalized = tidy(value).toLowerCase();
  if (normalized === 'acceptedits') return 'Workspace write';
  if (normalized === 'on-request') return 'Ask when needed';
  if (normalized === 'never' || normalized === 'bypasspermissions') return 'Unrestricted';
  if (normalized === 'plan') return 'Plan only';
  if (!normalized) return 'Default';
  return normalized
    .split(/[-_]+/)
    .filter(Boolean)
    .map((part) => part[0].toUpperCase() + part.slice(1))
    .join(' ');
}

export function displayProvider(provider: ThreadStartProvider): string {
  return PROVIDER_LABELS[provider];
}
