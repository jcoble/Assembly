import assert from 'node:assert/strict';
import { createAssistanceAuditLog, restoreAssistanceAuditLog } from '../src/lib/shell/assistance/assistanceAudit.ts';

const log = createAssistanceAuditLog();
const statuses = ['requested', 'proposed', 'dismissed', 'stale', 'apply-started', 'applied', 'refused', 'failed', 'outcome-unknown'];
for (const status of statuses) {
  log.record({
    requestId: `request-${status}`,
    recipeId: 'browser-feedback',
    status,
    ownedId: 'owned-1',
    generation: 1,
    surface: 'browser',
    detail: 'token=secret-value; Authorization: bearer secret-value'
  });
}

const entries = log.entries();
assert.deepEqual(entries.map((entry) => entry.status), statuses);
assert.equal(entries.some((entry) => JSON.stringify(entry).includes('secret-value')), false, 'audit is redacted');
assert.ok(entries.every((entry) => entry.sequence > 0));

const restored = restoreAssistanceAuditLog(log.serialize());
assert.deepEqual(restored.entries(), entries, 'audit receipts survive reload');
assert.throws(() => log.record({
  requestId: 'bad', recipeId: 'browser-feedback', status: 'requested', ownedId: 'owned-1', generation: 1,
  surface: 'browser', detail: 'x'.repeat(10_000)
}), /bounded|redact/i);

console.log('assistanceAudit: all tests passed');
