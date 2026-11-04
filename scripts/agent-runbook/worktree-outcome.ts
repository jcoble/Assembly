import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { atomicWrite } from './runbook.ts';
import { stateRoot } from './worktree-state.ts';
import { canonical, inside } from './guard.ts';
import { notionRequest } from './notion.ts';

export function markWorktreeRunbooksDone(path: string) {
  const root = join(stateRoot(), 'bindings');
  if (!existsSync(root)) return;
  for (const provider of readdirSync(root)) {
    for (const name of readdirSync(join(root, provider))) {
      const file = join(root, provider, name);
      const binding = JSON.parse(readFileSync(file, 'utf8'));
      if (binding.readOnly || binding.ssh || !binding.cwd || !(canonical(binding.cwd) === path || inside(binding.cwd, path))) continue;
      if (!binding.notionPageId) continue;
      try {
        notionRequest('PATCH', '/v1/pages/' + encodeURIComponent(binding.notionPageId), { properties: { Status: { select: { name: 'Done' } } } });
        delete binding.worktreeDonePending;
      } catch (error) {
        binding.worktreeDonePending = true;
        console.error('Runbook Done update pending: ' + (error as Error).message);
      }
      atomicWrite(file, JSON.stringify(binding));
    }
  }
}
