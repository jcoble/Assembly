import { readFileSync, mkdirSync, chmodSync } from 'node:fs';
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
for (const name of ['rules.ts', 'runbook.ts', 'cli.ts', 'harnesses.ts']) {
  atomicWrite(join(destination, name), readFileSync(new URL(name, import.meta.url), 'utf8'));
}
atomicWrite(launcher, '#!/bin/sh\nexec ' + quote(process.execPath) + ' ' + quote(join(destination, 'cli.ts')) + ' "$@"\n');
chmodSync(launcher, 0o700);
const receipt: any[] = [];
for (const [provider, harness] of Object.entries(harnesses)) {
  const path = join(home, harness.settingsFile);
  let config: any;
  try { config = JSON.parse(readFileSync(path, 'utf8')); } catch (error: any) { if (error.code !== 'ENOENT') throw error; config = {}; }
  config.hooks ??= {};
  const command = quote(launcher) + ' hook ' + provider;
  for (const event of ['SessionStart', 'PreCompact', 'SubagentStart']) {
    const groups = config.hooks[event] || [];
    for (const group of groups) group.hooks = group.hooks.filter((hook: any) => hook.command !== command);
    config.hooks[event] = groups.filter((group: any) => group.hooks.length);
    const hook: any = { type: 'command', command, timeout: 8, ...harness.hookOptions };
    const group: any = { hooks: [hook] };
    if (event === 'SessionStart') group.matcher = 'startup|resume|clear|compact|fork';
    if (event === 'PreCompact') group.matcher = 'manual|auto';
    config.hooks[event].push(group);
  }
  atomicWrite(path, JSON.stringify(config, null, 2) + '\n');
  receipt.push({ provider, config: path, command });
}
if (!process.argv.includes('--without-codex-trust')) {
  const binary = process.env.RUNBOOK_CODEX || join(home, '.local/bin/codex');
  await withCodex(binary, async request => {
    const data = await request('hooks/list', { cwds: [process.cwd()] });
    const entry = data.data[0];
    if (entry.errors.length) throw Error(JSON.stringify(entry.errors));
    const command = quote(launcher) + ' hook codex';
    const hooks = entry.hooks.filter((hook: any) => hook.command === command);
    if (hooks.length !== 3) throw Error('Expected exactly three installed Codex recovery hooks.');
    await request('config/batchWrite', { edits: hooks.map((hook: any) => ({
      keyPath: 'hooks.state.' + JSON.stringify(hook.key),
      value: { trusted_hash: hook.currentHash, enabled: true },
      mergeStrategy: 'upsert',
    })) });
    receipt.push({ trusted: hooks.map((hook: any) => ({ event: hook.eventName, key: hook.key, hash: hook.currentHash })) });
  });
}
console.log(JSON.stringify(receipt, null, 2));
