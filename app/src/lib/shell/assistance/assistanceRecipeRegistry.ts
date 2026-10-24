import {
  ASSISTANCE_RECIPE_IDS,
  type AssistanceRecipeDefinition,
  type AssistanceRecipeId
} from './assistanceTypes.ts';
import { isAllowedAssistanceField } from './assistanceSchemas.ts';

const RECIPE_DEFINITIONS: readonly AssistanceRecipeDefinition[] = [
  { id: 'pr-draft', version: 1, surface: 'pull-request', mutation: 'proposal', label: 'Draft a pull request', allowedFields: ['title', 'body'] },
  { id: 'pr-review', version: 1, surface: 'pull-request', mutation: 'none', label: 'Review a pull request', allowedFields: ['reviewComment'] },
  { id: 'review-comments', version: 1, surface: 'pull-request', mutation: 'proposal', label: 'Draft review comments', allowedFields: ['reviewComment'] },
  { id: 'checks', version: 1, surface: 'checks', mutation: 'none', label: 'Explain checks', allowedFields: ['summary', 'note'] },
  { id: 'conflicts', version: 1, surface: 'git', mutation: 'proposal', label: 'Explain conflicts', allowedFields: ['summary', 'note'] },
  { id: 'commit', version: 1, surface: 'git', mutation: 'proposal', label: 'Draft a commit message', allowedFields: ['commitMessage'] },
  { id: 'diff-explanation', version: 1, surface: 'diff', mutation: 'none', label: 'Explain this diff', allowedFields: ['summary', 'reviewComment'] },
  { id: 'file-explanation', version: 1, surface: 'editor', mutation: 'none', label: 'Explain this file', allowedFields: ['summary'] },
  { id: 'problem-explanation', version: 1, surface: 'problems', mutation: 'none', label: 'Explain this problem', allowedFields: ['explanation', 'suggestedFix'] },
  { id: 'run-config-generate', version: 1, surface: 'run-configuration', mutation: 'proposal', label: 'Suggest a run configuration', allowedFields: ['name', 'cwd', 'script'] },
  { id: 'run-config-review', version: 1, surface: 'run-configuration', mutation: 'none', label: 'Review this run configuration', allowedFields: ['summary', 'note'] },
  { id: 'workspace-summary', version: 1, surface: 'workspace', mutation: 'none', label: 'Summarize the workspace', allowedFields: ['summary'] },
  { id: 'browser-feedback', version: 1, surface: 'browser', mutation: 'proposal', label: 'Draft browser feedback', allowedFields: ['note', 'intent', 'selector', 'accessibleName'] },
  { id: 'form-suggestion', version: 1, surface: 'form', mutation: 'proposal', label: 'Suggest a form value', allowedFields: ['label', 'description', 'value', 'note'] },
  { id: 'save-spec-review', version: 1, surface: 'save', mutation: 'none', label: 'Review before saving', allowedFields: ['summary', 'specReview', 'note'] },
  { id: 'worktree-cleanup', version: 1, surface: 'worktree', mutation: 'proposal', label: 'Plan worktree cleanup', allowedFields: ['summary', 'branch', 'note'] },
  { id: 'test-plan', version: 1, surface: 'tests', mutation: 'proposal', label: 'Draft a test plan', allowedFields: ['plan', 'summary', 'note'] }
];

function isRecipeId(value: string): value is AssistanceRecipeId {
  return (ASSISTANCE_RECIPE_IDS as readonly string[]).includes(value);
}

function validateDefinition(definition: AssistanceRecipeDefinition): void {
  if (!isRecipeId(definition.id)) throw new Error(`unknown assistance recipe: ${definition.id}`);
  if (!Number.isInteger(definition.version) || definition.version < 1) throw new Error(`Invalid version for assistance recipe ${definition.id}`);
  if (definition.mutation !== 'none' && definition.mutation !== 'proposal') throw new Error(`Invalid mutation policy for assistance recipe ${definition.id}`);
  if (!definition.label.trim() || definition.label.length > 160) throw new Error(`Invalid label for assistance recipe ${definition.id}`);
  for (const field of definition.allowedFields) {
    if (!isAllowedAssistanceField(definition.surface, field)) throw new Error(`Recipe ${definition.id} contains a field outside the ${definition.surface} allow-list`);
  }
}

export class AssistanceRecipeRegistry {
  private readonly recipes = new Map<AssistanceRecipeId, AssistanceRecipeDefinition>();

  constructor(initial: readonly AssistanceRecipeDefinition[] = []) {
    for (const recipe of initial) this.register(recipe);
  }

  register(definition: AssistanceRecipeDefinition): void {
    validateDefinition(definition);
    if (this.recipes.has(definition.id)) throw new Error(`Assistance recipe ${definition.id} is already registered`);
    this.recipes.set(definition.id, Object.freeze({ ...definition, allowedFields: Object.freeze([...definition.allowedFields]) }));
  }

  get(id: AssistanceRecipeId): AssistanceRecipeDefinition | undefined {
    return this.recipes.get(id);
  }

  list(): AssistanceRecipeDefinition[] {
    return [...this.recipes.values()].map((recipe) => ({ ...recipe, allowedFields: [...recipe.allowedFields] }));
  }
}

export function createAssistanceRecipeRegistry(): AssistanceRecipeRegistry {
  return new AssistanceRecipeRegistry(RECIPE_DEFINITIONS);
}

export const assistanceRecipeRegistry = createAssistanceRecipeRegistry();

export function registerAssistanceRecipe(definition: AssistanceRecipeDefinition): void {
  assistanceRecipeRegistry.register(definition);
}

export function getAssistanceRecipe(id: AssistanceRecipeId): AssistanceRecipeDefinition | undefined {
  return assistanceRecipeRegistry.get(id);
}

export { ASSISTANCE_RECIPE_IDS } from './assistanceTypes.ts';
