import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { captureAssistanceContext } from '../src/lib/shell/assistance/assistanceContext.ts';
import { createAssistanceAuditLog } from '../src/lib/shell/assistance/assistanceAudit.ts';
import { createAssistanceService } from '../src/lib/shell/assistance/assistanceService.ts';

const context = captureAssistanceContext({
  ownedId: 'owned-1',
  generation: 2,
  surface: 'browser',
  target: { surface: 'browser', id: 'tab-1', expectedValueHash: 'target-hash' },
  facts: { draft: 'current draft' }
}, { now: 1000 });

const audit = createAssistanceAuditLog();
const applied = [];
const service = createAssistanceService({
  now: () => 1000,
  audit,
  structuredOutput: async (request) => ({
    provenance: 'workflow',
    confidence: 0.9,
    patches: [{
      id: 'patch-note',
      surface: request.context.surface,
      field: 'note',
      value: 'A safe suggestion',
      expectedValueHash: 'target-hash'
    }]
  }),
  adapters: {
    browser: {
      applyPatches: async (patches) => {
        applied.push(...patches);
        return { status: 'applied' };
      }
    }
  }
});

const proposal = await service.requestAssistance({ recipeId: 'browser-feedback', context });
assert.equal(proposal.ownedId, 'owned-1');
assert.equal(proposal.generation, 2);
assert.equal(proposal.patches.length, 1);
assert.deepEqual(audit.entries().map((entry) => entry.status), ['requested', 'proposed']);

const appliedReceipt = await service.applySelectedAssistancePatches({
  proposal,
  selectedPatchIds: ['patch-note'],
  currentContext: context
});
assert.equal(appliedReceipt.status, 'applied');
assert.equal(applied.length, 1, 'only selected allow-listed fields reach the typed adapter');
assert.deepEqual(audit.entries().slice(-2).map((entry) => entry.status), ['apply-started', 'applied']);

const stale = captureAssistanceContext({
  ownedId: 'owned-1',
  generation: 3,
  surface: 'browser',
  target: { surface: 'browser', id: 'tab-1', expectedValueHash: 'target-hash' },
  facts: { draft: 'current draft' }
}, { now: 1000 });
await assert.rejects(
  service.applySelectedAssistancePatches({ proposal, selectedPatchIds: ['patch-note'], currentContext: stale }),
  /stale|current|generation/i
);

const cancelled = await service.requestAssistance({ recipeId: 'browser-feedback', context });
assert.equal(service.cancelAssistanceRequest(cancelled.requestId), true);
await assert.rejects(
  service.applySelectedAssistancePatches({ proposal: cancelled, selectedPatchIds: ['patch-note'], currentContext: context }),
  /cancelled|dismissed/i
);

const failingAudit = createAssistanceAuditLog();
const failing = createAssistanceService({
  now: () => 1000,
  audit: failingAudit,
  structuredOutput: async () => { throw new Error('provider unavailable'); },
  adapters: { browser: { applyPatches: () => ({ status: 'applied' }) } }
});
await assert.rejects(failing.requestAssistance({ recipeId: 'browser-feedback', context }), /provider unavailable/);
assert.equal(failingAudit.entries().at(-1).status, 'failed');

const serviceSource = fs.readFileSync(
  path.join(process.cwd(), 'src/lib/shell/assistance/assistanceService.ts'),
  'utf8'
);
assert.doesNotMatch(serviceSource, /\binvoke\s*\(|writeFile|readFile|fetch\s*\(|child_process|git\s/i);

const outputHashMismatch = createAssistanceService({
  now: () => 1000,
  structuredOutput: async () => ({
    provenance: 'workflow',
    confidence: 0.5,
    outputHash: 'not-the-computed-hash',
    patches: []
  }),
  adapters: { browser: { applyPatches: () => ({ status: 'applied' }) } }
});
await assert.rejects(
  outputHashMismatch.requestAssistance({ recipeId: 'browser-feedback', context }),
  /output hash/i
);

const readOnlyRecipe = createAssistanceService({
  now: () => 1000,
  structuredOutput: async () => ({
    provenance: 'workflow',
    confidence: 0.5,
    patches: [{ id: 'read-only-patch', surface: 'diff', field: 'note', value: 'not allowed here', expectedValueHash: 'target-hash' }]
  }),
  adapters: { diff: { applyPatches: () => ({ status: 'applied' }) } }
});
const diffContext = { ...context, surface: 'diff', target: { ...context.target, surface: 'diff' } };
await assert.rejects(
  readOnlyRecipe.requestAssistance({ recipeId: 'diff-explanation', context: diffContext }),
  /read-only|mutation|patch/i
);

console.log('assistanceService: all tests passed');
