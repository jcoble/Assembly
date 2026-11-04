import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { homedir, hostname } from 'node:os';
import { join, basename } from 'node:path';
import { notionConfig, notionRequest, remainingBudget } from './notion.ts';

export type RunbookMeta = {
  title: string; projectId?: string; provider: string; host: string; branch: string; worktree: string;
  session: string; taskKey?: string; taskId?: string; prUrl?: string;
  kind: 'Session' | 'Subagent' | 'Coordinated'; parentPageId?: string;
};
export function collectMeta(cwd: string, provider: string, session: string, parent?: string): RunbookMeta {
  const git = (...args: string[]) => {
    try { return execFileSync('git', ['-C', cwd, ...args], { encoding: 'utf8', timeout: remainingBudget(1000), stdio: ['ignore', 'pipe', 'ignore'] }).trim(); } catch { return ''; }
  };
  const branch = git('symbolic-ref', '--short', 'HEAD') || 'HEAD';
  const worktree = git('rev-parse', '--show-toplevel') || cwd;
  const common = git('rev-parse', '--path-format=absolute', '--git-common-dir');
  const repo = common ? basename(common.replace(/\/.git$/, '')) : basename(cwd);
  const meta: RunbookMeta = { title: `${repo} / ${provider} / ${branch} / ${session}`, provider, host: hostname(), branch, worktree, session, kind: parent ? 'Subagent' : 'Session', parentPageId: parent };
  try {
    const projects = JSON.parse(readFileSync(join(homedir(), '.config/agent-runbook/projects.json'), 'utf8'));
    meta.projectId = projects[repo];
  } catch { /* Task 4 installs the project mapping. */ }
  meta.taskKey = branch.match(/(?:^|\/)tsk-(\d+)(?:-|$)/i)?.[1];
  if (meta.taskKey) {
    meta.taskKey = 'TSK-' + meta.taskKey;
    const source = notionConfig().tasksDataSourceId;
    if (source) {
      const result = notionRequest('POST', '/v1/data_sources/' + encodeURIComponent(source) + '/query', { filter: { property: 'Task ID', unique_id: { equals: Number(meta.taskKey.slice(4)) } }, page_size: 2 });
      if (result.results?.length === 1) meta.taskId = result.results[0].id;
    }
  }
  try {
    meta.prUrl = JSON.parse(execFileSync('gh', ['pr', 'view', '--json', 'url'], { cwd, encoding: 'utf8', timeout: remainingBudget(2000), stdio: ['ignore', 'pipe', 'ignore'] })).url;
  } catch { /* No PR or offline is valid. */ }
  return meta;
}
