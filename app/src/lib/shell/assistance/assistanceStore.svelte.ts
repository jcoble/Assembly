import type { AssistanceProposal, AssistanceSurface } from './assistanceTypes.ts';

export type AssistancePresentationStatus = 'idle' | 'requesting' | 'proposed' | 'applying' | 'error';

export interface AssistancePresentationState {
  status: AssistancePresentationStatus;
  surface: AssistanceSurface | null;
  requestId: string | null;
  proposal: AssistanceProposal | null;
  error: string | null;
}

export const assistanceState = $state<AssistancePresentationState>({
  status: 'idle',
  surface: null,
  requestId: null,
  proposal: null,
  error: null
});

export function beginAssistanceRequest(surface: AssistanceSurface, requestId: string): void {
  Object.assign(assistanceState, { status: 'requesting', surface, requestId, proposal: null, error: null });
}

export function showAssistanceProposal(proposal: AssistanceProposal): void {
  Object.assign(assistanceState, { status: 'proposed', surface: proposal.context.surface, requestId: proposal.requestId, proposal, error: null });
}

export function beginAssistanceApply(): void {
  assistanceState.status = 'applying';
  assistanceState.error = null;
}

export function setAssistanceError(error: unknown): void {
  assistanceState.status = 'error';
  assistanceState.error = error instanceof Error ? error.message : String(error);
}

export function clearAssistance(): void {
  Object.assign(assistanceState, { status: 'idle', surface: null, requestId: null, proposal: null, error: null });
}

export function toggleAssistancePatch(patchId: string): void {
  const proposal = assistanceState.proposal;
  if (!proposal) return;
  const selected = new Set(proposal.selectedPatchIds);
  if (selected.has(patchId)) selected.delete(patchId);
  else selected.add(patchId);
  proposal.selectedPatchIds = [...selected];
}
