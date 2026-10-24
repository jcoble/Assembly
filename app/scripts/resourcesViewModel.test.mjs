import assert from 'node:assert/strict';

import {
  formatResourceBytes,
  formatResourceCpu,
  formatResourceUpdatedAgo,
  resourceSessionKindLabel,
  shapeResourceSample
} from '../src/lib/shell/resources/resourceSampleViewModel.ts';

const sample = {
  generatedAtMs: 10_000,
  totals: { cpuPercent: 7.5, rssBytes: 1_170_000_000, processCount: 5 },
  app: {
    parts: [
      { label: 'Webview / renderer', pid: 12, cpuPercent: 1.25, physicalFootprintBytes: 200, rssBytes: 200 },
      { label: 'Main process', pid: 10, cpuPercent: 0.25, physicalFootprintBytes: 300, rssBytes: 300 }
    ]
  },
  groups: [
    {
      workspace: 'project-b / worktree-b',
      sessions: [
        {
          ownedId: 'conversation-b',
          label: 'Agent conversation',
          kind: 'conversation',
          processes: [
            { pid: 22, name: 'helper', cpuPercent: 2, physicalFootprintBytes: 100, rssBytes: 100 },
            { pid: 21, name: 'adapter', cpuPercent: 1, physicalFootprintBytes: 400, rssBytes: 400 }
          ]
        }
      ]
    },
    {
      workspace: 'project-a / worktree-a',
      sessions: [
        {
          ownedId: 'terminal-a',
          label: 'Terminal 1',
          kind: 'terminal',
          processes: [{ pid: 31, name: 'zsh', cpuPercent: 3, physicalFootprintBytes: 600, rssBytes: 600 }]
        }
      ]
    }
  ]
};

const view = shapeResourceSample(sample);
assert.deepEqual(
  view.groups.map((group) => group.workspace),
  ['project-a / worktree-a', 'project-b / worktree-b']
);
assert.equal(view.groups[0].totals.processCount, 1);
assert.equal(view.groups[0].totals.rssBytes, 600);
assert.deepEqual(
  view.groups[1].sessions[0].processes.map((process) => process.pid),
  [21, 22]
);
assert.equal(view.groups[1].sessions[0].totals.cpuPercent, 3);
assert.equal(view.appParts[0].label, 'Main process');
assert.deepEqual(view.appTotals, { cpuPercent: 1.5, physicalFootprintBytes: 500, rssBytes: 500, processCount: 2 });

assert.equal(formatResourceBytes(1_170_000_000), '1.09 GB');
assert.equal(formatResourceBytes(1024 * 1024), '1.0 MB');
assert.equal(formatResourceCpu(0.44), '0.4%');
assert.equal(formatResourceUpdatedAgo(null, 10_000), 'waiting for first sample');
assert.equal(formatResourceUpdatedAgo(10_000, 10_900), 'updated just now');
assert.equal(formatResourceUpdatedAgo(10_000, 15_000), 'updated 5s ago');
assert.equal(resourceSessionKindLabel('terminal'), 'Terminal');
assert.equal(resourceSessionKindLabel('conversation'), 'Conversation');
assert.equal(resourceSessionKindLabel('other'), 'Other');

console.log('resources view-model tests passed');
