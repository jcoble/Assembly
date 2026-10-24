import {
  ASSISTANCE_RECIPE_IDS,
  ASSISTANCE_SURFACES,
  type AssistanceContext,
  type AssistanceFieldPatch,
  type AssistanceProposal,
  type AssistanceRecipeId,
  type AssistanceTargetRef
} from './assistanceTypes.ts';

export interface SchemaIssue {
  path: string;
  message: string;
}

export interface SchemaResult<T> {
  ok: boolean;
  value?: T;
  issues: SchemaIssue[];
}

const MAX_SERIALIZED_BYTES = 32_000;
const MAX_TEXT_BYTES = 2_048;
const MAX_FACTS = 32;
const MAX_PATCHES = 32;

const surfaceSet = new Set<string>(ASSISTANCE_SURFACES);
const recipeSet = new Set<string>(ASSISTANCE_RECIPE_IDS);

const allowedFields: Record<string, readonly string[]> = {
  conversation: ['title', 'summary', 'draft', 'question'],
  editor: ['summary', 'note'],
  git: ['branch', 'commitMessage', 'reviewComment', 'summary', 'note'],
  diff: ['summary', 'reviewComment', 'note'],
  problems: ['explanation', 'note', 'suggestedFix'],
  'run-configuration': ['name', 'cwd', 'script', 'environmentName', 'summary', 'note'],
  browser: ['note', 'intent', 'selector', 'accessibleName'],
  context: ['summary', 'note'],
  form: ['label', 'description', 'value', 'note'],
  save: ['summary', 'specReview', 'note'],
  worktree: ['summary', 'branch', 'note'],
  'pull-request': ['title', 'body', 'reviewComment'],
  checks: ['summary', 'note'],
  tests: ['plan', 'summary', 'note'],
  workspace: ['summary', 'note']
};

function issue(path: string, message: string): SchemaIssue {
  return { path, message };
}

export function isPlainObject(value: unknown): value is Record<string, unknown> {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) return false;
  return Object.getPrototypeOf(value) === Object.prototype;
}

function hasAllowedKeys(value: Record<string, unknown>, keys: readonly string[], path: string): SchemaIssue[] {
  const allowed = new Set(keys);
  return Object.keys(value)
    .filter((key) => !allowed.has(key))
    .map((key) => issue(`${path}.${key}`, 'unknown key'));
}

function serializedSize(value: unknown): number {
  try {
    return JSON.stringify(value).length;
  } catch {
    return Number.POSITIVE_INFINITY;
  }
}

/** Reject obvious command/script fragments in values that must stay typed. */
export function containsShellFragment(value: string): boolean {
  return /[`]|\$\(|&&|\|\||(?:^|[\n\r])\s*(?:sudo|rm|git|npm|pnpm|cargo|bash|zsh|sh)\b|;\s*(?:sudo|rm|git|npm|pnpm|cargo|bash|zsh|sh)\b/i.test(value);
}

function validateTarget(value: unknown, path: string): SchemaIssue[] {
  if (!isPlainObject(value)) return [issue(path, 'target must be a plain object')];
  const issues = hasAllowedKeys(value, ['surface', 'id', 'kind', 'expectedValueHash'], path);
  if (typeof value.surface !== 'string' || !surfaceSet.has(value.surface)) issues.push(issue(`${path}.surface`, 'unknown surface'));
  if (typeof value.id !== 'string' || value.id.length === 0 || value.id.length > 256) issues.push(issue(`${path}.id`, 'target id must be bounded'));
  if (value.kind !== undefined && (typeof value.kind !== 'string' || value.kind.length > 128 || containsShellFragment(value.kind))) issues.push(issue(`${path}.kind`, 'invalid target kind'));
  if (value.expectedValueHash !== undefined && (typeof value.expectedValueHash !== 'string' || value.expectedValueHash.length > 256)) issues.push(issue(`${path}.expectedValueHash`, 'invalid expected-value hash'));
  return issues;
}

export function validateAssistanceContext(value: unknown): SchemaResult<AssistanceContext> {
  if (!isPlainObject(value)) return { ok: false, issues: [issue('$', 'context must be a plain object')] };
  const issues = hasAllowedKeys(
    value,
    ['ownedId', 'generation', 'surface', 'target', 'facts', 'factsHash', 'boundedFacts', 'capturedAt', 'expiresAt'],
    '$'
  );
  if (serializedSize(value) > MAX_SERIALIZED_BYTES) issues.push(issue('$', 'context is oversized'));
  if (typeof value.ownedId !== 'string' || value.ownedId.length === 0 || value.ownedId.length > 256) issues.push(issue('$.ownedId', 'owner id must be bounded'));
  if (!Number.isInteger(value.generation) || (value.generation as number) < 0) issues.push(issue('$.generation', 'generation must be a non-negative integer'));
  if (typeof value.surface !== 'string' || !surfaceSet.has(value.surface)) issues.push(issue('$.surface', 'unknown surface'));
  issues.push(...validateTarget(value.target, '$.target'));
  if (!Array.isArray(value.facts) || value.facts.length > MAX_FACTS) issues.push(issue('$.facts', 'facts must be a bounded array'));
  else {
    for (const [index, fact] of value.facts.entries()) {
      if (!isPlainObject(fact)) {
        issues.push(issue(`$.facts[${index}]`, 'fact must be a plain object'));
        continue;
      }
      issues.push(...hasAllowedKeys(fact, ['id', 'hash', 'source'], `$.facts[${index}]`));
      if (typeof fact.id !== 'string' || fact.id.length === 0 || fact.id.length > 128 || containsShellFragment(fact.id)) issues.push(issue(`$.facts[${index}].id`, 'invalid fact id'));
      if (typeof fact.hash !== 'string' || fact.hash.length === 0 || fact.hash.length > 256) issues.push(issue(`$.facts[${index}].hash`, 'invalid fact hash'));
      if (fact.source !== undefined && (typeof fact.source !== 'string' || !surfaceSet.has(fact.source))) issues.push(issue(`$.facts[${index}].source`, 'invalid fact source'));
    }
  }
  if (typeof value.factsHash !== 'string' || value.factsHash.length === 0 || value.factsHash.length > 256) issues.push(issue('$.factsHash', 'invalid facts hash'));
  if (!isPlainObject(value.boundedFacts)) issues.push(issue('$.boundedFacts', 'bounded facts must be a plain object'));
  else {
    for (const [key, text] of Object.entries(value.boundedFacts)) {
      if (key.length > 128 || !/^[a-zA-Z0-9._:-]+$/.test(key)) issues.push(issue(`$.boundedFacts.${key}`, 'invalid bounded fact key'));
      if (typeof text !== 'string' || text.length > MAX_TEXT_BYTES || !text.startsWith('[') || containsShellFragment(text)) issues.push(issue(`$.boundedFacts.${key}`, 'bounded fact text is invalid'));
    }
  }
  if (!Number.isFinite(value.capturedAt) || !Number.isFinite(value.expiresAt) || (value.expiresAt as number) < (value.capturedAt as number)) issues.push(issue('$.expiresAt', 'invalid context lifetime'));
  return issues.length ? { ok: false, issues } : { ok: true, value: value as unknown as AssistanceContext, issues: [] };
}

export function isAllowedAssistanceField(surface: string, field: string): boolean {
  return (allowedFields[surface] ?? []).includes(field);
}

export function validateAssistanceFieldPatch(value: unknown): SchemaResult<AssistanceFieldPatch> {
  if (!isPlainObject(value)) return { ok: false, issues: [issue('$', 'patch must be a plain object')] };
  const issues = hasAllowedKeys(value, ['id', 'surface', 'field', 'value', 'expectedValueHash', 'targetId'], '$');
  if (serializedSize(value) > MAX_SERIALIZED_BYTES) issues.push(issue('$', 'patch is oversized'));
  if (typeof value.id !== 'string' || value.id.length === 0 || value.id.length > 128) issues.push(issue('$.id', 'patch id must be bounded'));
  if (typeof value.surface !== 'string' || !surfaceSet.has(value.surface)) issues.push(issue('$.surface', 'unknown surface'));
  if (typeof value.field !== 'string' || value.field.length === 0 || !isAllowedAssistanceField(String(value.surface), value.field)) issues.push(issue('$.field', 'field is not in the surface allow-list'));
  if (!(typeof value.value === 'string' || typeof value.value === 'number' || typeof value.value === 'boolean' || value.value === null)) issues.push(issue('$.value', 'patch value must remain typed'));
  if (typeof value.value === 'string' && (value.value.length > MAX_TEXT_BYTES || containsShellFragment(value.value))) issues.push(issue('$.value', 'patch value is unsafe or oversized'));
  if (typeof value.expectedValueHash !== 'string' || value.expectedValueHash.length === 0 || value.expectedValueHash.length > 256) issues.push(issue('$.expectedValueHash', 'expected-value hash is required'));
  if (value.targetId !== undefined && (typeof value.targetId !== 'string' || value.targetId.length > 256)) issues.push(issue('$.targetId', 'target id must be bounded'));
  return issues.length ? { ok: false, issues } : { ok: true, value: value as unknown as AssistanceFieldPatch, issues: [] };
}

export function assertAssistanceFieldPatch(value: unknown): AssistanceFieldPatch {
  const result = validateAssistanceFieldPatch(value);
  if (!result.ok) throw new Error(`Invalid assistance patch: ${result.issues.map((item) => `${item.path} ${item.message}`).join('; ')}`);
  return result.value as AssistanceFieldPatch;
}

export function validateAssistanceProposal(value: unknown): SchemaResult<AssistanceProposal> {
  if (!isPlainObject(value)) return { ok: false, issues: [issue('$', 'proposal must be a plain object')] };
  const issues = hasAllowedKeys(value, ['requestId', 'recipeId', 'recipeVersion', 'ownedId', 'generation', 'context', 'outputHash', 'provenance', 'confidence', 'patches', 'selectedPatchIds', 'createdAt', 'expiresAt', 'status'], '$');
  if (serializedSize(value) > MAX_SERIALIZED_BYTES) issues.push(issue('$', 'proposal is oversized'));
  if (typeof value.requestId !== 'string' || value.requestId.length === 0 || value.requestId.length > 256) issues.push(issue('$.requestId', 'request id must be bounded'));
  if (typeof value.recipeId !== 'string' || !recipeSet.has(value.recipeId)) issues.push(issue('$.recipeId', 'unknown assistance recipe'));
  if (!Number.isInteger(value.recipeVersion) || (value.recipeVersion as number) < 1) issues.push(issue('$.recipeVersion', 'recipe version must be positive'));
  if (typeof value.ownedId !== 'string' || value.ownedId.length === 0 || value.ownedId.length > 256) issues.push(issue('$.ownedId', 'owner id must be bounded'));
  if (!Number.isInteger(value.generation) || (value.generation as number) < 0) issues.push(issue('$.generation', 'generation must be a non-negative integer'));
  const context = validateAssistanceContext(value.context);
  if (!context.ok) issues.push(...context.issues.map((item) => ({ ...item, path: `$.context${item.path.slice(1)}` })));
  if (typeof value.outputHash !== 'string' || value.outputHash.length === 0 || value.outputHash.length > 256) issues.push(issue('$.outputHash', 'output hash is required'));
  if (value.provenance !== 'provider-native' && value.provenance !== 'workflow') issues.push(issue('$.provenance', 'unknown provenance'));
  if (typeof value.confidence !== 'number' || !Number.isFinite(value.confidence) || value.confidence < 0 || value.confidence > 1) issues.push(issue('$.confidence', 'confidence must be between zero and one'));
  if (!Array.isArray(value.patches) || value.patches.length > MAX_PATCHES) issues.push(issue('$.patches', 'patches must be bounded'));
  else value.patches.forEach((patch, index) => {
    const result = validateAssistanceFieldPatch(patch);
    if (!result.ok) issues.push(...result.issues.map((item) => ({ ...item, path: `$.patches[${index}]${item.path.slice(1)}` })));
  });
  if (!Array.isArray(value.selectedPatchIds) || value.selectedPatchIds.some((id) => typeof id !== 'string')) issues.push(issue('$.selectedPatchIds', 'selected patch ids must be strings'));
  if (!Number.isFinite(value.createdAt) || !Number.isFinite(value.expiresAt) || (value.expiresAt as number) < (value.createdAt as number)) issues.push(issue('$.expiresAt', 'invalid proposal lifetime'));
  if (!['proposed', 'dismissed', 'cancelled', 'applied', 'refused', 'failed'].includes(String(value.status))) issues.push(issue('$.status', 'invalid proposal status'));
  return issues.length ? { ok: false, issues } : { ok: true, value: value as unknown as AssistanceProposal, issues: [] };
}

export function assertAssistanceContext(value: unknown): AssistanceContext {
  const result = validateAssistanceContext(value);
  if (!result.ok) throw new Error(`Invalid assistance context: ${result.issues.map((item) => `${item.path} ${item.message}`).join('; ')}`);
  return result.value as AssistanceContext;
}

export function assertAssistanceProposal(value: unknown): AssistanceProposal {
  const result = validateAssistanceProposal(value);
  if (!result.ok) throw new Error(`Invalid assistance proposal: ${result.issues.map((item) => `${item.path} ${item.message}`).join('; ')}`);
  return result.value as AssistanceProposal;
}

export function assertAssistanceTarget(value: unknown): AssistanceTargetRef {
  const issues = validateTarget(value, '$');
  if (issues.length) throw new Error(`Invalid assistance target: ${issues.map((item) => `${item.path} ${item.message}`).join('; ')}`);
  return value as AssistanceTargetRef;
}
