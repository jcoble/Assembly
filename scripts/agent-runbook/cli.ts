import { readFileSync, statSync } from 'node:fs';
import { init, getBinding, saveBinding, readRunbook, update, checkpoint, recover, identity, projectFor, flushPending, atomicWrite } from './runbook.ts';
import { rules } from './rules.ts';
import { harnessFor } from './harnesses.ts';
import { stopDecision, shouldNudge, contextPercent, toolCallsSince } from './stale.ts';
import { setHookBudget } from './notion.ts';

let eventName = '';
let manualMissing = false;
const [command, provider, id, ...args] = process.argv.slice(2);
const option = (name: string) => { const i = args.indexOf(name); return i < 0 ? undefined : args[i + 1]; };
try {
  if (command === 'hook') {
    const event = JSON.parse(readFileSync(0, 'utf8'));
    eventName = event.hook_event_name;
    setHookBudget(['PreToolUse', 'PostToolUse'].includes(eventName) ? 3500 : 10000);
    if (process.env.RUNBOOK_FAST_STAMP && process.env.RUNBOOK_FAST_BYTES) atomicWrite(process.env.RUNBOOK_FAST_STAMP, process.env.RUNBOOK_FAST_BYTES + '\n');
    const parentSession = String(event.session_id || '');
    const session = eventName === 'SubagentStart' ? parentSession : String(event.agent_id || parentSession);
    identity(provider, session);
    flushPending(provider, session);
    if (event.hook_event_name === 'SubagentStart') {
      const parent = getBinding(provider, session) || init(provider, session, event.cwd);
      const child = String(event.agent_id || '');
      identity(provider, child);
      const binding = init(provider, child, event.cwd);
      binding.parent = provider + ':' + session;
      binding.parentPageId = parent.notionPageId;
      saveBinding(provider, child, binding);
      const own = recover(provider, child, binding, 'SubagentStart');
      own.hookSpecificOutput.additionalContext += '\nRead-only parent context; check with your spawning parent before resuming:\n' + readRunbook(parent);
      console.log(JSON.stringify(own));
    } else if (event.hook_event_name === 'SessionStart') {
      const binding = getBinding(provider, session) || init(provider, session, event.cwd);
      if (event.transcript_path) { binding.transcriptPath = event.transcript_path; saveBinding(provider, session, binding); }
      const result = recover(provider, session, binding);
      if (event.source === 'compact') result.hookSpecificOutput.additionalContext += '\nRefresh your own runbook first before continuing work.';
      console.log(JSON.stringify(result));
    } else if (event.hook_event_name === 'PreCompact') {
      // Never initialize a replacement during compaction: a missing binding must be visible.
      manualMissing = event.trigger === 'manual' && !getBinding(provider, session);
      checkpoint(provider, session, event);
      console.log('{}');
    } else if (['Stop', 'SubagentStop', 'PostToolUse'].includes(eventName)) {
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
    if (eventName === 'PreCompact' && manualMissing) {
      const harness = harnessFor(provider);
      console.log(JSON.stringify(harness.compactFailure(message)));
      process.exitCode = harness.compactExit;
    } else if (eventName === 'PreCompact') {
      console.log('{}');
    } else {
      console.log(JSON.stringify({ hookSpecificOutput: { hookEventName: eventName || 'SessionStart', additionalContext: rules() + '\nRECOVERY BLOCKED: ' + message + '\nAsk the spawning parent / runbook owner before affected work.' } }));
      process.exitCode = 0;
    }
  } else { console.error(message); process.exitCode = 1; }
}
