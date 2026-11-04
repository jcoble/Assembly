import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import type { RunbookMeta } from './meta.ts';

export function notionConfig(): { runbooksDataSourceId: string; tasksDataSourceId?: string } {
  const config = JSON.parse(readFileSync(join(homedir(), '.config/agent-runbook/notion.json'), 'utf8'));
  if (!config.runbooksDataSourceId) throw Error('Configure runbooksDataSourceId in agent-runbook/notion.json.');
  return config;
}
export function notionRequest(method: string, path: string, body?: unknown, timeoutMs = 5000): any {
  const args = ['api', path, '-X', method];
  if (body !== undefined) args.push('-d', JSON.stringify(body));
  const result = JSON.parse(execFileSync('ntn', args, { encoding: 'utf8', timeout: timeoutMs, maxBuffer: 262144, stdio: ['ignore', 'pipe', 'pipe'] }));
  if (result.object === 'error') throw Error('Notion request failed: ' + result.code);
  return result;
}
function properties(meta: Partial<RunbookMeta>) {
  const props: Record<string, any> = {};
  const text = (value: string) => [{ type: 'text', text: { content: value } }];
  if (meta.title !== undefined) props.Name = { title: text(meta.title) };
  for (const [key, value] of Object.entries({ Provider: meta.provider, Host: meta.host, Branch: meta.branch, Worktree: meta.worktree, Session: meta.session })) {
    if (value !== undefined) props[key] = { rich_text: text(value) };
  }
  if (meta.kind) props.Kind = { select: { name: meta.kind } };
  for (const [key, value] of Object.entries({ Project: meta.projectId, Task: meta.taskId, Parent: meta.parentPageId })) {
    if (value) props[key] = { relation: [{ id: value }] };
  }
  if (meta.prUrl) props.PR = { url: meta.prUrl };
  props.Updated = { date: { start: new Date().toISOString() } };
  return props;
}
export function createRunbookPage(meta: RunbookMeta, body: string): { id: string; url: string } {
  const result = notionRequest('POST', '/v1/pages', {
    parent: { type: 'data_source_id', data_source_id: notionConfig().runbooksDataSourceId },
    properties: { ...properties(meta), Status: { select: { name: 'Active' } } }, markdown: body,
  });
  if (!result.id || !result.url) throw Error('Notion did not return the created page identity.');
  return { id: result.id, url: result.url };
}
export function writeRunbookBody(id: string, body: string) {
  notionRequest('PATCH', '/v1/pages/' + encodeURIComponent(id) + '/markdown', { type: 'replace_content', replace_content: { new_str: body }, markdown_version: 'v2' });
}
export function readRunbookBody(id: string, timeoutMs: number): string {
  const result = notionRequest('GET', '/v1/pages/' + encodeURIComponent(id) + '/markdown?markdown_version=v2', undefined, timeoutMs);
  if (typeof result.markdown !== 'string' || result.truncated || result.unknown_block_ids?.length || result.markdown.length > 12000) throw Error('Incomplete or oversized Notion runbook.');
  return result.markdown;
}
export function setRunbookProps(id: string, meta: Partial<RunbookMeta>) {
  notionRequest('PATCH', '/v1/pages/' + encodeURIComponent(id), { properties: properties(meta) });
}
