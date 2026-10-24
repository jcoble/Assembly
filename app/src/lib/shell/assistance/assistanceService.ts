import type {
  AssistanceContext,
  AssistanceFieldPatch,
  AssistanceProposal,
  AssistanceRecipeId,
  AssistanceSurface
} from './assistanceTypes.ts';
import { isAssistanceContextCurrent, isAssistanceContextExpired, hashAssistanceFacts } from './assistanceContext.ts';
import {
  assertAssistanceContext,
  assertAssistanceFieldPatch,
  assertAssistanceProposal,
  validateAssistanceProposal
} from './assistanceSchemas.ts';
import { assistanceRecipeRegistry } from './assistanceRecipeRegistry.ts';
import { createAssistanceAuditLog, type AssistanceAuditLog } from './assistanceAudit.ts';

export interface AssistanceStructuredOutputRequest {
  requestId: string;
  recipeId: AssistanceRecipeId;
  recipeVersion: number;
  context: AssistanceContext;
}

export interface AssistanceStructuredOutputResponse {
  provenance: 'provider-native' | 'workflow';
  confidence: number;
  outputHash?: string;
  patches: readonly unknown[];
}

export interface AssistanceApplyResult {
  status: 'applied' | 'refused' | 'outcome-unknown';
  message?: string;
}

export interface AssistanceSurfaceAdapter {
  applyPatches(
    patches: readonly AssistanceFieldPatch[],
    context: AssistanceContext
  ): AssistanceApplyResult | Promise<AssistanceApplyResult>;
}

export interface AssistanceServiceDependencies {
  structuredOutput:
    | ((request: AssistanceStructuredOutputRequest) => AssistanceStructuredOutputResponse | Promise<AssistanceStructuredOutputResponse>)
    | { request(request: AssistanceStructuredOutputRequest): AssistanceStructuredOutputResponse | Promise<AssistanceStructuredOutputResponse> };
  adapters: Partial<Record<AssistanceSurface, AssistanceSurfaceAdapter>>;
  audit?: AssistanceAuditLog;
  now?: () => number;
  requestId?: () => string;
}

export interface AssistanceRequestInput {
  recipeId: AssistanceRecipeId;
  context: AssistanceContext;
}

export interface AssistanceApplyInput {
  proposal: AssistanceProposal;
  selectedPatchIds: readonly string[];
  currentContext: Pick<AssistanceContext, 'ownedId' | 'generation' | 'surface' | 'target' | 'factsHash'>;
  expectedOutputHash?: string;
}

export class AssistanceRequestError extends Error {
  readonly status: 'cancelled' | 'stale' | 'refused' | 'failed';

  constructor(message: string, status: 'cancelled' | 'stale' | 'refused' | 'failed') {
    super(message);
    this.status = status;
    this.name = 'AssistanceRequestError';
  }
}

function stableSerialize(value: unknown): string {
  if (value === null || typeof value !== 'object') return JSON.stringify(value);
  if (Array.isArray(value)) return `[${value.map(stableSerialize).join(',')}]`;
  const object = value as Record<string, unknown>;
  return `{${Object.keys(object).sort().map((key) => `${JSON.stringify(key)}:${stableSerialize(object[key])}`).join(',')}}`;
}

function makeOutputHash(response: AssistanceStructuredOutputResponse): string {
  return hashAssistanceFacts(stableSerialize({ provenance: response.provenance, confidence: response.confidence, patches: response.patches }));
}

function createRequestId(next: () => string, counter: { value: number }): string {
  return next?.() ?? `assistance-${counter.value++}`;
}

async function adapterRequest(
  structuredOutput: AssistanceServiceDependencies['structuredOutput'],
  request: AssistanceStructuredOutputRequest
): Promise<AssistanceStructuredOutputResponse> {
  const response = await (typeof structuredOutput === 'function'
    ? structuredOutput(request)
    : structuredOutput.request(request));
  return response;
}

function auditRequest(
  audit: AssistanceAuditLog,
  status: Parameters<AssistanceAuditLog['record']>[0]['status'],
  proposal: Pick<AssistanceProposal, 'requestId' | 'recipeId' | 'ownedId' | 'generation' | 'context'>,
  detail?: string
): void {
  audit.record({
    requestId: proposal.requestId,
    recipeId: proposal.recipeId,
    status,
    ownedId: proposal.ownedId,
    generation: proposal.generation,
    surface: proposal.context.surface,
    ...(detail ? { detail } : {})
  });
}

function normalizeOutput(
  response: AssistanceStructuredOutputResponse,
  request: AssistanceStructuredOutputRequest,
  allowedFields: readonly string[],
  mutation: 'none' | 'proposal',
  now: number
): AssistanceProposal {
  if (!response || typeof response !== 'object') throw new AssistanceRequestError('Structured assistance output is not an object', 'failed');
  const patches = response.patches.map((patch) => assertAssistanceFieldPatch(patch));
  if (mutation === 'none' && patches.length > 0) {
    throw new AssistanceRequestError('This assistance recipe is read-only and cannot propose field patches', 'failed');
  }
  if (patches.some((patch) => !allowedFields.includes(patch.field))) {
    throw new AssistanceRequestError('Structured assistance output contains a field outside the recipe allow-list', 'failed');
  }
  const outputHash = makeOutputHash({ ...response, patches });
  if (response.outputHash !== undefined && response.outputHash !== outputHash) {
    throw new AssistanceRequestError('Structured assistance output hash did not match its contents', 'failed');
  }
  const proposal: AssistanceProposal = {
    requestId: request.requestId,
    recipeId: request.recipeId,
    recipeVersion: request.recipeVersion,
    ownedId: request.context.ownedId,
    generation: request.context.generation,
    context: request.context,
    outputHash,
    provenance: response.provenance,
    confidence: response.confidence,
    patches,
    selectedPatchIds: [],
    createdAt: now,
    expiresAt: Math.min(request.context.expiresAt, now + 5 * 60 * 1000),
    status: 'proposed'
  };
  const validation = validateAssistanceProposal(proposal);
  if (!validation.ok) throw new AssistanceRequestError(`Structured assistance output was refused: ${validation.issues.map((item) => item.message).join('; ')}`, 'failed');
  return assertAssistanceProposal(proposal);
}

export interface AssistanceService {
  requestAssistance(input: AssistanceRequestInput): Promise<AssistanceProposal>;
  applySelectedAssistancePatches(input: AssistanceApplyInput): Promise<AssistanceApplyResult>;
  cancelAssistanceRequest(requestId: string): boolean;
  audit: AssistanceAuditLog;
}

export function createAssistanceService(dependencies: AssistanceServiceDependencies): AssistanceService {
  const now = dependencies.now ?? Date.now;
  const audit = dependencies.audit ?? createAssistanceAuditLog([], { now });
  const counter = { value: 1 };
  const pending = new Map<string, { cancelled: boolean; proposal?: AssistanceProposal }>();
  const requestIdFactory = dependencies.requestId ?? (() => `assistance-${counter.value++}`);

  async function requestAssistance(input: AssistanceRequestInput): Promise<AssistanceProposal> {
    const recipe = assistanceRecipeRegistry.get(input.recipeId);
    if (!recipe) throw new AssistanceRequestError(`Unknown assistance recipe: ${input.recipeId}`, 'refused');
    const context = assertAssistanceContext(input.context);
    const requestId = createRequestId(requestIdFactory, counter);
    const request = { requestId, recipeId: input.recipeId, recipeVersion: recipe.version, context } satisfies AssistanceStructuredOutputRequest;
    const pendingRequest: { cancelled: boolean; proposal?: AssistanceProposal } = { cancelled: false };
    pending.set(requestId, pendingRequest);
    const auditShape = { requestId, recipeId: input.recipeId, ownedId: context.ownedId, generation: context.generation, context };
    auditRequest(audit, 'requested', auditShape);
    try {
      if (pendingRequest.cancelled) throw new AssistanceRequestError('Assistance request was cancelled', 'cancelled');
      const response = await adapterRequest(dependencies.structuredOutput, request);
      if (pendingRequest.cancelled) {
        auditRequest(audit, 'dismissed', auditShape, 'Cancelled before proposal was accepted');
        throw new AssistanceRequestError('Assistance request was cancelled', 'cancelled');
      }
      const proposal = normalizeOutput(response, request, recipe.allowedFields, recipe.mutation, now());
      pendingRequest.proposal = proposal;
      auditRequest(audit, 'proposed', proposal);
      return proposal;
    } catch (error) {
      if (error instanceof AssistanceRequestError && error.status === 'cancelled') throw error;
      auditRequest(audit, 'failed', auditShape, error instanceof Error ? error.message : String(error));
      throw error;
    }
  }

  async function applySelectedAssistancePatches(input: AssistanceApplyInput): Promise<AssistanceApplyResult> {
    const proposal = assertAssistanceProposal(input.proposal);
    const request = pending.get(proposal.requestId);
    if (request?.cancelled || proposal.status === 'cancelled' || proposal.status === 'dismissed') {
      auditRequest(audit, 'refused', proposal, 'Proposal was cancelled or dismissed');
      throw new AssistanceRequestError('Assistance proposal was cancelled or dismissed', 'cancelled');
    }
    if (now() >= proposal.expiresAt || isAssistanceContextExpired(proposal.context, now())) {
      proposal.status = 'refused';
      auditRequest(audit, 'stale', proposal, 'Proposal expired');
      throw new AssistanceRequestError('Assistance proposal has expired', 'stale');
    }
    if (input.expectedOutputHash !== undefined && input.expectedOutputHash !== proposal.outputHash) {
      proposal.status = 'refused';
      auditRequest(audit, 'refused', proposal, 'Structured output changed before apply');
      throw new AssistanceRequestError('Structured assistance output is stale', 'stale');
    }
    if (!isAssistanceContextCurrent(proposal.context, input.currentContext)) {
      proposal.status = 'refused';
      auditRequest(audit, 'stale', proposal, 'Owner, generation, target, or fact hash changed');
      throw new AssistanceRequestError('Assistance proposal is stale for the current target', 'stale');
    }
    const selected = new Set(input.selectedPatchIds);
    const patches = proposal.patches.filter((patch) => selected.has(patch.id));
    if (patches.length !== selected.size) {
      proposal.status = 'refused';
      auditRequest(audit, 'refused', proposal, 'A selected patch was not in the proposal');
      throw new AssistanceRequestError('Selected assistance patches are not valid', 'refused');
    }
    for (const patch of patches) {
      assertAssistanceFieldPatch(patch);
      if (patch.surface !== input.currentContext.surface || (patch.targetId && patch.targetId !== input.currentContext.target.id)) {
        proposal.status = 'refused';
        auditRequest(audit, 'refused', proposal, 'Patch target does not match the current surface');
        throw new AssistanceRequestError('Selected assistance patch targets a different surface', 'refused');
      }
      if (patch.expectedValueHash !== (input.currentContext.target.expectedValueHash ?? '')) {
        proposal.status = 'refused';
        auditRequest(audit, 'refused', proposal, 'Expected value changed');
        throw new AssistanceRequestError('Expected value changed before apply', 'refused');
      }
    }
    const adapter = dependencies.adapters[input.currentContext.surface];
    if (!adapter) {
      proposal.status = 'refused';
      auditRequest(audit, 'refused', proposal, 'No typed adapter was supplied for this surface');
      throw new AssistanceRequestError('No typed adapter is available for this surface', 'refused');
    }
    auditRequest(audit, 'apply-started', proposal);
    try {
      const result = await adapter.applyPatches(patches, proposal.context);
      if (result.status === 'applied') {
        proposal.status = 'applied';
        auditRequest(audit, 'applied', proposal, result.message);
      } else if (result.status === 'outcome-unknown') {
        proposal.status = 'failed';
        auditRequest(audit, 'outcome-unknown', proposal, result.message);
      } else {
        proposal.status = 'refused';
        auditRequest(audit, 'refused', proposal, result.message);
      }
      return result;
    } catch (error) {
      proposal.status = 'failed';
      auditRequest(audit, 'failed', proposal, error instanceof Error ? error.message : String(error));
      throw error;
    }
  }

  function cancelAssistanceRequest(requestId: string): boolean {
    const request = pending.get(requestId);
    if (!request || request.cancelled) return false;
    request.cancelled = true;
    if (request.proposal) {
      request.proposal.status = 'dismissed';
      auditRequest(audit, 'dismissed', request.proposal, 'Dismissed by the user');
    }
    return true;
  }

  return { requestAssistance, applySelectedAssistancePatches, cancelAssistanceRequest, audit };
}

export async function requestAssistance(
  input: AssistanceRequestInput,
  dependencies: AssistanceServiceDependencies
): Promise<AssistanceProposal> {
  const proposal = await createAssistanceService(dependencies).requestAssistance(input);
  return proposal;
}

export async function applySelectedAssistancePatches(
  input: AssistanceApplyInput,
  dependencies: AssistanceServiceDependencies
): Promise<AssistanceApplyResult> {
  const result = await createAssistanceService(dependencies).applySelectedAssistancePatches(input);
  return result;
}

export function cancelAssistanceRequest(
  requestId: string,
  service: AssistanceService
): boolean {
  return service.cancelAssistanceRequest(requestId);
}
