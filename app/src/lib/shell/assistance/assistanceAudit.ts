import type { AssistanceRecipeId, AssistanceSurface } from './assistanceTypes.ts';

export const ASSISTANCE_AUDIT_STATUSES = [
  'requested',
  'proposed',
  'dismissed',
  'stale',
  'apply-started',
  'applied',
  'refused',
  'failed',
  'outcome-unknown'
] as const;

export type AssistanceAuditStatus = (typeof ASSISTANCE_AUDIT_STATUSES)[number];

export interface AssistanceAuditEvent {
  sequence: number;
  at: number;
  requestId: string;
  recipeId: AssistanceRecipeId;
  status: AssistanceAuditStatus;
  ownedId: string;
  generation: number;
  surface: AssistanceSurface;
  detail?: string;
}

export interface AssistanceAuditLog {
  record(input: Omit<AssistanceAuditEvent, 'sequence' | 'at'> & { at?: number }): AssistanceAuditEvent;
  entries(): AssistanceAuditEvent[];
  serialize(): string;
}

const MAX_DETAIL_LENGTH = 2_048;
const SECRET_PATTERN = /(api[_-]?key|token|secret|password|authorization|cookie)\s*[:=]\s*[^,;]+/gi;

export function redactAssistanceAuditText(value: string): string {
  return value.replace(SECRET_PATTERN, '$1=[redacted]').slice(0, MAX_DETAIL_LENGTH);
}

function validStatus(value: unknown): value is AssistanceAuditStatus {
  return typeof value === 'string' && (ASSISTANCE_AUDIT_STATUSES as readonly string[]).includes(value);
}

function cloneEntry(value: AssistanceAuditEvent): AssistanceAuditEvent {
  return { ...value };
}

export function createAssistanceAuditLog(
  initial: readonly AssistanceAuditEvent[] = [],
  options: { now?: () => number } = {}
): AssistanceAuditLog {
  const events = initial.map(cloneEntry);
  let nextSequence = events.reduce((max, event) => Math.max(max, event.sequence), 0) + 1;
  const now = options.now ?? Date.now;
  return {
    record(input) {
      if (!validStatus(input.status)) throw new Error(`Invalid assistance audit status: ${String(input.status)}`);
      if (input.detail && input.detail.length > MAX_DETAIL_LENGTH * 2) throw new Error('Audit detail is oversized; redact or bound it first');
      const event: AssistanceAuditEvent = {
        sequence: nextSequence++,
        at: input.at ?? now(),
        requestId: input.requestId,
        recipeId: input.recipeId,
        status: input.status,
        ownedId: input.ownedId,
        generation: input.generation,
        surface: input.surface,
        ...(input.detail ? { detail: redactAssistanceAuditText(input.detail) } : {})
      };
      events.push(event);
      return cloneEntry(event);
    },
    entries() {
      return events.map(cloneEntry);
    },
    serialize() {
      return JSON.stringify(events);
    }
  };
}

export function restoreAssistanceAuditLog(serialized: string): AssistanceAuditLog {
  let parsed: unknown;
  try {
    parsed = JSON.parse(serialized);
  } catch {
    throw new Error('Stored assistance audit is not valid JSON');
  }
  if (!Array.isArray(parsed)) throw new Error('Stored assistance audit must be an array');
  const entries: AssistanceAuditEvent[] = [];
  for (const [index, value] of parsed.entries()) {
    if (!value || typeof value !== 'object' || !validStatus((value as { status?: unknown }).status)) throw new Error(`Invalid stored assistance audit event ${index}`);
    const event = value as AssistanceAuditEvent;
    entries.push({ ...event, detail: event.detail ? redactAssistanceAuditText(event.detail) : undefined });
  }
  return createAssistanceAuditLog(entries);
}
