import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { copyFileSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync, watch, existsSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { adapterSources, digestInputs, remoteBehindExit, sourcesDigest } from './dev-remote-identity.ts';

const current = 'a'.repeat(40), previous = 'b'.repeat(40);
const hostEntries = ['100644 blob ' + '1'.repeat(40) + '\tdotnet/Assembly.Remote/Assembly.Remote.csproj', '100644 blob ' + '3'.repeat(40) + '\tdotnet/Assembly.Infrastructure/Notion/NotionCalls.cs'];
const previousIdentity = digestInputs(hostEntries, 'none');
const launcher = createHash('sha256').update('launcher\n').digest('hex');
const sources = await sourcesDigest(adapterSources, async () => 'launcher\n');
const fakeGit = `#!/usr/bin/env node
import fs from 'node:fs';
const args=process.argv.slice(2), state=JSON.parse(fs.readFileSync(process.env.STATE,'utf8'));
fs.appendFileSync(process.env.CALLS, JSON.stringify(['git',...args])+'\\n');
switch(args[0]) {
 case 'rev-parse': console.log('${current}'); break;
 case 'diff': console.log(state.dirty ?? ''); break;
 case 'ls-files': console.log(state.untracked ?? ''); break;
 case 'ls-tree': console.log('100644 blob '+(state.changed?'2':'1').repeat(40)+'\\tdotnet/Assembly.Remote/Assembly.Remote.csproj\\n100644 blob '+(state.unrelated?'4':'3').repeat(40)+'\\tdotnet/Assembly.Infrastructure/Notion/NotionCalls.cs'); break;
 case 'show': process.stdout.write(args.at(-1).endsWith('Cargo.toml') ? (state.version ? '[package]\\nversion = "'+state.version+'"\\n' : '') : args.at(-1).endsWith(state.adapterChange ?? '?') ? 'changed\\n' : 'launcher\\n'); break;
 case 'push': break;
 case 'fetch': break;
 case 'status': console.log(state.mainDirty ? ' M dotnet/Directory.Build.props' : ''); break;
 default: throw Error('unexpected git call '+args);
}
`;
const fakeSsh = `#!/usr/bin/env node
import fs from 'node:fs';
import {spawn} from 'node:child_process';
const args=process.argv.slice(2), state=JSON.parse(fs.readFileSync(process.env.STATE,'utf8'));
fs.appendFileSync(process.env.CALLS, JSON.stringify(['ssh',...args])+'\\n');
if (state.unreachable) {console.error('ssh: connect to host workbox-test: Network is unreachable');process.exit(255)}
if (args.at(-1).startsWith('flock ')) {
 if (state.startFail) {console.log('ASSEMBLY_REMOTE_SYNC '+JSON.stringify({reason:'Remote backend failed to start. Host may be down; no automatic rollback.'}));console.error('new backend failed to start; no rollback');process.exit(1)}
 if (state.deployFail) {console.error('dotnet publish failed: test compiler error');process.exit(1)}
 if(state.hold) {
  const child=spawn('flock',['-n','-E','75',process.env.LOCK,process.execPath,process.env.HOLDER],{stdio:['inherit','inherit','inherit']});
  child.on('close',code=>process.exit(code));
 } else console.log('deployed backend; authenticated host commit ${current}');
} else if(state.missing) console.log(JSON.stringify({missing:true}));
else console.log(JSON.stringify({commit:'${previous}',backendIdentity:'${previousIdentity}',version:state.hostVersion===undefined?'none':state.hostVersion,engine:'test-engine',dll:'/lane/bin/old/backend/Assembly.Remote.dll',pid:'123',unitSha:'f'.repeat(64),adapterOK:true,launcherSha:'${launcher}',toolsKey:state.toolsKey,adapterSources:'${sources}',seedCohort:state.seedCohort}));
`;
/** A fake that waits until the test writes `release`, after writing its pid to `started`. It also ends, with 1, when
 * the test removes `root` or the test host (`TEST_HOST`) is gone, so a failed or killed test leaves nothing running. */
const waiter = (root: string, release: string, started: string, done = '') => `import fs from 'node:fs';
const host=+process.env.TEST_HOST;
const end=()=>{watcher.close();clearInterval(alive);process.exit(1)};
const finish=()=>{if(fs.existsSync(${JSON.stringify(release)})){watcher.close();clearInterval(alive);${done}}else if(!fs.existsSync(${JSON.stringify(root)}))end()};
const watcher=fs.watch(${JSON.stringify(root)},finish);
const alive=setInterval(()=>{if(!fs.existsSync(${JSON.stringify(root)}))end();try{process.kill(host,0)}catch{end()}},200);
fs.writeFileSync(${JSON.stringify(started)}+'.new',String(process.pid));fs.renameSync(${JSON.stringify(started)}+'.new',${JSON.stringify(started)});finish();
`;
function fixture(state: object = {}) {
  const root = mkdtempSync(join(tmpdir(), 'assembly-devsync-'));
  for (const dir of ['scripts', 'bin']) mkdirSync(join(root, dir));
  for (const file of ['workbox-serve.ts', 'dev-remote-process.ts', 'dev-remote-identity.ts', 'dev-remote-probe.ts', 'dev-remote-timing.ts', 'dev-toolchain.ts'])
    copyFileSync(new URL(file, import.meta.url), join(root, 'scripts', file));
  const paths = { state: join(root, 'state'), calls: join(root, 'calls'), marker: join(root, 'started'), release: join(root, 'release') };
  writeFileSync(paths.state, JSON.stringify(state));
  writeFileSync(paths.calls, '');
  writeFileSync(join(root, 'bin/git'), fakeGit, { mode: 0o755 });
  writeFileSync(join(root, 'bin/ssh'), fakeSsh, { mode: 0o755 });
  const holder = join(root, 'holder.ts');
  writeFileSync(holder, waiter(root, paths.release, paths.marker, "console.log('deploy completed')"));
  const env: NodeJS.ProcessEnv = { ...process.env, PATH: join(root, 'bin') + ':' + process.env.PATH, STATE: paths.state, CALLS: paths.calls, LOCK: join(root, 'lock'), HOLDER: holder, TEST_HOST: String(process.pid) };
  const args = [join(root, 'scripts/workbox-serve.ts'), 'test-lane', '9'];
  return { root, paths, env, args, run: (extra: string[] = []) => spawnSync(process.execPath, extra.length ? [args[0], ...extra] : args, { cwd: root, env, encoding: 'utf8' }),
    calls: () => readFileSync(paths.calls, 'utf8').trim().split('\n').filter(Boolean).map(line => JSON.parse(line) as string[]),
    close: () => rmSync(root, { recursive: true, force: true }) };
}
const update = { ASSEMBLY_DEV_REMOTE_SYNC: '1' };
const check = { ASSEMBLY_DEV_REMOTE_SYNC: 'check' };
const deploys = (f: ReturnType<typeof fixture>) => f.calls().filter(call => call[0] === 'ssh' && call.at(-1)?.startsWith('flock ')).length;
const pushes = (f: ReturnType<typeof fixture>) => f.calls().filter(call => call[0] === 'git' && call[1] === 'push').length;
for (const [name, state] of [
  ['a desktop-only commit', {}],
  ['an unrelated backend commit', { unrelated: true }],
  ['a backend commit that left the number alone', { changed: true }],
  ['an uncommitted backend edit', { dirty: 'dotnet/Assembly.Engine/EngineCommands.cs' }],
] as const) test(`launch check: ${name} changes nothing while the number matches`, () => {
  const f = fixture(state);
  try {
    Object.assign(f.env, check);
    const result = f.run();
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /on version none \(host commit [a-f0-9]+\); nothing to do/);
    assert.equal(deploys(f), 0);
    assert.equal(pushes(f), 0);
  } finally { f.close(); }
});
for (const [name, state] of [
  ['a raised number', { version: '0.2.0', hostVersion: '0.1.0' }],
  ['a lower number, compared as numbers', { version: '0.10.0', hostVersion: '0.9.0' }],
  ['an old host that reports no number', { hostVersion: null }],
  ['a lane with nothing installed', { missing: true }],
] as const) test(`launch check: ${name} is behind, and the check itself deploys nothing`, () => {
  const f = fixture(state);
  try {
    Object.assign(f.env, check);
    const result = f.run();
    assert.equal(result.status, remoteBehindExit, result.stderr);
    assert.equal(deploys(f), 0);
    assert.equal(pushes(f), 0);
  } finally { f.close(); }
});
test('launch check: a managed lane with a new seed is behind even on the same number', () => {
  const f = fixture({ seedCohort: 'old' });
  try {
    Object.assign(f.env, check);
    f.env.ASSEMBLY_DEV_SYNC_REQUEST = JSON.stringify({ owner: { name: 'test-lane', number: 9 }, cohort: 'new' });
    assert.equal(f.run().status, remoteBehindExit);
    f.env.ASSEMBLY_DEV_SYNC_REQUEST = JSON.stringify({ owner: { name: 'test-lane', number: 9 }, cohort: 'old' });
    assert.equal(f.run().status, 0);
  } finally { f.close(); }
});
for (const [name, state, expected] of [
  ['a matching build', {}, 0],
  ['an unrelated backend commit', { unrelated: true }, 1],
  ['a change on the list', { changed: true }, 1],
  ['a release version bump', { version: '0.2.0' }, 1],
  ['an adapter packaging script change', { adapterChange: 'scripts/package-adapters.ts' }, 1],
  ['an adapter install script change', { adapterChange: 'dotnet/scripts/agents-file.sh' }, 1],
] as const) test(`the one update: ${name} ${expected ? 'deploys' : 'builds nothing'}`, () => {
  const f = fixture(state);
  try {
    Object.assign(f.env, update);
    const result = f.run();
    assert.equal(result.status, 0, result.stderr);
    assert.equal(deploys(f), expected);
    assert.match(result.stdout, expected ? /building and deploying/ : /no deploy/);
    if (!expected) assert.equal(pushes(f), 0);
    assert.ok(f.calls().filter(call=>call[0]==='ssh').every(call=>call.includes('workbox-test')));
    if (expected) assert.ok(f.calls().some(call => call[0] === 'ssh' && call.at(-1)?.includes('"waitForIdle":true')), 'a running session is never killed');
  } finally { f.close(); }
});
test('the one update: uncommitted desktop edits do not stop it; backend edits say the remote stays behind', () => {
  const f = fixture({ dirty: 'dotnet/Assembly.Desktop/Serve/DesktopBackend.cs' });
  try {
    Object.assign(f.env, update);
    let result = f.run();
    assert.equal(result.status, 0, result.stderr);
    writeFileSync(f.paths.state, JSON.stringify({ dirty: 'dotnet/Assembly.Engine/EngineCommands.cs' }));
    result = f.run();
    assert.equal(result.status, 1);
    assert.match(result.stderr, /uncommitted backend changes.*stays behind/);
    assert.equal(deploys(f), 0);
  } finally { f.close(); }
});
test('the dev serve command matches them exactly: an unrelated backend commit deploys when asked', () => {
  const f = fixture({ unrelated: true });
  try {
    const result = f.run();
    assert.equal(result.status, 0, result.stderr);
    assert.equal(deploys(f), 1);
  } finally { f.close(); }
});
test('a reserved lane with no installed unit enters the same deploy path', () => {
  const f=fixture({missing:true});
  try {const result=f.run();assert.equal(result.status,0,result.stderr);assert.match(result.stdout,/not installed.*building and deploying/)}
  finally {f.close()}
});
for (const [name, state, reason] of [
  ['unreachable host', { unreachable: true }, /Network is unreachable/],
  ['failed deploy', { changed: true, deployFail: true }, /test compiler error/],
  ['dirty backend', { dirty: 'dotnet/Assembly.Remote/Serve.cs' }, /uncommitted backend changes.*stays behind/],
  ['untracked backend', { untracked: 'dotnet/Assembly.Engine/New.cs' }, /uncommitted backend changes/],
] as const) test(name + ' reports the actual reason', () => {
  const f = fixture(state);
  try { const result=f.run(); assert.equal(result.status,1); assert.match(result.stderr,reason); }
  finally { f.close(); }
});
test('automatic sync refuses main before SSH or git', () => {
  const f = fixture();
  try { const result=f.run(['', '0']); assert.equal(result.status,1); assert.match(result.stderr,/refuses main/); assert.deepEqual(f.calls(),[]); }
  finally { f.close(); }
});
test('manual main still requires a clean checkout of origin/main before SSH', () => {
  const f=fixture({mainDirty:true});
  try {
    const result=f.run(['','0','HEAD','clean-origin-main']);
    assert.equal(result.status,1);assert.match(result.stderr,/main host requires a clean checkout of origin\/main/);
    assert.equal(f.calls().filter(call=>call[0]==='ssh').length,0);
  }finally{f.close()}
});
// A fake that waits for the test can't be left behind: it ends when the test removes its folder, and when the test
// host is gone, even one killed before it could clean up.
function holderExit(f: ReturnType<typeof fixture>, host: number) {
  const holder = spawn(process.execPath, [f.env.HOLDER!], { cwd: f.root, env: { ...f.env, TEST_HOST: String(host) }, stdio: 'ignore' });
  const exited = new Promise<number | null>(resolve => holder.on('exit', resolve));
  return { started: untilFile(f.paths.marker), exited };
}
test('a waiting holder ends when the test removes its folder', { timeout: 10_000 }, async () => {
  const f = fixture();
  const run = holderExit(f, process.pid);
  await run.started;
  f.close();
  assert.equal(await run.exited, 1);
});
test('a waiting holder ends when its test host is killed', { timeout: 10_000 }, async () => {
  const f = fixture();
  const host = spawn('sleep', ['60'], { stdio: 'ignore' });
  const hostGone = new Promise(resolve => host.on('exit', resolve));
  const run = holderExit(f, host.pid!);
  try {
    await run.started;
    host.kill('SIGKILL');
    await hostGone;
    assert.equal(await run.exited, 1);
  } finally { f.close(); }
});
function untilFile(file: string) {
  return new Promise<void>(resolve => {
    const watcher = watch(join(file, '..'), () => { if(existsSync(file)) { watcher.close(); resolve(); } });
    if(existsSync(file)) { watcher.close(); resolve(); }
  });
}
test('second launch during deployment cannot start another deploy', { timeout: 10_000 }, async () => {
  const f = fixture({ changed: true, hold: true });
  const started = untilFile(f.paths.marker);
  const first = spawn(process.execPath, f.args, { cwd: f.root, env: f.env, stdio: ['pipe','pipe','pipe'] });
  const finished = new Promise<number|null>(resolve=>first.on('close',resolve));
  try {
    await started;
    const second = f.run();
    assert.equal(second.status,1);
    assert.match(second.stderr,/another launch is already syncing/);
    writeFileSync(f.paths.release,'finish');
    assert.equal(await finished,0);
  } finally { first.kill('SIGTERM'); await finished; f.close(); }
});

// A real launch's scripts with fakes at the edges: git, ssh, dotnet, cargo and the tool installer.
function launchFixture(state: object) {
  const f = fixture(state);
  for (const directory of ['desktop', 'dotnet/scripts']) mkdirSync(join(f.root, directory), { recursive: true });
  for (const file of readdirSync(new URL('./', import.meta.url))) if (file.endsWith('.ts') && !file.endsWith('.test.ts')) copyFileSync(new URL(file, import.meta.url), join(f.root, 'scripts', file));
  writeFileSync(join(f.root, 'scripts/dev-toolchain.ts'), `import fs from 'node:fs';
export const packages = [];
export const toolKey = () => 'key';
export const keepNewer = versions => versions;
export const toolsCurrent = async () => true;
export const checkoutSources = async () => 'sources';
export const latestTools = async () => { fs.appendFileSync(process.env.CALLS, JSON.stringify(['tools','latest'])+'\\n'); return {}; };
export const ensureTools = async () => { fs.appendFileSync(process.env.CALLS, JSON.stringify(['tools','ensure'])+'\\n'); return { changed: true }; };
`);
  writeFileSync(join(f.root, 'scripts/lane-env.sh'), 'LANE_NAME=test-lane\nDATA="$PWD/profile"\nDESKTOP_PORT=18887\nTOKEN_FILE=test\nBOX_PORT=9887\nBOX_TOKEN_FILE=test\n');
  writeFileSync(join(f.root, 'dotnet/scripts/workbox.sh'), 'exec node scripts/workbox-serve.ts test-lane 9\n');
  writeFileSync(join(f.root, 'dotnet/scripts/agents-file.sh'), 'printf "[]" > "$2"\n');
  writeFileSync(join(f.root, 'bin/dotnet'), '#!/bin/sh\nexit 0\n', { mode: 0o755 });
  const desktop = join(f.root, 'desktop-opened');
  // The desktop build: it never ends until the test says so, so anything that finishes first didn't wait for it.
  writeFileSync(join(f.root, 'bin/cargo'), '#!/usr/bin/env node\n' + waiter(f.root, f.paths.release, desktop), { mode: 0o755 });
  const statusFile = join(f.root, 'profile/mac/remote-sync-state.json');
  const launch = () => {
    const opened = untilFile(desktop);
    const app = spawn(process.execPath, [join(f.root, 'scripts/app-dev.ts')], { cwd: f.root, env: f.env, stdio: ['pipe', 'pipe', 'pipe'] });
    let stderr = '';
    const heard = (pattern: RegExp) => new Promise<void>(resolve => { const look = () => { if (pattern.test(stderr)) { app.stderr.off('data', listen); resolve(); } }; const listen = (bytes: Buffer) => { stderr += bytes; look(); }; app.stderr.on('data', listen); look(); });
    const exited = new Promise<number | null>(resolve => app.on('close', resolve));
    return { app, opened, heard, exited, stderr: () => stderr };
  };
  return { ...f, statusFile, desktop, launch };
}
const toolCalls = (f: ReturnType<typeof fixture>) => f.calls().filter(call => call[0] === 'tools').length;
test('dev launch: a matching number does nothing, reads no tool versions, and the desktop opens', { timeout: 20_000 }, async () => {
  const f = launchFixture({ changed: true });
  const run = f.launch();
  try {
    await run.opened;
    await run.heard(/nothing to do/);
    assert.equal(deploys(f), 0);
    assert.equal(pushes(f), 0);
    assert.equal(toolCalls(f), 0);
    assert.deepEqual(JSON.parse(readFileSync(f.statusFile, 'utf8')), { updating: false, reason: null });
    writeFileSync(f.paths.release, 'close app');
    assert.equal(await run.exited, 0, run.stderr());
  } finally { run.app.kill('SIGTERM'); await run.exited; f.close(); }
});
test('dev launch: a different number deploys and updates tools in one shot while the desktop is still building, and the next launch builds nothing', { timeout: 20_000 }, async () => {
  const f = launchFixture({ version: '0.2.0' });
  let run = f.launch();
  try {
    await run.opened;
    await run.heard(/deployed backend/);
    assert.equal(existsSync(f.paths.release), false, 'the desktop is still building');
    assert.equal(deploys(f), 1);
    assert.equal(pushes(f), 1);
    assert.ok(toolCalls(f) > 0, 'adapters and CLIs update with it');
    assert.deepEqual(JSON.parse(readFileSync(f.statusFile, 'utf8')), { updating: false, reason: null });
    writeFileSync(f.paths.release, 'close app');
    assert.equal(await run.exited, 0, run.stderr());

    rmSync(f.paths.release); rmSync(f.desktop);
    writeFileSync(f.paths.state, JSON.stringify({ version: '0.2.0', hostVersion: '0.2.0' }));
    const before = toolCalls(f);
    run = f.launch();
    await run.opened;
    await run.heard(/nothing to do/);
    assert.equal(deploys(f), 1);
    assert.equal(toolCalls(f), before);
    writeFileSync(f.paths.release, 'close app');
    assert.equal(await run.exited, 0, run.stderr());
  } finally { run.app.kill('SIGTERM'); await run.exited; f.close(); }
});
for (const [state, reason] of [[{ unreachable: true }, /Network is unreachable/], [{ version: '0.2.0', deployFail: true }, /test compiler error/], [{ version: '0.2.0', startFail: true }, /failed to start/]] as const) {
  test('dev launch: the app stays open and is told why, on ' + Object.keys(state).at(-1), { timeout: 20_000 }, async () => {
    const f = launchFixture(state);
    const run = f.launch();
    try {
      await run.opened;
      await run.heard(new RegExp(reason.source + '.*log: .*\\.log'));
      assert.equal(run.app.exitCode, null);
      const shown = JSON.parse(readFileSync(f.statusFile, 'utf8'));
      assert.equal(shown.updating, false);
      assert.match(shown.reason, reason);
      writeFileSync(f.paths.release, 'close app');
      assert.equal(await run.exited, 0, run.stderr());
    } finally { run.app.kill('SIGTERM'); await run.exited; f.close(); }
  });
}
test('dev launch: closing the app stops a running update and frees its lock', { timeout: 20_000 }, async () => {
  const f = launchFixture({ version: '0.2.0', hold: true });
  const held = untilFile(f.paths.marker);
  const run = f.launch();
  try {
    await run.opened;
    await held;
    const pid = Number(readFileSync(f.paths.marker, 'utf8'));
    run.app.kill('SIGTERM');
    await run.exited;
    assert.throws(() => process.kill(pid, 0), { code: 'ESRCH' });
  } finally { run.app.kill('SIGTERM'); await run.exited; f.close(); }
});

test('backend identity follows real committed build inputs and ignores Rust-only commits', async () => {
  const {backendIdentity,requireCleanBackend}=await import('./dev-remote-identity.ts');
  const root=mkdtempSync(join(tmpdir(),'assembly-identity-'));
  const git=(...args:string[])=>{
    const result=spawnSync('git',args,{cwd:root,encoding:'utf8'});assert.equal(result.status,0,result.stderr);return result.stdout.trim();
  };
  const commit=()=>{git('add','.');git('-c','user.name=Fixture','-c','user.email=fixture@example.invalid','commit','-qm','fixture');return git('rev-parse','HEAD')};
  try {
    git('init','-q');
    mkdirSync(join(root,'dotnet/Assembly.Remote'),{recursive:true});mkdirSync(join(root,'desktop'),{recursive:true});
    writeFileSync(join(root,'dotnet/Assembly.Remote/Assembly.Remote.csproj'),'<Project />');
    writeFileSync(join(root,'desktop/view.rs'),'first');const first=commit();
    const identity=await backendIdentity(root,first);
    writeFileSync(join(root,'desktop/view.rs'),'second');const desktop=commit();
    assert.equal(await backendIdentity(root,desktop),identity);
    mkdirSync(join(root,'scripts'));
    writeFileSync(join(root,'scripts/dev-remote-build-profile.ts'),'export const args = ["linux-x64"];');
    await assert.rejects(()=>requireCleanBackend(root),/uncommitted/);
    const profile=commit();assert.notEqual(await backendIdentity(root,profile),identity);
    writeFileSync(join(root,'dotnet/Directory.Build.props'),'<Project />');
    await assert.rejects(()=>requireCleanBackend(root),/uncommitted/);
    const backend=commit();assert.notEqual(await backendIdentity(root,backend),identity);
    writeFileSync(join(root,'dotnet/Assembly.Remote/new.cs'),'pending');git('add','.');
    await assert.rejects(()=>requireCleanBackend(root),/uncommitted/);
  } finally {rmSync(root,{recursive:true,force:true})}
});

test('cancelling a launcher ends its owned SSH deployment and releases the kernel lock', {timeout:10_000}, async()=>{
  const f=fixture({changed:true,hold:true});
  const started=untilFile(f.paths.marker);
  const first=spawn(process.execPath,f.args,{cwd:f.root,env:f.env,stdio:['pipe','pipe','pipe']});
  const ended=new Promise<number|null>(resolve=>first.on('close',resolve));
  try {
    await started;
    const pid=Number(readFileSync(f.paths.marker,'utf8'));
    first.kill('SIGTERM');
    assert.equal(await ended,1);
    assert.throws(()=>process.kill(pid,0),{code:'ESRCH'});
    writeFileSync(f.paths.release,'complete');
    const next=f.run();assert.equal(next.status,0,next.stderr);
  } finally {first.kill('SIGTERM');await ended;f.close()}
});

test('SSH stdin EOF cancels the remote owner and its build child', {timeout:10_000}, async()=>{
  const root=mkdtempSync(join(tmpdir(),'assembly-remote-owner-'));
  const file=join(root,'child-pid');
  const processModule=new URL('./dev-remote-process.ts',import.meta.url).href;
  writeFileSync(join(root,'owner.ts'),`import {lifetime,command} from ${JSON.stringify(processModule)};\nconst owner=lifetime(true);try{await command(process.execPath,['-e',${JSON.stringify(`require('fs').writeFileSync(${JSON.stringify(file)},String(process.pid));process.stdin.resume()`)}],{signal:owner.signal})}catch(e){console.log(e.message)}finally{owner.close()}`);
  const started=untilFile(file);
  const remote=spawn(process.execPath,[join(root,'owner.ts')],{stdio:['pipe','pipe','pipe']});
  const ended=new Promise<number|null>(resolve=>remote.on('close',resolve));
  let out='';remote.stdout.on('data',data=>out+=data);
  try {
    await started;
    const pid=Number(readFileSync(file,'utf8'));
    remote.stdin.end();
    assert.equal(await ended,0);
    assert.match(out,/cancelled with its launcher/);
    assert.throws(()=>process.kill(pid,0),{code:'ESRCH'});
  }finally {remote.kill('SIGTERM');await ended;rmSync(root,{recursive:true,force:true})}
});

for (const [name, state, matches] of [
  ['a desktop-only commit', {}, true],
  ['an unrelated backend commit', { unrelated: true }, false],
  ['a change on the list', { changed: true }, false],
] as const) test(`before a PR: ${name} ${matches ? 'passes' : 'requires an exact match and names both commits'}`, () => {
  const f = fixture(state);
  try {
    copyFileSync(new URL('./check-remote.ts', import.meta.url), join(f.root, 'scripts/check-remote.ts'));
    const result = spawnSync(process.execPath, [join(f.root, 'scripts/check-remote.ts'), 'test-lane', '9'], { cwd: f.root, env: f.env, encoding: 'utf8' });
    assert.equal(result.status, matches ? 0 : 1, result.stderr);
    assert.match(result.stdout + result.stderr, new RegExp(`remote ${previous}; desktop ${current}`));
    assert.ok(f.calls().every(call => call[0] !== 'git' || call[1] !== 'push'));
    assert.ok(f.calls().every(call => call[0] !== 'ssh' || !call.at(-1)?.startsWith('flock ')));
  } finally { f.close(); }
});

for (const adopted of [false, true]) test(`managed ${adopted ? 'adopted' : 'creator'} lane with current backend, tools and seed skips transfer/build/deploy`, async () => {
  const { toolKey, packages } = await import('./dev-toolchain.ts');
  const versions = Object.fromEntries(packages.map(name => [name, '1.2.3']));
  const cohort = 'd'.repeat(64), key = toolKey(versions as any, sources, 'linux-x64');
  const f = fixture({ toolsKey: key, seedCohort: cohort });
  try {
    f.env.ASSEMBLY_DEV_SYNC_REQUEST = JSON.stringify({ owner: { name: 'test-lane', number: 9 }, versions, cohort, adopted });
    f.env.ASSEMBLY_DEV_REMOTE_SYNC = '1';
    const result = f.run();
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /already matches.*no deploy/);
    assert.equal(f.calls().filter(call => call[0] === 'git' && call[1] === 'push').length, 0);
    assert.equal(f.calls().filter(call => call[0] === 'ssh' && call.at(-1)?.startsWith('flock ')).length, 0);
  } finally { f.close(); }
});
test('a copied managed identity refuses before SSH and an adopted outdated lane cannot deploy to its shared remote', () => {
  const f = fixture();
  try {
    f.env.ASSEMBLY_DEV_SYNC_REQUEST = JSON.stringify({ owner: { name: 'another', number: 9 } });
    let result = f.run(); assert.equal(result.status, 1); assert.match(result.stderr, /lane identity differ/);
    assert.equal(f.calls().filter(call => call[0] === 'ssh').length, 0);
    f.env.ASSEMBLY_DEV_SYNC_REQUEST = JSON.stringify({ owner: { name: 'test-lane', number: 9 }, versions: {}, cohort: 'needs-seed', adopted: true });
    result = f.run(); assert.equal(result.status, 1); assert.match(result.stderr, /creator to update/);
    assert.equal(f.calls().filter(call => call[0] === 'git' && call[1] === 'push').length, 0);
  } finally { f.close(); }
});
for (const [name, env, state] of [
  ['the launch check', check, {}],
  ['the one update', update, {}],
  ['the one update with a new seed', update, { seedCohort: 'old' }],
  ['serving by hand', {}, {}],
  ['the one update, compared as numbers', update, { version: '0.9.0', hostVersion: '0.10.0' }],
] as const) test(`${name}: a remote on a higher number gets nothing, and it says this checkout is the side behind`, () => {
  const f = fixture({ version: '0.1.0', hostVersion: '0.2.0', ...state });
  try {
    Object.assign(f.env, env);
    if ('seedCohort' in state) f.env.ASSEMBLY_DEV_SYNC_REQUEST = JSON.stringify({ owner: { name: 'test-lane', number: 9 }, cohort: 'new' });
    const result = f.run();
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /newer than this checkout's [\d.]+, so nothing was deployed\. This checkout is the side that's behind: pull to update it\./);
    assert.equal(deploys(f), 0);
    assert.equal(pushes(f), 0);
  } finally { f.close(); }
});
test('launch check: a number that can\'t be compared decides nothing', () => {
  const f = fixture({ version: '0.1.0', hostVersion: 'garbled' });
  try {
    Object.assign(f.env, check);
    const result = f.run();
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /can't be compared/);
    assert.equal(deploys(f), 0);
  } finally { f.close(); }
});
