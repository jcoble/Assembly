import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync, realpathSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';

export function fixture(t: any) {
  const home = realpathSync(mkdtempSync(join(tmpdir(), 'workflow-test-')));
  t.after(() => rmSync(home, { recursive: true, force: true }));
  const cwd = join(home, 'Assembly');
  const bin = join(home, 'bin');
  mkdirSync(cwd); mkdirSync(bin);
  writeFileSync(join(bin, 'gh'), '#!/bin/sh\nexit 1\n', { mode: 0o755 });
  mkdirSync(join(home, '.config/agent-runbook'), { recursive: true });
  writeFileSync(join(home, '.config/agent-runbook/notion.json'), JSON.stringify({ runbooksDataSourceId: 'runbooks', tasksDataSourceId: 'tasks' }));
  const remote = join(home, 'remote.json');
  writeFileSync(remote, JSON.stringify({ calls: [], pages: {}, offline: false }));
  writeFileSync(join(bin, 'ntn'), `#!${process.execPath}
const fs = require('node:fs');
const file = process.env.FAKE_NOTION;
const db = JSON.parse(fs.readFileSync(file));
const args = process.argv.slice(2), path = args[1], method = args[args.indexOf('-X') + 1];
const body = args.includes('-d') ? JSON.parse(args[args.indexOf('-d') + 1]) : undefined;
db.calls.push({path, method, body});
fs.writeFileSync(file, JSON.stringify(db));
if (db.offline) process.exit(1);
let result = {};
if (path === '/v1/pages') { const id = 'page-' + Object.keys(db.pages).length; db.pages[id] = body.markdown; result = {id, url: 'https://www.notion.so/' + id}; }
else if (path.includes('/query')) result = {results: [{id: 'task-page'}]};
else if (path.includes('/markdown')) { const id = path.split('/')[3]; if (method === 'PATCH') db.pages[id] = body.replace_content.new_str; result = {markdown: db.pages[id]}; }
fs.writeFileSync(file, JSON.stringify(db));
console.log(JSON.stringify(result));
`, { mode: 0o755 });
  const env = { ...process.env, HOME: home, PATH: bin + ':' + process.env.PATH, FAKE_NOTION: remote, NOTION_HOME: join(home, 'notion') };
  function run(args: string[], input = '', extra = {}) {
    return spawnSync(process.execPath, [new URL('./cli.ts', import.meta.url).pathname, ...args], { cwd, input, encoding: 'utf8', env: { ...env, ...extra } });
  }
  function hook(event: any, provider = 'codex') {
    return run(['hook', provider], JSON.stringify({ cwd, session_id: 'parent', ...event }));
  }
  const db = () => JSON.parse(readFileSync(remote, 'utf8'));
  const change = (fn: (db: any) => void) => { const value = db(); fn(value); writeFileSync(remote, JSON.stringify(value)); };
  const binding = () => JSON.parse(run(['where', 'codex', 'parent']).stdout);
  return { home, cwd, bin, env, run, hook, db, change, binding };
}
