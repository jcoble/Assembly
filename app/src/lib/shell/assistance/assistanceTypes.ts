/**
 * Shared, provider-neutral contracts for product assistance.
 *
 * These values describe a bounded proposal. They are not an execution API and
 * deliberately contain no Tauri, filesystem, shell, or provider objects.
 */

export const ASSISTANCE_RECIPE_IDS = [
  'pr-draft',
  'pr-review',
  'review-comments',
  'checks',
  'conflicts',
  'commit',
  'diff-explanation',
  'file-explanation',
  'problem-explanation',
  'run-config-generate',
  'run-config-review',
  'workspace-summary',
  'browser-feedback',
  'form-suggestion',
  'save-spec-review',
  'worktree-cleanup',
  'test-plan'
] as const;

export type AssistanceRecipeId = (typeof ASSISTANCE_RECIPE_IDS)[number];

export const ASSISTANCE_SURFACES = [
  'conversation',
  'editor',
  'git',
  'diff',
  'problems',
  'run-configuration',
  'browser',
  'context',
  'form',
  'save',
  'worktree',
  'pull-request',
  'checks',
  'tests',
  'workspace'
] as const;

export type AssistanceSurface = (typeof ASSISTANCE_SURFACES)[number];
export type AssistanceMutationPolicy = 'none' | 'proposal';
export type AssistanceProvenance = 'provider-native' | 'workflow';

/** Stable identity for the object a proposal is allowed to discuss. */
export interface AssistanceTargetRef {
  surface: AssistanceSurface;
  id: string;
  kind?: string;
  expectedValueHash?: string;
}

/** A fact is represented by an identity and a hash, never by an unbounded blob. */
export interface AssistanceFactRef {
  id: string;
  hash: string;
  source?: AssistanceSurface;
}

/**
 * The synchronous snapshot sent to the structured-output adapter. `boundedFacts`
 * contains already-delimited text and is safe to treat as untrusted input.
 */
export interface AssistanceContext {
  ownedId: string;
  generation: number;
  surface: AssistanceSurface;
  target: AssistanceTargetRef;
  facts: AssistanceFactRef[];
  factsHash: string;
  boundedFacts: Record<string, string>;
  capturedAt: number;
  expiresAt: number;
}

/** One typed, allow-listed edit proposed for an existing product adapter. */
export interface AssistanceFieldPatch {
  id: string;
  surface: AssistanceSurface;
  field: string;
  value: string | number | boolean | null;
  expectedValueHash: string;
  targetId?: string;
}

export type AssistanceProposalStatus =
  | 'proposed'
  | 'dismissed'
  | 'cancelled'
  | 'applied'
  | 'refused'
  | 'failed';

/** An expiring, reviewable result. No proposal executes on construction. */
export interface AssistanceProposal {
  requestId: string;
  recipeId: AssistanceRecipeId;
  recipeVersion: number;
  ownedId: string;
  generation: number;
  context: AssistanceContext;
  outputHash: string;
  provenance: AssistanceProvenance;
  confidence: number;
  patches: AssistanceFieldPatch[];
  selectedPatchIds: string[];
  createdAt: number;
  expiresAt: number;
  status: AssistanceProposalStatus;
}

export interface AssistanceRecipeDefinition {
  id: AssistanceRecipeId;
  version: number;
  surface: AssistanceSurface;
  mutation: AssistanceMutationPolicy;
  label: string;
  allowedFields: readonly string[];
}
