import assert from 'node:assert/strict';
import {
  ASSISTANCE_RECIPE_IDS,
  assistanceRecipeRegistry,
  createAssistanceRecipeRegistry
} from '../src/lib/shell/assistance/assistanceRecipeRegistry.ts';
import {
  assertAssistanceFieldPatch,
  validateAssistanceProposal
} from '../src/lib/shell/assistance/assistanceSchemas.ts';

assert.equal(new Set(ASSISTANCE_RECIPE_IDS).size, ASSISTANCE_RECIPE_IDS.length);
assert.equal(assistanceRecipeRegistry.list().length, ASSISTANCE_RECIPE_IDS.length);

for (const recipeId of ASSISTANCE_RECIPE_IDS) {
  const recipe = assistanceRecipeRegistry.get(recipeId);
  assert.ok(recipe, `recipe ${recipeId} is registered`);
  assert.equal(recipe.version, 1, `${recipeId} has one stable version`);
  assert.ok(recipe.mutation === 'none' || recipe.mutation === 'proposal');
  assert.equal('execute' in recipe, false, `${recipeId} has no direct execute path`);
}

const registry = createAssistanceRecipeRegistry();
const definition = {
  id: 'browser-feedback',
  version: 1,
  surface: 'browser',
  mutation: 'proposal',
  label: 'Browser feedback',
  allowedFields: ['note']
};
assert.throws(() => registry.register(definition), /already registered/);
assert.throws(
  () => registry.register({ ...definition, id: 'not-a-recipe' }),
  /unknown assistance recipe/
);
assert.throws(
  () => assertAssistanceFieldPatch({
    id: 'patch-1',
    surface: 'browser',
    field: 'not-allowed',
    value: 'safe',
    expectedValueHash: 'hash'
  }),
  /allow-list/
);

const baseProposal = {
  requestId: 'request-1',
  recipeId: 'browser-feedback',
  recipeVersion: 1,
  ownedId: 'owned-1',
  generation: 4,
  context: {
    ownedId: 'owned-1',
    generation: 4,
    surface: 'browser',
    target: { surface: 'browser', id: 'tab-1', expectedValueHash: 'target-hash' },
    facts: [{ id: 'draft', hash: 'facts-hash' }],
    factsHash: 'facts-hash',
    boundedFacts: { draft: '[browser:draft]\nsafe' },
    capturedAt: 1000,
    expiresAt: 5000
  },
  outputHash: 'output-hash',
  provenance: 'workflow',
  confidence: 0.8,
  patches: [],
  createdAt: 1000,
  expiresAt: 5000,
  selectedPatchIds: [],
  status: 'proposed'
};
assert.equal(validateAssistanceProposal(baseProposal).ok, true);
assert.equal(validateAssistanceProposal({ ...baseProposal, unknown: true }).ok, false);

console.log('assistanceRecipes: all tests passed');
