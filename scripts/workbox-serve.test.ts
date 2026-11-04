import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync, watch, existsSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';

const current = 'a'.repeat(40), previous = 'b'.repeat(40);
const launcher = createHash('sha256').update('launcher\n').digest('hex');
const fakeGit = `#!/usr/bin/env node
import fs from 'node:fs';
const args=process.argv.slice(2), state=JSON.parse(fs.readFileSync(process.env.STATE,'utf8'));
fs.appendFileSync(process.env.CALLS, JSON.stringify(['git',...args])+'\\n');
switch(args[0]) {
 case 'rev-parse': console.log('${current}'); break;
 case 'diff': console.log(state.dirty ?? ''); break;
 case 'ls-files': console.log(state.untracked ?? ''); break;
 case 'ls-tree': console.log('100644 blob '+(state.changed && args.at(-1)==='${current}'?'2':'1').repeat(40)+'\\tdotnet/Assembly.Remote/Assembly.Remote.csproj'); break;
 case 'show': process.stdout.write('launcher\\n'); break;
 case 'push': break;
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
 if (state.deployFail) {console.error('dotnet publish failed: test compiler error');process.exit(1)}
 if(state.hold) {
  const child=spawn('flock',['-n','-E','75',process.env.LOCK,process.execPath,process.env.HOLDER],{stdio:['inherit','inherit','inherit']});
  child.on('close',code=>process.exit(code));
 } else console.log('deployed backend; authenticated host commit ${current}');
} else if(state.missing) console.log(JSON.stringify({missing:true}));
else console.log(JSON.stringify({commit:'${previous}',engine:'test-engine',dll:'/lane/bin/old/backend/Assembly.Remote.dll',pid:'123',unitSha:'f'.repeat(64),adapterOK:true,launcherSha:'${launcher}'}));
`;
function fixture(state: object = {}) {
  const root = mkdtempSync(join(tmpdir(), 'assembly-devsync-'));
  for (const dir of ['scripts', 'bin']) mkdirSync(join(root, dir));
  for (const file of ['workbox-serve.ts', 'dev-remote-process.ts', 'dev-remote-identity.ts', 'dev-remote-probe.ts'])
    copyFileSync(new URL(file, import.meta.url), join(root, 'scripts', file));
  const paths = { state: join(root, 'state'), calls: join(root, 'calls'), marker: join(root, 'started'), release: join(root, 'release') };
  writeFileSync(paths.state, JSON.stringify(state));
  writeFileSync(paths.calls, '');
  writeFileSync(join(root, 'bin/git'), fakeGit, { mode: 0o755 });
  writeFileSync(join(root, 'bin/ssh'), fakeSsh, { mode: 0o755 });
  const holder = join(root, 'holder.ts');
  writeFileSync(holder, `import fs from 'node:fs';\nconst finish=()=>{if(fs.existsSync(${JSON.stringify(paths.release)})){watcher.close();console.log('deploy completed')}};const watcher=fs.watch(${JSON.stringify(root)},finish);fs.writeFileSync(${JSON.stringify(paths.marker)},String(process.pid));finish();`);
  const env = { ...process.env, PATH: join(root, 'bin') + ':' + process.env.PATH, STATE: paths.state, CALLS: paths.calls, LOCK: join(root, 'lock'), HOLDER: holder };
  const args = [join(root, 'scripts/workbox-serve.ts'), 'test-lane', '9'];
  return { root, paths, env, args, run: (extra: string[] = []) => spawnSync(process.execPath, extra.length ? [args[0], ...extra] : args, { cwd: root, env, encoding: 'utf8' }),
    calls: () => readFileSync(paths.calls, 'utf8').trim().split('\n').filter(Boolean).map(line => JSON.parse(line) as string[]),
    close: () => rmSync(root, { recursive: true, force: true }) };
}
for (const changed of [false, true]) test(changed ? 'backend content changes deploy through the lane lock' : 'a desktop-only commit reuses the running backend', () => {
  const f = fixture({ changed });
  try {
    const result = f.run();
    assert.equal(result.status, 0, result.stderr);
    const deploys = f.calls().filter(call => call[0] === 'ssh' && call.at(-1)?.startsWith('flock '));
    assert.equal(deploys.length, changed ? 1 : 0);
    assert.match(result.stdout, changed ? /building and deploying/ : /no deploy/);
    assert.ok(f.calls().filter(call=>call[0]==='ssh').every(call=>call.includes('workbox-test')));
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

for (const [state, reason] of [[{unreachable:true}, /Network is unreachable/], [{changed:true,deployFail:true}, /test compiler error/]] as const) {
  test('app opens during remote sync and reports ' + Object.keys(state).at(-1), { timeout: 10_000 }, async () => {
    const f=fixture(state);
    for(const directory of ['desktop','dotnet/scripts']) mkdirSync(join(f.root,directory),{recursive:true});
    for(const file of ['app-dev.ts','app-dev-remote.ts','seed-dev.ts']) copyFileSync(new URL(file,import.meta.url),join(f.root,'scripts',file));
    writeFileSync(join(f.root,'scripts/lane-env.sh'), 'LANE_NAME=test-lane\nDATA="$PWD/profile"\nDESKTOP_PORT=18887\nTOKEN_FILE=test\nBOX_PORT=9887\nBOX_TOKEN_FILE=test\n');
    writeFileSync(join(f.root,'dotnet/scripts/workbox.sh'), `exec node scripts/workbox-serve.ts test-lane 9\n`);
    writeFileSync(join(f.root,'dotnet/scripts/agents-file.sh'), 'printf "[]" > "$2"\n');
    writeFileSync(join(f.root,'bin/dotnet'),'#!/bin/sh\nexit 0\n',{mode:0o755});
    writeFileSync(join(f.root,'bin/cargo'),`#!/usr/bin/env node\nimport fs from 'node:fs';\nconst watcher=fs.watch(${JSON.stringify(f.root)},()=>{if(fs.existsSync(${JSON.stringify(f.paths.release)}))watcher.close()});fs.writeFileSync(${JSON.stringify(f.paths.marker)},'app open');`,{mode:0o755});
    const opened=untilFile(f.paths.marker);
    const app=spawn(process.execPath,[join(f.root,'scripts/app-dev.ts')],{cwd:f.root,env:f.env,stdio:['pipe','pipe','pipe']});
    let stderr='';
    const reported=new Promise<void>(resolve=>app.stderr.on('data',bytes=>{stderr+=bytes;if(reason.test(stderr))resolve()}));
    const exited=new Promise<number|null>(resolve=>app.on('close',resolve));
    try {
      await opened;
      assert.equal(app.exitCode,null,'app must stay open independently of sync');
      await reported;
      assert.match(stderr,/log: .*\.log/);
      writeFileSync(f.paths.release,'close app');
      assert.equal(await exited,0,stderr);
    } finally { app.kill('SIGTERM'); await exited; f.close(); }
  });
}

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

test('closing app-dev during adapter setup ends the installer and its remote sync', {timeout:10_000}, async()=>{
  const f=fixture({changed:true,hold:true});
  for(const directory of ['desktop','dotnet/scripts']) mkdirSync(join(f.root,directory),{recursive:true});
  for(const file of ['app-dev.ts','app-dev-remote.ts','seed-dev.ts']) copyFileSync(new URL(file,import.meta.url),join(f.root,'scripts',file));
  writeFileSync(join(f.root,'scripts/lane-env.sh'),'LANE_NAME=test-lane\nDATA="$PWD/profile"\nDESKTOP_PORT=18887\nTOKEN_FILE=test\nBOX_PORT=9887\nBOX_TOKEN_FILE=test\n');
  writeFileSync(join(f.root,'dotnet/scripts/workbox.sh'),'exec node scripts/workbox-serve.ts test-lane 9\n');
  const installerPid=join(f.root,'installer-pid'), holder=join(f.root,'installer.ts');
  writeFileSync(holder,`import fs from 'node:fs';fs.writeFileSync(${JSON.stringify(installerPid)},String(process.pid));process.stdin.resume()`);
  writeFileSync(join(f.root,'dotnet/scripts/agents-file.sh'),'exec node "$INSTALL_HOLDER"\n');
  writeFileSync(join(f.root,'bin/dotnet'),'#!/bin/sh\nexit 0\n',{mode:0o755});
  const remoteStarted=untilFile(f.paths.marker), installStarted=untilFile(installerPid);
  const app=spawn(process.execPath,[join(f.root,'scripts/app-dev.ts')],{cwd:f.root,env:{...f.env,INSTALL_HOLDER:holder},stdio:['pipe','pipe','pipe']});
  const ended=new Promise<number|null>(resolve=>app.on('close',resolve));
  try {
    await Promise.all([remoteStarted,installStarted]);
    const pids=[Number(readFileSync(f.paths.marker,'utf8')),Number(readFileSync(installerPid,'utf8'))];
    app.kill('SIGTERM');assert.equal(await ended,1);
    for(const pid of pids) assert.throws(()=>process.kill(pid,0),{code:'ESRCH'});
  }finally{app.kill('SIGTERM');await ended;f.close()}
});
