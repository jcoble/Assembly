import { readFileSync } from 'node:fs';
import { init, getBinding, saveBinding, readRunbook, update, checkpoint, recover, identity, projectFor } from './runbook.ts';
import { rules } from './rules.ts';
import { harnessFor } from './harnesses.ts';

let eventName = '';
const [command, provider, id, ...args] = process.argv.slice(2);
const option = (name: string) => { const i = args.indexOf(name); return i < 0 ? undefined : args[i + 1]; };
try {
  if (command === 'hook') {
    const event = JSON.parse(readFileSync(0, 'utf8'));
    eventName = event.hook_event_name;
    const parentSession = String(event.session_id || '');
    const session = eventName === 'SubagentStart' ? parentSession : String(event.agent_id || parentSession);
    identity(provider, session);
    if (event.hook_event_name === 'SubagentStart') {
      const parent = getBinding(provider, session) || init(provider, session, event.cwd);
      const child = String(event.agent_id || '');
      identity(provider, child);
      const binding = { ...parent, readOnly: true, parent: provider + ':' + session };
      saveBinding(provider, child, binding);
      console.log(JSON.stringify(recover(provider, child, binding, 'SubagentStart')));
    } else if (event.hook_event_name === 'SessionStart') {
      const binding = getBinding(provider, session) || init(provider, session, event.cwd);
      console.log(JSON.stringify(recover(provider, session, binding)));
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
    if (eventName === 'PreCompact') {
      const harness = harnessFor(provider);
      console.log(JSON.stringify(harness.compactFailure(message)));
      process.exitCode = harness.compactExit;
    } else {
      console.log(JSON.stringify({ hookSpecificOutput: { hookEventName: eventName || 'SessionStart', additionalContext: rules() + '\nRECOVERY BLOCKED: ' + message + '\nAsk the spawning parent / runbook owner before affected work.' } }));
      process.exitCode = 0;
    }
  } else { console.error(message); process.exitCode = 1; }
}
