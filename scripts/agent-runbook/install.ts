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
for (const name of ['rules.ts', 'runbook.ts', 'cli.ts', 'harnesses.ts', 'notion.ts', 'meta.ts', 'stale.ts', 'fast-hook.sh', 'worktree.ts', 'worktree-state.ts', 'guard.ts', 'data-guard.ts', 'backup.ts', 'agent-worktree.ts']) {
  atomicWrite(join(destination, name), readFileSync(new URL(name, import.meta.url), 'utf8'));
}
atomicWrite(launcher, '#!/bin/sh\nexec ' + quote(process.execPath) + ' ' + quote(join(destination, 'cli.ts')) + ' "$@"\n');
chmodSync(launcher, 0o700);
const worktreeLauncher = join(home, '.local/bin/agent-worktree');
atomicWrite(worktreeLauncher, '#!/bin/sh\nexec ' + quote(process.execPath) + ' ' + quote(join(destination, 'agent-worktree.ts')) + ' "$@"\n');
chmodSync(worktreeLauncher, 0o700);
// capture-task's repo table, plus the canonical Assembly and workflow project pages.
const projectPath = join(home, '.config/agent-runbook/projects.json');
const projects = {
  'rental-management': '376394b0689d81d5a191fb89c98a4fe4',
  EdiPlatform: '376394b0689d818a9df6c37a31496479',
  HealthAggregator: '376394b0689d8131b59ec3d6d36cefc2',
  'mac-command-bar': '37a394b0689d810bb21bed03cdafef58',
  'portfolio-work': '376394b0689d81658d54dc24635dda26',
  Assembly: '406394b0689d8162a0a4c21e11a6d23a',
  workflow: '40e394b0689d810ca65dd017104d278f',
};
let existingProjects = {};
if (existsSync(projectPath)) {
  const text = readFileSync(projectPath, 'utf8');
  existingProjects = JSON.parse(text);
  if (!existsSync(projectPath + '.agent-runbook.bak')) atomicWrite(projectPath + '.agent-runbook.bak', text);
}
atomicWrite(projectPath, JSON.stringify({ ...projects, ...existingProjects }, null, 2) + '\n');
const receipt: any[] = [];
const installedCommands: Record<string, string[]> = {};
for (const [provider, harness] of Object.entries(harnesses)) {
  const path = join(home, harness.settingsFile);
  let config: any;
  try { config = JSON.parse(readFileSync(path, 'utf8')); } catch (error: any) { if (error.code !== 'ENOENT') throw error; config = {}; }
  config.hooks ??= {};
  const command = quote(launcher) + ' hook ' + provider;
  const postCommand = '/bin/sh ' + quote(join(destination, 'fast-hook.sh')) + ' post ' + quote(process.execPath) + ' ' + quote(join(destination, 'cli.ts')) + ' ' + provider;
  const preCommand = '/bin/sh ' + quote(join(destination, 'fast-hook.sh')) + ' pre ' + quote(process.execPath) + ' ' + quote(join(destination, 'cli.ts')) + ' ' + provider;
  for (const event of ['SessionStart', 'PreCompact', 'SubagentStart', 'Stop', 'SubagentStop', 'PostToolUse', 'PreToolUse', 'SessionEnd', ...harness.worktreeEvents]) {
    const groups = config.hooks[event] || [];
    for (const group of groups) group.hooks = group.hooks.filter((hook: any) => ![command, postCommand, preCommand].includes(hook.command));
    config.hooks[event] = groups.filter((group: any) => group.hooks.length);
    const commands: Record<string, string> = { PostToolUse: postCommand, PreToolUse: preCommand };
    const hook: any = { type: 'command', command: commands[event] || command, timeout: ['SessionEnd', 'SubagentStop'].includes(event) ? 60 : ['PostToolUse', 'PreToolUse'].includes(event) ? 5 : 15 };
    if (['SessionStart', 'SubagentStart', 'PostToolUse'].includes(event)) Object.assign(hook, harness.hookOptions);
    const group: any = { hooks: [hook] };
    if (event === 'SessionStart') group.matcher = 'startup|resume|clear|compact|fork';
    if (event === 'PreCompact') group.matcher = 'manual|auto';
    if (event === 'PreToolUse') group.matcher = 'Bash';
    config.hooks[event].push(group);
  }
  if (existsSync(path) && !existsSync(path + '.agent-runbook.bak')) atomicWrite(path + '.agent-runbook.bak', readFileSync(path, 'utf8'));
  atomicWrite(path, JSON.stringify(config, null, 2) + '\n');
  receipt.push({ provider, config: path, commands: [command, postCommand, preCommand] });
  installedCommands[provider] = [command, postCommand, preCommand];
}
if (!process.argv.includes('--without-codex-trust')) {
  const binary = process.env.RUNBOOK_CODEX || join(home, '.local/bin/codex');
  await withCodex(binary, async request => {
    const data = await request('hooks/list', { cwds: [process.cwd()] });
    const entry = data.data[0];
    if (entry.errors.length) throw Error(JSON.stringify(entry.errors));
    const commands = installedCommands.codex;
    const hooks = entry.hooks.filter((hook: any) => commands.includes(hook.command));
    if (hooks.length !== 8) throw Error('Expected exactly eight installed Codex runbook hooks.');
    await request('config/batchWrite', { edits: hooks.map((hook: any) => ({
      keyPath: 'hooks.state.' + JSON.stringify(hook.key),
      value: { trusted_hash: hook.currentHash, enabled: true },
      mergeStrategy: 'upsert',
    })) });
    receipt.push({ trusted: hooks.map((hook: any) => ({ event: hook.eventName, key: hook.key, hash: hook.currentHash })) });
  });
}
console.log(JSON.stringify(receipt, null, 2));
