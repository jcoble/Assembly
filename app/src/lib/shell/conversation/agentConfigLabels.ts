/**
 * agentConfigLabels.ts — the one place raw configuration ids become words a
 * person reads.
 *
 * The agent reports its settings as machine ids: `gpt-5.6-sol`, `xhigh`,
 * `on-request`. Those are fine on the wire and wrong on screen. Everything
 * that shows a model, a reasoning effort or an approval policy asks this
 * module for the label, so the composer, the menus and any later surface
 * cannot drift apart.
 *
 * Unknown ids are expected, not exceptional: the agent gains models faster
 * than this app is rebuilt. Every function here falls back to a tidied-up
 * version of the id rather than hiding it, so a new model shows as
 * "GPT-5.7 Vega" instead of blank.
 */

/** Vendor prefixes that carry no meaning on screen — the model name follows. */
const VENDOR_PREFIXES = new Set(['claude', 'openai', 'anthropic', 'google', 'meta']);

/** Words that read better fully capitalised than title-cased. */
const ALWAYS_UPPERCASE = new Set(['gpt', 'llm', 'api', 'xl']);

function titleCasePart(part: string): string {
  if (!part) return part;
  if (ALWAYS_UPPERCASE.has(part.toLowerCase())) return part.toUpperCase();
  // A version number ("5.6", "4o") is already written the way it should read.
  if (/\d/.test(part)) return part;
  return part[0].toUpperCase() + part.slice(1);
}

/**
 * A model id as a name: `gpt-5.6-sol` → "GPT-5.6 Sol", `claude-opus-5` → "Opus 5".
 * A leading vendor word is dropped because the conversation already knows
 * which agent it is talking to. "GPT" is kept, joined to its version, because
 * it is part of the model's own name.
 */
export function modelLabel(model: string | null | undefined, labels?: Readonly<Record<string, string>>): string {
  if (!model) return 'Default';
  if (labels?.[model]) return labels[model];
  const parts = model
    .trim()
    .split(/[-_\s]+/)
    .flatMap((part) => part.split(/(?<=[a-z0-9])(?=[A-Z])/))
    .filter(Boolean);
  if (parts.length === 0) return model;
  if (parts.length > 1 && parts[0].toLowerCase() === 'gpt') {
    return [`GPT-${parts[1]}`, ...parts.slice(2).map(titleCasePart)].join(' ');
  }
  const withoutVendor =
    parts.length > 1 && VENDOR_PREFIXES.has(parts[0].toLowerCase()) ? parts.slice(1) : parts;
  return withoutVendor.map(titleCasePart).join(' ');
}

/** The five reasoning levels the agent offers, in the words the UI uses. */
const EFFORT_LABELS: Record<string, string> = {
  minimal: 'Minimal',
  low: 'Low',
  medium: 'Medium',
  high: 'High',
  xhigh: 'Extra High',
  max: 'Max'
};

/** A reasoning-effort id as a name: `xhigh` → "Extra High". */
export function effortLabel(effort: string | null | undefined): string {
  if (!effort) return 'Default';
  return EFFORT_LABELS[effort.toLowerCase()] ?? modelLabel(effort);
}

/**
 * Approval policies, said in terms of what the agent will do rather than the
 * id's own vocabulary. The second line is the one-sentence explanation shown
 * under the name in the menu.
 */
const APPROVAL_LABELS: Record<string, { label: string; description: string }> = {
  untrusted: {
    label: 'Ask every time',
    description: 'Nothing runs until you say yes.'
  },
  'on-request': {
    label: 'Ask when needed',
    description: 'The agent works on its own and asks before anything risky.'
  },
  'on-failure': {
    label: 'Ask after a failure',
    description: 'The agent works on its own and asks only when a command fails.'
  },
  never: {
    label: 'Never ask',
    description: 'The agent runs everything itself. Nothing stops to wait for you.'
  },
  'read-only': {
    label: 'Ask for approval',
    description: 'Can edit this workspace; asks before external files or internet access.'
  },
  agent: {
    label: 'Approve for me',
    description: 'Can edit this workspace; automatic review checks actions for safety.'
  },
  'agent-full-access': {
    label: 'Full access',
    description: 'The agent can make changes and run commands without asking.'
  },
  default: {
    label: 'Default',
    description: 'Standard behavior. The agent asks before anything dangerous.'
  },
  acceptedits: {
    label: 'Accept Edits',
    description: 'File edits are accepted automatically; other actions still ask.'
  },
  plan: {
    label: 'Plan Mode',
    description: 'Planning only. No tools actually run.'
  },
  dontask: {
    label: "Don't Ask",
    description: 'Never prompts. Anything not pre-approved is denied.'
  },
  bypasspermissions: {
    label: 'Bypass Permissions',
    description: 'Runs everything without permission checks.'
  }
};

/** An approval-policy id as a name: `on-request` → "Ask when needed". */
export function approvalLabel(policy: string | null | undefined): string {
  if (!policy) return 'Approvals';
  return APPROVAL_LABELS[policy.toLowerCase()]?.label ?? modelLabel(policy);
}

/** The one-line explanation under an approval policy, or an empty string. */
export function approvalDescription(policy: string | null | undefined): string {
  if (!policy) return '';
  return APPROVAL_LABELS[policy.toLowerCase()]?.description ?? '';
}

/**
 * What the composer's pill says: the model and how hard it is thinking, e.g.
 * "GPT-5.6 Sol · Extra High". Either half may be missing on an agent that does not
 * report it, and the pill then shows only the half that exists.
 */
export function modelEffortLabel(
  model: string | null | undefined,
  effort: string | null | undefined,
  labels?: Readonly<Record<string, string>>
): string {
  const parts: string[] = [];
  if (model) parts.push(modelLabel(model, labels));
  if (effort) parts.push(effortLabel(effort));
  return parts.length ? parts.join(' · ') : 'Agent settings';
}
