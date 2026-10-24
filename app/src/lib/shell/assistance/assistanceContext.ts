import type {
  AssistanceContext,
  AssistanceFactRef,
  AssistanceSurface,
  AssistanceTargetRef
} from './assistanceTypes.ts';
import { assertAssistanceTarget } from './assistanceSchemas.ts';

const DEFAULT_TTL_MS = 5 * 60 * 1000;
const MAX_FACTS = 32;
const MAX_TEXT_LENGTH = 2_048;
const EXCLUDED_KEY = /secret|token|password|credential|authorization|cookie|environment|env|prompt|hidden.?dom|unrestricted.?file|private.?key/i;
const SENSITIVE_VALUE = /(api[_-]?key|token|secret|password|authorization|cookie)\s*[:=]\s*[^,;]+/gi;

export interface AssistanceFactInput {
  id: string;
  value: unknown;
  source?: AssistanceSurface;
}

export interface AssistanceCaptureInput {
  ownedId: string;
  generation: number;
  surface: AssistanceSurface;
  target: AssistanceTargetRef;
  facts?: Record<string, unknown> | readonly AssistanceFactInput[];
  ttlMs?: number;
}

function stableSerialize(value: unknown): string {
  if (value === null || typeof value !== 'object') return JSON.stringify(value);
  if (Array.isArray(value)) return `[${value.map(stableSerialize).join(',')}]`;
  const object = value as Record<string, unknown>;
  return `{${Object.keys(object).sort().map((key) => `${JSON.stringify(key)}:${stableSerialize(object[key])}`).join(',')}}`;
}

/** A synchronous, deterministic hash that works in the browser and in Node tests. */
export function hashAssistanceFacts(value: unknown): string {
  const input = stableSerialize(value);
  let hash = 0x811c9dc5;
  for (let index = 0; index < input.length; index += 1) {
    hash ^= input.charCodeAt(index);
    hash = Math.imul(hash, 0x01000193);
  }
  return `fnv1a-${(hash >>> 0).toString(16).padStart(8, '0')}`;
}

function stringifyFact(value: unknown): string {
  if (typeof value === 'string') return value;
  try {
    return JSON.stringify(value);
  } catch {
    return '[unavailable]';
  }
}

function redactFactText(value: string): string {
  return value.replace(SENSITIVE_VALUE, '$1=[redacted]');
}

function boundedDelimitedText(surface: AssistanceSurface, id: string, value: unknown): string {
  const clean = redactFactText(stringifyFact(value)).slice(0, MAX_TEXT_LENGTH);
  return `[${surface}:${id}]\n${clean}`;
}

function normalizedFacts(input: AssistanceCaptureInput): Array<{ id: string; source?: AssistanceSurface; text: string }> {
  const facts: Array<{ id: string; source?: AssistanceSurface; value: unknown }> = Array.isArray(input.facts)
    ? input.facts.map((fact) => ({ id: fact.id, source: fact.source, value: fact.value }))
    : Object.entries(input.facts ?? {}).map(([id, value]) => ({ id, value }));
  return facts
    .filter((fact) => fact.id && !EXCLUDED_KEY.test(fact.id))
    .slice(0, MAX_FACTS)
    .map((fact) => ({
      id: fact.id.slice(0, 128),
      source: fact.source,
      text: boundedDelimitedText(fact.source ?? input.surface, fact.id, fact.value)
    }));
}

/** Capture only the bounded snapshot supplied by an existing typed store. */
export function captureAssistanceContext(
  input: AssistanceCaptureInput,
  options: { now?: number; ttlMs?: number } = {}
): AssistanceContext {
  const now = options.now ?? Date.now();
  const facts = normalizedFacts(input);
  const factRefs: AssistanceFactRef[] = facts.map((fact) => ({
    id: fact.id,
    hash: hashAssistanceFacts(fact.text),
    ...(fact.source ? { source: fact.source } : {})
  }));
  const boundedFacts = Object.fromEntries(facts.map((fact) => [fact.id, fact.text]));
  return {
    ownedId: input.ownedId,
    generation: input.generation,
    surface: input.surface,
    target: assertAssistanceTarget(input.target),
    facts: factRefs,
    factsHash: hashAssistanceFacts(factRefs),
    boundedFacts,
    capturedAt: now,
    expiresAt: now + Math.max(1, options.ttlMs ?? input.ttlMs ?? DEFAULT_TTL_MS)
  };
}

export function revalidateAssistanceTarget(
  context: AssistanceContext,
  target: AssistanceTargetRef
): boolean {
  return context.target.surface === target.surface
    && context.target.id === target.id
    && (context.target.kind ?? null) === (target.kind ?? null)
    && (context.target.expectedValueHash ?? null) === (target.expectedValueHash ?? null);
}

/** Compare the identity and fact hashes before an adapter is allowed to apply. */
export function isAssistanceContextCurrent(
  context: AssistanceContext,
  current: Pick<AssistanceContext, 'ownedId' | 'generation' | 'surface' | 'target' | 'factsHash'>
): boolean {
  return context.ownedId === current.ownedId
    && context.generation === current.generation
    && context.surface === current.surface
    && context.factsHash === current.factsHash
    && revalidateAssistanceTarget(context, current.target);
}

export function isAssistanceContextExpired(context: AssistanceContext, now = Date.now()): boolean {
  return now >= context.expiresAt;
}
