import { openSync, closeSync, readSync, statSync } from 'node:fs';
import type { Binding } from './runbook.ts';
import { harnessFor } from './harnesses.ts';

function* records(path: string, cursor = 0): Generator<any> {
  const fd = openSync(path, 'r');
  try {
    const size = statSync(path).size;
    // A stale or truncated transcript starts a new cursor. Bound context reads to the tail.
    let position = cursor > size ? 0 : Math.max(0, cursor);
    let partial = Buffer.alloc(0);
    const buffer = Buffer.alloc(65536);
    while (position < size) {
      const read = readSync(fd, buffer, 0, Math.min(buffer.length, size - position), position);
      if (!read) break;
      position += read;
      const chunk = Buffer.concat([partial, buffer.subarray(0, read)]);
      let start = 0;
      for (let end = chunk.indexOf(10); end >= 0; end = chunk.indexOf(10, start)) {
        try { yield JSON.parse(chunk.subarray(start, end).toString('utf8')); } catch {}
        start = end + 1;
      }
      // Oversized or malformed single records are not usable facts.
      partial = chunk.length - start > 4194304 ? Buffer.alloc(0) : Buffer.from(chunk.subarray(start));
    }
    if (partial.length) { try { yield JSON.parse(partial.toString('utf8')); } catch {} }
  } finally { closeSync(fd); }
}
export function toolCallsSince(transcriptPath: string, cursorBytes: number): number {
  const calls = new Set<string>();
  for (const row of records(transcriptPath, cursorBytes)) {
    if (row.type === 'response_item' && ['function_call', 'custom_tool_call'].includes(row.payload?.type)) calls.add(row.payload.call_id || JSON.stringify(row));
    for (const content of row.type === 'assistant' && Array.isArray(row.message?.content) ? row.message.content : []) {
      if (content.type === 'tool_use') calls.add(content.id || JSON.stringify(content));
    }
  }
  return calls.size;
}
export function contextPercent(transcriptPath: string, provider: string): number | undefined {
  for (const row of [...records(transcriptPath, Math.max(0, statSync(transcriptPath).size - 131072))].reverse()) {
    const { window, tokens } = harnessFor(provider).usage(row);
    if (window > 0 && Number.isFinite(tokens)) return 100 * tokens / window;
  }
  // No invented context size when the provider transcript does not expose one.
  return undefined;
}
export function shouldNudge(state: { percent?: number; calls: number; subagent?: boolean; nudged?: boolean }) {
  return !state.nudged && state.percent !== undefined && state.percent >= 70 && state.calls >= (state.subagent ? 25 : 10);
}
export function stopDecision(event: any, binding: Binding): { block: boolean; reason?: string } {
  if (event.stop_hook_active || binding.readOnly || binding.stopContinued) return { block: false };
  const path = event.agent_transcript_path || event.transcript_path;
  if (!path || toolCallsSince(path, binding.updateCursor?.bytes || 0) < (binding.parent ? 25 : 10)) return { block: false };
  return { block: true, reason: 'Update your own runbook now with agent-runbook update before stopping. Replace Summary, Next, Details, Questions for Jesse, Blockers, Links; delete finished items. Then finish this turn.' };
}
