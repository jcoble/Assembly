import { readFileSync, existsSync } from 'node:fs';
import { init, getBinding, saveBinding, readRunbook, update, checkpoint, recover, identity, projectFor } from './runbook.ts';
import { rules } from './rules.ts';
import { harnessFor } from './harnesses.ts';
import { checkWorktreeCommand } from './guard.ts';
import { checkDataCommand, namesProtectedFolder, DATA_DENY } from './data-guard.ts';
import { createWorktree, backupWorktree, lease } from './worktree.ts';
import { registry, tryGit, setHookBudget } from './worktree-state.ts';
import { spawn } from 'node:child_process';
import { sessionOwner } from './session-owner.ts';

let eventName = '';
const [command, provider, id, ...args] = process.argv.slice(2);
const option = (name: string) => { const i = args.indexOf(name); return i < 0 ? undefined : args[i + 1]; };
try {
  if (command === 'hook') {
    const event = JSON.parse(readFileSync(0, 'utf8'));
    eventName = event.hook_event_name;
    setHookBudget(['SessionEnd', 'SubagentStop'].includes(eventName) ? 55000 : eventName === 'PreToolUse' ? 3500 : 10000);
    const parentSession = String(event.session_id || '');
    const session = eventName === 'SubagentStart' ? parentSession : String(event.agent_id || parentSession);
    identity(provider, session);
    const ownerWarning = sessionOwner(provider, event);
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
      console.log(JSON.stringify(result.deny ? { hookSpecificOutput: { hookEventName: eventName, permissionDecision: 'deny', permissionDecisionReason: result.deny } } : {}));
    } else if (['SessionEnd', 'WorktreeRemove'].includes(eventName)) {
      const path = event.worktree_path || tryGit(event.cwd, 'rev-parse', '--show-toplevel');
      if (path && registry()[path]) {
        const binding = getBinding(provider, session);
        if (!binding?.readOnly) {
          lease(path, provider + ':' + session, true);
          const result = backupWorktree(path, provider + ':' + session);
          if (result.reason) console.error(result.reason);
        }
      }
      if (eventName === 'WorktreeRemove' && (!path || existsSync(path))) {
        console.error('Worktree retained for resume and safe sweep; refusing removal.');
        process.exitCode = 1;
      }
      console.log('{}');
    } else if (eventName === 'SubagentStop') {
      const path = tryGit(event.cwd, 'rev-parse', '--show-toplevel');
      if (registry()[path]) {
        lease(path, provider + ':' + session, true);
        if (!Object.keys(registry()[path].leases).length) {
          const result = backupWorktree(path, provider + ':' + session);
          if (result.reason) console.error(result.reason);
        }
      }
      console.log('{}');
    } else if (event.hook_event_name === 'SubagentStart') {
      const parent = getBinding(provider, session) || init(provider, session, event.cwd);
      const child = String(event.agent_id || '');
      identity(provider, child);
      const binding = { ...parent, readOnly: true, parent: provider + ':' + session };
      const path = tryGit(event.cwd, 'rev-parse', '--show-toplevel');
      if (registry()[path]) lease(path, provider + ':' + child);
      saveBinding(provider, child, binding);
      console.log(JSON.stringify(recover(provider, child, binding, 'SubagentStart')));
    } else if (event.hook_event_name === 'SessionStart') {
      const binding = getBinding(provider, session) || init(provider, session, event.cwd);
      const path = tryGit(event.cwd, 'rev-parse', '--show-toplevel');
      if (registry()[path] && !binding.readOnly) lease(path, provider + ':' + session);
      // Detached one-shot sweep owns its own lock/stamp. No timer or persistent daemon.
      if (Object.keys(registry()).length) {
        const child = spawn(process.execPath, [new URL('./agent-worktree.ts', import.meta.url).pathname, 'sweep', '--hourly'], { cwd: process.env.HOME, env: process.env, detached: true, stdio: 'ignore' });
        child.on('error', error => console.error('Sweep could not start: ' + error.message)); child.unref();
      }
      const recovered = recover(provider, session, binding);
      if (ownerWarning) recovered.hookSpecificOutput.additionalContext += '\n' + ownerWarning;
      console.log(JSON.stringify(recovered));
    } else if (event.hook_event_name === 'PreCompact') {
      // Never initialize a replacement during compaction: a missing binding must be visible.
      checkpoint(provider, session, event);
      console.log('{}');
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
    if (['WorktreeCreate', 'WorktreeRemove'].includes(eventName)) {
      process.exitCode = 1;
    } else if (eventName === 'PreCompact') {
      const harness = harnessFor(provider);
      console.log(JSON.stringify(harness.compactFailure(message)));
      process.exitCode = harness.compactExit;
    } else {
      console.log(JSON.stringify({ hookSpecificOutput: { hookEventName: eventName || 'SessionStart', additionalContext: rules() + '\nRECOVERY BLOCKED: ' + message + '\nAsk the spawning parent / runbook owner before affected work.' } }));
      process.exitCode = 0;
    }
  } else { console.error(message); process.exitCode = 1; }
}
