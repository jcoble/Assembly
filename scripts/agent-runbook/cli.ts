import { readFileSync, statSync } from 'node:fs';
import { init, getBinding, saveBinding, readRunbook, update, checkpoint, recover, identity, projectFor, flushPending, atomicWrite, recordWorktreeOutcome } from './runbook.ts';
import { rules } from './rules.ts';
import { harnessFor } from './harnesses.ts';
import { stopDecision, shouldNudge, contextPercent, toolCallsSince } from './stale.ts';
import { setHookBudget } from './notion.ts';
import { checkWorktreeCommand } from './guard.ts';
import { checkDataCommand, namesProtectedFolder, DATA_DENY } from './data-guard.ts';
import { createWorktree, backupWorktree, lease } from './worktree.ts';
import { registry, tryGit } from './worktree-state.ts';
import { spawn } from 'node:child_process';

let eventName = '';
let manualMissing = false;
const [command, provider, id, ...args] = process.argv.slice(2);
const option = (name: string) => { const i = args.indexOf(name); return i < 0 ? undefined : args[i + 1]; };
try {
  if (command === 'hook') {
    const event = JSON.parse(readFileSync(0, 'utf8'));
    eventName = event.hook_event_name;
    setHookBudget(['SessionEnd', 'SubagentStop'].includes(eventName) ? 55000 : ['PreToolUse', 'PostToolUse'].includes(eventName) ? 3500 : 10000);
    if (process.env.RUNBOOK_FAST_STAMP && process.env.RUNBOOK_FAST_BYTES) atomicWrite(process.env.RUNBOOK_FAST_STAMP, process.env.RUNBOOK_FAST_BYTES + '\n');
    const parentSession = String(event.session_id || '');
    const session = eventName === 'SubagentStart' ? parentSession : String(event.agent_id || parentSession);
    identity(provider, session);
    if (eventName === 'WorktreeCreate') {
      const path = createWorktree(event.name, event.cwd);
      lease(path, provider + ':' + session);
      console.log(path);
    } else if (eventName === 'PreToolUse') {
      const text = String(event.tool_input?.command || '');
      let data: { deny?: string };
      try { data = checkDataCommand(text, event.cwd); } catch { data = namesProtectedFolder(text) ? { deny: DATA_DENY } : {}; }
      let worktree: { deny?: string } = {};
      try { worktree = checkWorktreeCommand(text, event.cwd); } catch (error) { if (!data.deny) throw error; }
      const result = worktree.deny ? worktree : data;
      flushPending(provider, session);
      console.log(JSON.stringify(result.deny ? { hookSpecificOutput: { hookEventName: eventName, permissionDecision: 'deny', permissionDecisionReason: result.deny } } : {}));
    } else if (['SessionEnd', 'WorktreeRemove'].includes(eventName)) {
      const path = event.worktree_path || tryGit(event.cwd, 'rev-parse', '--show-toplevel');
      if (path && registry()[path]) {
        const binding = getBinding(provider, session);
        if (!binding?.readOnly) {
          lease(path, provider + ':' + session, true);
          const result = backupWorktree(path, provider + ':' + session);
          recordWorktreeOutcome(provider, session, result);
          if (result.reason) console.error(result.reason);
        }
      }
      flushPending(provider, session);
      console.log('{}');
    } else if (event.hook_event_name === 'SubagentStart') {
      flushPending(provider, session);
      const parent = getBinding(provider, session) || init(provider, session, event.cwd);
      const child = String(event.agent_id || '');
      identity(provider, child);
      const binding = init(provider, child, event.cwd);
      binding.parent = provider + ':' + session;
      binding.parentPageId = parent.notionPageId;
      const path = tryGit(event.cwd, 'rev-parse', '--show-toplevel');
      if (registry()[path]) lease(path, provider + ':' + child);
      saveBinding(provider, child, binding);
      const own = recover(provider, child, binding, 'SubagentStart');
      own.hookSpecificOutput.additionalContext += '\nRead-only parent context; check with your spawning parent before resuming:\n' + readRunbook(parent);
      console.log(JSON.stringify(own));
    } else if (event.hook_event_name === 'SessionStart') {
      let binding = getBinding(provider, session) || init(provider, session, event.cwd);
      flushPending(provider, session);
      binding = getBinding(provider, session)!;
      const path = tryGit(event.cwd, 'rev-parse', '--show-toplevel');
      if (registry()[path] && !binding.readOnly) lease(path, provider + ':' + session);
      // Detached one-shot sweep owns its own lock/stamp. No timer or persistent daemon.
      if (Object.keys(registry()).length) {
        const child = spawn(process.execPath, [new URL('./agent-worktree.ts', import.meta.url).pathname, 'sweep', '--hourly'], { cwd: process.env.HOME, env: process.env, detached: true, stdio: 'ignore' });
        child.on('error', error => console.error('Sweep could not start: ' + error.message)); child.unref();
      }
      if (event.transcript_path) { binding.transcriptPath = event.transcript_path; saveBinding(provider, session, binding); }
      const result = recover(provider, session, binding);
      if (event.source === 'compact') result.hookSpecificOutput.additionalContext += '\nRefresh your own runbook first before continuing work.';
      console.log(JSON.stringify(result));
    } else if (event.hook_event_name === 'PreCompact') {
      flushPending(provider, session);
      // Never initialize a replacement during compaction: a missing binding must be visible.
      manualMissing = event.trigger === 'manual' && !getBinding(provider, session);
      checkpoint(provider, session, event);
      console.log('{}');
    } else if (['Stop', 'SubagentStop', 'PostToolUse'].includes(eventName)) {
      flushPending(provider, session);
      const binding = getBinding(provider, session);
      let result: any = {};
      if (binding && !binding.readOnly) {
        const path = event.agent_transcript_path || event.transcript_path;
        if (path) binding.transcriptPath = path;
        if (eventName === 'PostToolUse' && path) {
          const bytes = statSync(path).size;
          if (bytes !== binding.checkedBytes) {
            binding.checkedBytes = bytes;
            if (shouldNudge({ percent: contextPercent(path, provider), calls: toolCallsSince(path, binding.updateCursor?.bytes || 0), subagent: !!binding.parent, nudged: binding.nudged })) {
              binding.nudged = true;
              result = { hookSpecificOutput: { hookEventName: eventName, additionalContext: 'Update your own runbook now with agent-runbook update; context is at least 70% and state is stale.' } };
            }
          }
        } else if (eventName !== 'PostToolUse') {
          const decision = stopDecision(event, binding);
          if (decision.block) { binding.stopContinued = true; result = { decision: 'block', reason: decision.reason }; }
        }
        saveBinding(provider, session, binding);
        if (eventName === 'SubagentStop' && !result.decision && !event.stop_hook_active) {
          const worktree = tryGit(event.cwd, 'rev-parse', '--show-toplevel');
          if (registry()[worktree]) {
            lease(worktree, provider + ':' + session, true);
            const backup = backupWorktree(worktree, provider + ':' + session);
            recordWorktreeOutcome(provider, session, backup);
            if (backup.reason) console.error(backup.reason);
          }
        }
      }
      console.log(JSON.stringify(result));
    }
  } else if (command === 'init') {
    console.log(JSON.stringify(init(provider, id, process.cwd(), option('--path'), option('--task'))));
  } else if (command === 'bind') {
    identity(provider, id);
    const path = option('--path');
    if (!path) throw Error('bind requires --path <absolute canonical runbook path>.');
    const binding = { path, project: option('--project') || projectFor(process.cwd()), owner: option('--parent') || 'parent', parent: option('--parent'), readOnly: true, ssh: option('--ssh') };
    readRunbook(binding);
    saveBinding(provider, id, binding);
    console.log(JSON.stringify(binding));
  } else {
    const binding = getBinding(provider, id);
    if (!binding) throw Error('No runbook binding.');
    if (command === 'update') { update(provider, id, readFileSync(0, 'utf8')); console.log(binding.path); }
    else if (command === 'read') process.stdout.write(readRunbook(binding));
    else if (command === 'where') console.log(JSON.stringify(binding));
    else throw Error('Use init, bind (read-only), update (stdin), read, where, or hook.');
  }
} catch (error) {
  const message = 'Runbook recovery error: ' + (error as Error).message;
  if (command === 'hook') {
    // Prevent compaction from silently losing the checkpoint when supported by the provider.
    console.error(message);
    if (eventName === 'WorktreeCreate') {
      process.exitCode = 1;
    } else if (eventName === 'PreCompact' && manualMissing) {
      const harness = harnessFor(provider);
      console.log(JSON.stringify(harness.compactFailure(message)));
      process.exitCode = harness.compactExit;
    } else if (eventName === 'PreCompact') {
      console.log('{}');
    } else if (['SessionStart', 'SubagentStart'].includes(eventName)) {
      console.log(JSON.stringify({ hookSpecificOutput: { hookEventName: eventName || 'SessionStart', additionalContext: rules() + '\nRECOVERY BLOCKED: ' + message + '\nAsk the spawning parent / runbook owner before affected work.' } }));
      process.exitCode = 0;
    } else console.log('{}');
  } else { console.error(message); process.exitCode = 1; }
}
