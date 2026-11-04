import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync, watch, existsSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { adapterSources, digestContract, digestInputs, remoteContractInput, sourcesDigest } from './dev-remote-identity.ts';

const current = 'a'.repeat(40), previous = 'b'.repeat(40);
const hostEntries = ['100644 blob ' + '1'.repeat(40) + '\tdotnet/Assembly.Remote/Assembly.Remote.csproj', '100644 blob ' + '3'.repeat(40) + '\tdotnet/Assembly.Infrastructure/Notion/NotionCalls.cs'];
const previousIdentity = digestInputs(hostEntries, 'none'), previousContract = digestContract(hostEntries.filter(line => remoteContractInput(line.split('\t')[1])), 'none');
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
 case 'show': process.stdout.write(args.at(-1).endsWith('Cargo.toml') ? (state.version ? 'version = "'+state.version+'"\\n' : '') : args.at(-1).endsWith(state.adapterChange ?? '?') ? 'changed\\n' : 'launcher\\n'); break;
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
else console.log(JSON.stringify({commit:'${previous}',backendIdentity:'${previousIdentity}',remoteContract:state.hostContract===undefined?'${previousContract}':state.hostContract,engine:'test-engine',dll:'/lane/bin/old/backend/Assembly.Remote.dll',pid:'123',unitSha:'f'.repeat(64),adapterOK:true,launcherSha:'${launcher}',toolsKey:state.toolsKey,adapterSources:'${sources}',seedCohort:state.seedCohort}));
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
  writeFileSync(holder, `import fs from 'node:fs';\nconst finish=()=>{if(fs.existsSync(${JSON.stringify(paths.release)})){watcher.close();console.log('deploy completed')}};const watcher=fs.watch(${JSON.stringify(root)},finish);fs.writeFileSync(${JSON.stringify(paths.marker)},String(process.pid));finish();`);
  const env: NodeJS.ProcessEnv = { ...process.env, PATH: join(root, 'bin') + ':' + process.env.PATH, STATE: paths.state, CALLS: paths.calls, LOCK: join(root, 'lock'), HOLDER: holder };
  const args = [join(root, 'scripts/workbox-serve.ts'), 'test-lane', '9'];
  return { root, paths, env, args, run: (extra: string[] = []) => spawnSync(process.execPath, extra.length ? [args[0], ...extra] : args, { cwd: root, env, encoding: 'utf8' }),
    calls: () => readFileSync(paths.calls, 'utf8').trim().split('\n').filter(Boolean).map(line => JSON.parse(line) as string[]),
    close: () => rmSync(root, { recursive: true, force: true }) };
}
const update = { ASSEMBLY_DEV_REMOTE_SYNC: '1' };
const deploys = (f: ReturnType<typeof fixture>) => f.calls().filter(call => call[0] === 'ssh' && call.at(-1)?.startsWith('flock ')).length;
for (const [name, state, expected] of [
  ['a desktop-only commit', {}, 0],
  ['an unrelated backend commit', { unrelated: true }, 0],
  ['a change on the list', { changed: true }, 1],
  ['a release version bump', { version: '0.2.0' }, 1],
  ['an adapter packaging script change', { adapterChange: 'scripts/package-adapters.ts' }, 1],
  ['an adapter install script change', { adapterChange: 'dotnet/scripts/agents-file.sh' }, 1],
] as const) test(`deploy remote: ${name} ${expected ? 'deploys' : 'builds nothing'}`, () => {
  const f = fixture(state);
  try {
    Object.assign(f.env, update);
    const result = f.run();
    assert.equal(result.status, 0, result.stderr);
    assert.equal(deploys(f), expected);
    assert.match(result.stdout, expected ? /building and deploying/ : /no deploy/);
    if (!expected) assert.equal(f.calls().filter(call => call[0] === 'git' && call[1] === 'push').length, 0);
    assert.ok(f.calls().filter(call=>call[0]==='ssh').every(call=>call.includes('workbox-test')));
  } finally { f.close(); }
});
test('deploy remote: a change on the list deploys once, and the next run builds nothing', () => {
  const f = fixture({ changed: true });
  try {
    Object.assign(f.env, update);
    assert.equal(f.run().status, 0);
    assert.equal(deploys(f), 1);
    const deployed = digestContract(['100644 blob ' + '2'.repeat(40) + '\tdotnet/Assembly.Remote/Assembly.Remote.csproj'], 'none');
    writeFileSync(f.paths.state, JSON.stringify({ changed: true, hostContract: deployed }));
    const next = f.run();
    assert.equal(next.status, 0, next.stderr);
    assert.equal(deploys(f), 1);
    assert.match(next.stdout, /no deploy/);
  } finally { f.close(); }
});
test('deploy remote: uncommitted unrelated backend edits do not stop it; edits on the list say the remote stays behind', () => {
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
test('an old host that reports no list identity gets one deploy', () => {
  const f = fixture({ hostContract: null });
  try {
    Object.assign(f.env, update);
    assert.equal(f.run().status, 0);
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
