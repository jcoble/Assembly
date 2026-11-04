import { readFileSync, mkdirSync, chmodSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { homedir } from 'node:os';
import { atomicWrite } from './runbook.ts';
import { withCodex } from './codex-rpc.ts';
import { harnesses } from './harnesses.ts';

const home = homedir();
const destination = join(home, '.local/share/agent-runbook');
const launcher = join(home, '.local/bin/agent-runbook');
const quote = (text: string) => "'" + text.replaceAll("'", "'\\''") + "'";
mkdirSync(destination, { recursive: true, mode: 0o700 });
atomicWrite(join(destination, 'package.json'), '{\n  \"type\": \"module\"\n}\n');
for (const name of ['rules.ts', 'runbook.ts', 'cli.ts', 'harnesses.ts', 'fast-hook.sh', 'worktree.ts', 'worktree-state.ts', 'worktree-safety.ts', 'guard.ts', 'data-guard.ts', 'backup.ts', 'agent-worktree.ts']) {
  atomicWrite(join(destination, name), readFileSync(new URL(name, import.meta.url), 'utf8'));
}
atomicWrite(launcher, '#!/bin/sh\nexec ' + quote(process.execPath) + ' ' + quote(join(destination, 'cli.ts')) + ' "$@"\n');
chmodSync(launcher, 0o700);
const worktreeLauncher = join(home, '.local/bin/agent-worktree');
atomicWrite(worktreeLauncher, '#!/bin/sh\nexec ' + quote(process.execPath) + ' ' + quote(join(destination, 'agent-worktree.ts')) + ' "$@"\n');
chmodSync(worktreeLauncher, 0o700);
const receipt: any[] = [];
const installedCommands: Record<string, string[]> = {};
for (const [provider, harness] of Object.entries(harnesses)) {
  const path = join(home, harness.settingsFile);
  let config: any;
  try { config = JSON.parse(readFileSync(path, 'utf8')); } catch (error: any) { if (error.code !== 'ENOENT') throw error; config = {}; }
  config.hooks ??= {};
  const command = quote(launcher) + ' hook ' + provider;
  const preCommand = '/bin/sh ' + quote(join(destination, 'fast-hook.sh')) + ' pre ' + quote(process.execPath) + ' ' + quote(join(destination, 'cli.ts')) + ' ' + provider;
  // Remove our prior registrations, including the shelved post hook, preserving user hooks.
  const ownedCommands = [command, preCommand, '/bin/sh ' + quote(join(destination, 'fast-hook.sh')) + ' post ' + quote(process.execPath) + ' ' + quote(join(destination, 'cli.ts')) + ' ' + provider];
  for (const [event, groups] of Object.entries(config.hooks) as [string, any[]][]) {
    for (const group of groups) group.hooks = group.hooks.filter((hook: any) => !ownedCommands.includes(hook.command));
    const retained = groups.filter((group: any) => group.hooks.length);
    if (retained.length) config.hooks[event] = retained;
    else delete config.hooks[event];
  }
  for (const event of ['SessionStart', 'PreCompact', 'SubagentStart', 'SubagentStop', 'PreToolUse', 'SessionEnd', ...harness.worktreeEvents]) {
    const groups = config.hooks[event] || [];
    config.hooks[event] = groups;
    const commands: Record<string, string> = { PreToolUse: preCommand };
    const hook: any = { type: 'command', command: commands[event] || command, timeout: ['SessionEnd', 'SubagentStop'].includes(event) ? 60 : event === 'PreToolUse' ? 5 : ['SessionStart', 'PreCompact', 'SubagentStart'].includes(event) ? 8 : 15 };
    if (['SessionStart', 'PreCompact', 'SubagentStart'].includes(event)) Object.assign(hook, harness.hookOptions);
    const group: any = { hooks: [hook] };
    if (event === 'SessionStart') group.matcher = 'startup|resume|clear|compact|fork';
    if (event === 'PreCompact') group.matcher = 'manual|auto';
    if (event === 'PreToolUse') group.matcher = 'Bash';
    config.hooks[event].push(group);
  }
  if (existsSync(path) && !existsSync(path + '.agent-runbook.bak')) atomicWrite(path + '.agent-runbook.bak', readFileSync(path, 'utf8'));
  atomicWrite(path, JSON.stringify(config, null, 2) + '\n');
  receipt.push({ provider, config: path, commands: [command, preCommand] });
  installedCommands[provider] = [command, preCommand];
}
if (!process.argv.includes('--without-codex-trust')) {
  const binary = process.env.RUNBOOK_CODEX || join(home, '.local/bin/codex');
  await withCodex(binary, async request => {
    const data = await request('hooks/list', { cwds: [process.cwd()] });
    const entry = data.data[0];
    if (entry.errors.length) throw Error(JSON.stringify(entry.errors));
    const commands = installedCommands.codex;
    const hooks = entry.hooks.filter((hook: any) => commands.includes(hook.command));
    if (hooks.length !== 6) throw Error('Expected exactly six installed Codex runbook hooks.');
    await request('config/batchWrite', { edits: hooks.map((hook: any) => ({
      keyPath: 'hooks.state.' + JSON.stringify(hook.key),
      value: { trusted_hash: hook.currentHash, enabled: true },
      mergeStrategy: 'upsert',
    })) });
    receipt.push({ trusted: hooks.map((hook: any) => ({ event: hook.eventName, key: hook.key, hash: hook.currentHash })) });
  });
}
console.log(JSON.stringify(receipt, null, 2));
