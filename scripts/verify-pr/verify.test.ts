import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync, existsSync, symlinkSync, readdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';

const source = import.meta.dirname;
const sha = 'a'.repeat(40);
function fixture(t: any, head = sha, base = 'b'.repeat(40)) {
  const root = mkdtempSync(join(tmpdir(), 'verify-pr-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const bin = join(root, 'bin'); mkdirSync(bin);
  const config = { repository: 'jcoble/Assembly', owner: 'jcoble', state: join(root, 'state'),
    ownerHome:root, workerPath:bin+':'+process.env.PATH,evidence:join(bin,'evidence'),
    clone: join(root, 'clone'), workerHome: join(root, 'worker'), worker: 'verify',
    worktrees: join(root, 'worktrees'), laneNumber: 3,
    runtimeSeconds: 7200, minimumMemoryMB: 2048, livePorts: [5000] };
  for (const p of [config.state, config.clone, config.workerHome, config.worktrees]) mkdirSync(p);
  const configPath = join(root, 'config.json');
  const log = join(root, 'calls.jsonl');
  const responses = join(root, 'responses.json');
  let replies: Record<string, any> = {
    'repos/jcoble/Assembly/pulls/12': { number: 12, state: 'open', draft: false,
      user: { login: 'jcoble' }, head: { sha: head, repo: { full_name: 'jcoble/Assembly' } },
      base: { sha: base }, title: 'TSK-1667', body: 'Task https://app.notion.com/p/40e394b0689d81e7ad28d008bf580fe6' },
    'repos/jcoble/Assembly/issues/12/comments?per_page=100&page=1': [],
    ['repos/jcoble/Assembly/commits/' + head + '/check-runs']: { check_runs: [] },
    ['repos/jcoble/Assembly/commits/' + sha + '/statuses?per_page=100&page=1']: [],
    'repos/jcoble/Assembly/issues/comments/99': {user:{login:'jcoble'},issue_url:'https://api.github.com/repos/jcoble/Assembly/issues/12',body:'@verify'},
  };
  const fake = `#!${process.execPath}
const fs=require('node:fs'),path=require('node:path');
const name=path.basename(process.argv[1]),args=process.argv.slice(2);
fs.appendFileSync(process.env.CALLS,JSON.stringify({name,args})+'\n');
const replies=JSON.parse(fs.readFileSync(process.env.REPLIES));
if(name==='gh') { const endpoint=args.find(x=>x.startsWith('repos/')); const r=replies[endpoint];
 if(process.env.FAIL_POST==='1' && args.includes('POST')) process.exit(1);
 process.stdout.write(JSON.stringify(r??{})); }
if(name==='git') { if(args.includes('--format=%B')) console.log(process.env.TRAILERS||'Committed-by: Codex SOL 6.1');
 if(args.includes('rev-parse')&&args.includes('HEAD')) console.log('${head}');
 if(args.includes('worktree')&&args.includes('add')) { fs.mkdirSync(args.at(-2)); fs.mkdirSync(path.join(args.at(-2),'app')); }
 if(args.includes('worktree')&&args.includes('remove')) fs.rmSync(args.at(-1),{recursive:true});
 if(args.includes('--name-only')) {
  if(process.env.DIFF_REPO) { const r=require('node:child_process').spawnSync(process.env.REAL_GIT,['-C',process.env.DIFF_REPO,...args.slice(2)],{encoding:'utf8'});process.stdout.write(r.stdout||'');process.exit(r.status??1); }
  console.log(process.env.DIFF_FILES||'src/code.ts'); }
 if(args.includes('status')) { const dirty=(process.env.DIRTY||'').split('\\n').filter(x=>!args.includes(':!evidence')||!/^.. evidence\\//.test(x)); process.stdout.write(dirty.join('\\n')); } }
if(name==='ps') console.log(process.env.BUILDS||'');
if(name==='id') console.log(args.includes('-nG')?'verify':'1002');
if(name==='free') console.log('Mem: 16000 1000 15000 0 0 '+(process.env.MEMORY||'15000'));
if(name==='systemctl'&&args.includes('show')) console.log(process.env.SERVICE_LOAD||'not-found');
if(name==='systemd-run'&&process.env.FAIL_START==='1') process.exit(1);
if(name==='flock') { const r=require('node:child_process').spawnSync(args[2],args.slice(3),{env:process.env,encoding:'utf8'});process.stdout.write(r.stdout||'');process.stderr.write(r.stderr||'');process.exit(r.status??1); }
if(name==='evidence') console.log('https://drive.google.com/file/d/test-proof/view');
if(name==='sudo') { const child=require('node:child_process'); const index=args.indexOf('env');
 if(args.includes('/bin/sh')&&args.includes('verify-read-audit')) {console.log(process.env.AUDIT_READABLE==='1'?'READABLE':'DENIED');process.exit(0);}
 if(args.includes('-C')&&(args.includes('iptables')||args.includes('ip6tables'))&&process.env.FAIL_FIREWALL==='1') process.exit(1);
 if(index>=0) { const rest=args.slice(index+2); const env={}; while(rest[0]?.includes('=')){const x=rest.shift();const p=x.indexOf('=');env[x.slice(0,p)]=x.slice(p+1);}
 for(const key of ['CALLS','REPLIES','TRAILERS','DIRTY','AGENT_RESULT','DIFF_FILES','DIFF_REPO','REAL_GIT']) if(process.env[key]) env[key]=process.env[key];
 const r=child.spawnSync(rest.shift(),rest,{env,encoding:'utf8'}); process.stdout.write(r.stdout||'');process.stderr.write(r.stderr||'');process.exit(r.status??1); } }
if(name==='codex'||name==='claude') { const writable=args.flatMap((x,i)=>x==='--add-dir'?[args[i+1]]:[]);
 fs.appendFileSync(process.env.CALLS,JSON.stringify({name:'agent-environment',env:process.env,stdin:fs.readFileSync(0,'utf8'),directories:writable.map(p=>({path:p,exists:fs.existsSync(p)&&fs.statSync(p).isDirectory()}))})+'\n');
 fs.writeFileSync(process.env.VERIFY_RESULT,process.env.AGENT_RESULT||'{}'); }
`;
  // JSON.stringify ensures the generated JavaScript contains a newline escape, not a literal newline.
  const executable = fake.replaceAll("+'\n'", "+'\\n'");
  for (const name of ['gh','git','systemd-run','systemctl','sudo','codex','claude','ps','free','evidence','flock','id','pnpm'])
    writeFileSync(join(bin, name), executable, { mode: 0o755 });
  function save() { writeFileSync(configPath, JSON.stringify(config)); writeFileSync(responses, JSON.stringify(replies)); }
  function run(file: string, args: string[] = [], env: Record<string,string> = {}) {
    save(); return spawnSync(process.execPath, [join(source,file), ...args], { cwd: root,
      encoding:'utf8', env: {...process.env, HOME:root, PATH:bin+':'+process.env.PATH,
        VERIFY_CONFIG:configPath, CALLS:log, REPLIES:responses, ...env} });
  }
  const calls = () => existsSync(log) ? readFileSync(log,'utf8').trim().split('\n').map(x=>JSON.parse(x)) : [];
  function result(value: any) {
    const dir = join(config.state, 'run-12-test'); mkdirSync(dir);
    const output = join(config.workerHome, 'result'); mkdirSync(output);
    writeFileSync(join(dir,'context.json'), JSON.stringify({pr:12, sha:head, base, started:'2026-11-04T00:00:00Z', output,
      worktree:join(config.worktrees,'verify-pr-12-'+head+'-run-12-test'), task:'TSK-1667', title:'Test', body:'', ci:{check_runs:[]}}));
    if(value !== undefined) writeFileSync(join(output,'result.json'), typeof value==='string'? value:JSON.stringify(value));
    return dir;
  }
  return {root, config, replies, run, calls, result};
}

for (const input of ['', '12; touch bad', '12\n13', '-12', '12 34 56', '$(id)', '１２']) {
  test('forced command rejects '+JSON.stringify(input), t=> {
    const f=fixture(t), r=f.run('dispatch.ts',[],{SSH_ORIGINAL_COMMAND:input});
    assert.notEqual(r.status,0); assert.deepEqual(f.calls(),[]);
  });
}
for (const [name, change] of [
  ['fork', (p:any)=>p.head.repo.full_name='evil/Assembly'],
  ['non-author', (p:any)=>p.user.login='other'],
  ['closed', (p:any)=>p.state='closed'],
] as const) test('rejects '+name+' PR', t=> {
  const f=fixture(t); change(f.replies['repos/jcoble/Assembly/pulls/12']);
  assert.notEqual(f.run('dispatch.ts',[],{SSH_ORIGINAL_COMMAND:'12'}).status,0);
  assert.ok(f.calls().every(x=>['gh','flock'].includes(x.name)));
});
for(const input of ['12','12 99']) test('draft is always skipped for '+input,t=>{
  const f=fixture(t); f.replies['repos/jcoble/Assembly/pulls/12'].draft=true;
  const r=f.run('dispatch.ts',[],{SSH_ORIGINAL_COMMAND:input}); assert.equal(r.status,0,r.stderr);
  assert.match(r.stdout,/Draft skipped/);
  assert.ok(!f.calls().some(x=>['systemctl','systemd-run'].includes(x.name)||x.args.includes('POST')));
});
test('dispatch resolves SHA, stops only that PR unit, posts pending then starts detached unit',t=>{
  const f=fixture(t); writeFileSync(join(f.config.state,'units.json'),JSON.stringify(['verify-pr-12.service'])); const r=f.run('dispatch.ts',[],{SSH_ORIGINAL_COMMAND:'12'});
  assert.equal(r.status,0,r.stderr);
  const calls=f.calls(); const stop=calls.findIndex(x=>x.name==='systemctl');
  assert.equal(calls[0].name,'flock');
  assert.deepEqual(calls[stop].args,['--user','stop','verify-pr-12.service']);
  const pending=calls.findIndex(x=>x.args.includes('state=pending'));
  assert.ok(stop<pending);
  const unit=calls.find(x=>x.name==='systemd-run');
  assert.ok(unit.args.includes('--unit=verify-pr-12'));
  assert.ok(unit.args.some((x:string)=>x.startsWith('--property=ExecStopPost=')));
  assert.ok(unit.args.includes('--property=RuntimeMaxSec=7200'));
  assert.ok(!r.stdout.includes('SECRET_SENTINEL'));
});
for (const [name, comment] of [
  ['other author',{user:{login:'other'},issue_url:'https://api.github.com/repos/jcoble/Assembly/issues/12',body:'@verify'}],
  ['other PR',{user:{login:'jcoble'},issue_url:'https://api.github.com/repos/jcoble/Assembly/issues/13',body:'@verify'}],
] as const) test('rerun rejects '+name,t=>{
  const f=fixture(t);
  f.replies['repos/jcoble/Assembly/issues/comments/99']=comment;
  assert.notEqual(f.run('dispatch.ts',[],{SSH_ORIGINAL_COMMAND:'12 99'}).status,0);
  assert.ok(!f.calls().some(x=>x.name==='systemd-run'));
});
for(const state of [undefined,'error','success','failure','pending']) test('comment checks latest acceptance status '+state,t=>{
  const f=fixture(t);
  writeFileSync(join(f.config.state,'units.json'),JSON.stringify(['verify-pr-12.service']));
  f.replies['repos/jcoble/Assembly/commits/'+sha+'/statuses?per_page=100&page=1']=[
    {context:'other',state:'success'},
    ...(state?[{context:'verify/acceptance',state,description:'needs Mac'}]:[]),
    {context:'other',state:'failure'},
    ...(state?[{context:'verify/acceptance',state:'error'}]:[]),
  ];
  const r=f.run('dispatch.ts',[],{SSH_ORIGINAL_COMMAND:'12 99'}); assert.equal(r.status,0,r.stderr);
  const starts=state===undefined||state==='error';
  assert.equal(f.calls().some(x=>x.name==='systemd-run'),starts);
  if(!starts) {
    assert.match(r.stdout,/already verified or running/);
    assert.ok(!f.calls().some(x=>x.name==='systemctl'||x.args.includes('POST')||x.args.includes('PATCH')));
  }
});
test('comment finds an acceptance status beyond the first page',t=>{
  const f=fixture(t),endpoint='repos/jcoble/Assembly/commits/'+sha+'/statuses?per_page=100&page=';
  f.replies[endpoint+'1']=Array.from({length:100},()=>({context:'other',state:'error'}));
  f.replies[endpoint+'2']=[{context:'verify/acceptance',state:'success'}];
  const r=f.run('dispatch.ts',[],{SSH_ORIGINAL_COMMAND:'12 99'});assert.equal(r.status,0,r.stderr);
  assert.match(r.stdout,/already verified or running/);
  assert.ok(!f.calls().some(x=>x.name==='systemctl'||x.name==='systemd-run'||x.args.includes('POST')));
});

const pass = {sha, summary:'Verified', behaviors:[{name:'native edit',state:'PASS',details:'Saved correctly',proof:['after.png']}],
  tests:[{name:'edit tests',listed:3,ran:3,passed:3,receipt:'tests.log'}], proof:[{file:'after.png',description:'Saved text'}]};
for (const [name, value, expected] of [
  ['missing',undefined,'error'],['malformed','{','error'],['wrong SHA',{...pass,sha:'c'.repeat(40)},'error'],
  ['no proof',{...pass,proof:[]},'failure'],
  ['zero tests',{...pass,tests:[{name:'empty',listed:0,ran:0,passed:0}]},'failure'],
  ['needs Mac',{...pass,proof:[],behaviors:[{name:'layout',state:'BLOCKED',details:'needs Mac',proof:[]}]},'pending'],
  ['blocked phone',{...pass,behaviors:[{name:'phone',state:'BLOCKED',details:'no device',proof:[]}]},'failure'],
  ['docs only',{sha,summary:'Docs',behaviors:[{name:'docs',state:'NOT AFFECTED',details:'only prose',proof:[]}],tests:[],proof:[]},'success'],
] as const) test('post '+name+' => '+expected,t=>{
  const f=fixture(t), dir=f.result(value); writeFileSync(join(f.config.workerHome,'result','tests.log'),'edit tests'); const r=f.run('post.ts',[dir],name==='docs only'?{DIFF_FILES:'README.md'}:{});
  assert.equal(r.status,0,r.stderr);
  assert.ok(f.calls().some(x=>x.args.includes('state='+expected)),'expected status '+expected);
  assert.ok(existsSync(join(dir,'posted.json')));
});
test('uploads proof, posts once, updates the marked comment, cleans only a clean lane',t=>{
  const f=fixture(t), dir=f.result(pass);
  writeFileSync(join(f.config.workerHome,'result','tests.log'),'edit tests');
  writeFileSync(join(f.config.workerHome,'result','after.png'),'capture');
  f.replies['repos/jcoble/Assembly/issues/12/comments?per_page=100&page=1']=[{id:91,user:{login:'jcoble'},body:'<!-- verify/acceptance --> old'}];
  assert.equal(f.run('post.ts',[dir]).status,0);
  const calls=f.calls(); assert.ok(calls.some(x=>x.name==='evidence'));
  assert.ok(calls.some(x=>x.name==='sudo'&&x.args.includes(join(f.config.workerHome,'result','result.json'))));
  assert.ok(calls.some(x=>x.name==='sudo'&&x.args.includes(join(f.config.workerHome,'result','after.png'))));
  assert.ok(calls.some(x=>x.args.includes('PATCH')&&x.args.includes('repos/jcoble/Assembly/issues/comments/91')));
  assert.ok(calls.some(x=>x.name==='git'&&x.args.includes('remove')));
  assert.equal(f.run('post.ts',[dir]).status,0);
  assert.equal(f.calls().length,calls.length,'already posted must be a no-op');
});
test('failed posting saves receipt, retries boundedly, next dispatcher flushes it',t=>{
  const f=fixture(t),dir=f.result(undefined);
  assert.notEqual(f.run('post.ts',[dir],{FAIL_POST:'1'}).status,0);
  assert.ok(existsSync(join(dir,'receipt.json'))); assert.ok(!existsSync(join(dir,'posted.json')));
  assert.equal(f.calls().filter(x=>x.args.includes('state=error')).length,3);
  assert.equal(f.run('dispatch.ts',[],{SSH_ORIGINAL_COMMAND:'12'}).status,0);
  assert.ok(existsSync(join(dir,'posted.json')));
});
test('dirty worktree is preserved',t=>{
  const f=fixture(t),dir=f.result(undefined);
  assert.equal(f.run('post.ts',[dir],{DIRTY:' M file'}).status,0);
  assert.ok(!f.calls().some(x=>x.name==='git'&&x.args.includes('remove')));
});
test('a PASS after changing PR code is REDO and preserves the lane',t=>{
  const f=fixture(t),dir=f.result(pass);writeFileSync(join(f.config.workerHome,'result','tests.log'),'edit tests');
  writeFileSync(join(f.config.workerHome,'result','after.png'),'capture');
  assert.equal(f.run('post.ts',[dir],{DIRTY:' M tracked-source.ts'}).status,0);
  assert.ok(f.calls().some(x=>x.args?.includes('state=failure')));
  assert.ok(!f.calls().some(x=>x.name==='git'&&x.args.includes('remove')));
});
test('installer refuses mutation without explicit account and network approval',t=>{
  const f=fixture(t); const r=f.run('install.ts'); assert.notEqual(r.status,0);
  assert.deepEqual(f.calls(),[]); assert.match(r.stderr,/approval/i);
});
for (const [trailers, model] of [
  ['Committed-by: Codex SOL 6.1','gpt-6-luna'],
  ['Committed-by: Luna 6','claude-sonnet-5-5'],
  ['Committed-by: Luna 6\nCommitted-by: Sonnet 5.5',undefined],
  ['',undefined],
] as const) test('selects a different model for '+JSON.stringify(trailers),async()=>{
  const { selectModel } = await import('./run.ts');
  if(model) assert.equal(selectModel(trailers,[]).model,model);
  else assert.throws(()=>selectModel(trailers,[]));
});
test('Codex receives every writable directory and Claude arguments stay unchanged',async()=>{
  const { selectModel } = await import('./run.ts');
  const directories=['/verify/result','/verify/assembly-dotnet-verify-pr','/verify/.config/assembly-dotnet-verify-pr'];
  const codex=selectModel('Committed-by: Codex SOL 6.1',directories);
  assert.deepEqual(codex.args.flatMap((x,i)=>x==='--add-dir'?[codex.args[i+1]]:[]),directories);
  assert.deepEqual(selectModel('Committed-by: Luna 6',directories),selectModel('Committed-by: Luna 6',[]));
});
test('run creates lane and tool directories as verify before starting the Codex verifier',t=>{
  const f=fixture(t),dir=f.result(undefined);
  const directories=[join(f.config.workerHome,'result'),
    join(f.config.workerHome,'assembly-dotnet-verify-pr'),
    join(f.config.workerHome,'.config','assembly-dotnet-verify-pr'),
    join(f.config.workerHome,'.cargo'),join(f.config.workerHome,'.nuget'),
    join(f.config.workerHome,'.dotnet'),join(f.config.workerHome,'.cache','pnpm'),
    join(f.config.workerHome,'.local','state','pnpm')];
  rmSync(directories[0],{recursive:true});
  const r=f.run('run.ts',[dir,'--locked']); assert.equal(r.status,0,r.stderr);
  const calls=f.calls(),agent=calls.findIndex(x=>x.name==='codex'); assert.ok(agent>=0);
  assert.deepEqual(calls[agent].args.flatMap((x,i)=>x==='--add-dir'?[calls[agent].args[i+1]]:[]),directories);
  for(const directory of directories) {
    const mkdir=calls.findIndex(x=>x.name==='sudo'&&x.args.includes('mkdir')&&x.args.includes('-p')&&x.args.includes(directory));
    assert.ok(mkdir>=0&&mkdir<agent,directory+' created before verifier');
    assert.deepEqual(calls[mkdir].args.slice(0,4),['-n','-u','verify','--']);
  }
  assert.deepEqual(calls.find(x=>x.name==='agent-environment').directories,directories.map(path=>({path,exists:true})));
});
test('run creates a detached exact-SHA lane, isolates credentials and starts Codex without stdin',t=>{
  const f=fixture(t),dir=f.result(undefined);
  const r=f.run('run.ts',[dir,'--locked'],{SECRET_SENTINEL:'do-not-inherit'});
  assert.equal(r.status,0,r.stderr);
  const calls=f.calls();
  assert.ok(calls.some(x=>x.name==='git'&&x.args.includes('--detach')&&x.args.includes(sha)));
  const lane=join(f.config.worktrees,'verify-pr-12-'+sha+'-run-12-test','.lane');
  assert.equal(readFileSync(lane,'utf8'),'LANE_NAME=verify-pr\nLANE_NUMBER=3\n');
  const packages=calls.findIndex(x=>x.name==='pnpm');
  assert.deepEqual(calls[packages].args,['install','--silent','--prefer-offline']);
  assert.ok(packages<calls.findIndex(x=>x.name==='codex'));
  assert.ok(calls.some(x=>x.name==='sudo'&&x.args.includes(lane)));
  const agent=calls.find(x=>x.name==='codex'); assert.ok(agent);
  assert.ok(agent.args.includes('workspace-write'));
  assert.ok(agent.args.includes('gpt-6-luna'));
  const child=calls.find(x=>x.name==='agent-environment');
  assert.equal(child.stdin,''); assert.equal(child.env.SECRET_SENTINEL,undefined);
  assert.equal(child.env.HOME,f.config.workerHome);
  assert.ok(!r.stdout.includes('do-not-inherit'));
  const fetch=calls.find(x=>x.name==='git'&&x.args.includes('fetch'));
  assert.deepEqual(fetch.args.slice(fetch.args.indexOf('fetch')),['fetch','--no-tags','origin','b'.repeat(40),sha]);
});
test('run takes one blocking flock before launching and gives Claude no messaging hooks',t=>{
  const f=fixture(t),dir=f.result(undefined);
  const r=f.run('run.ts',[dir],{TRAILERS:'Committed-by: Luna 6'}); assert.equal(r.status,0,r.stderr);
  const calls=f.calls(); assert.equal(calls[0].name,'flock');
  assert.deepEqual(calls[0].args.slice(0,2),['--exclusive',join(f.config.state,'verifier.lock')]);
  const agent=calls.find(x=>x.name==='claude'); assert.ok(agent);
  assert.ok(agent.args.includes('{"disableAllHooks":true}'));
});
test('low available memory blocks before worktree creation',t=>{
  const f=fixture(t),dir=f.result(undefined);
  const r=f.run('run.ts',[dir,'--locked'],{MEMORY:'1024'});
  assert.notEqual(r.status,0); assert.match(r.stderr,/memory/);
  assert.ok(!f.calls().some(x=>x.name==='git'));
});
for(const [name,env] of [['readable owner home',{AUDIT_READABLE:'1'}],['missing firewall rule',{FAIL_FIREWALL:'1'}]] as const)
  test('run blocks '+name+' before launching PR code',t=>{
    const f=fixture(t),dir=f.result(undefined); const r=f.run('run.ts',[dir,'--locked'],env);
    assert.notEqual(r.status,0); assert.ok(!f.calls().some(x=>x.name==='git'));
  });
test('unit start failure posts no-result immediately',t=>{
  const f=fixture(t); const r=f.run('dispatch.ts',[],{SSH_ORIGINAL_COMMAND:'12',FAIL_START:'1'});
  assert.notEqual(r.status,0); assert.ok(f.calls().some(x=>x.args?.includes('state=error')));
});
test('symlink evidence is rejected instead of reading an owner file',t=>{
  const f=fixture(t),dir=f.result(pass);
  const secret=join(f.root,'owner-private.txt'); writeFileSync(secret,'SECRET_SENTINEL');
  symlinkSync(secret,join(f.config.workerHome,'result','after.png'));
  assert.equal(f.run('post.ts',[dir]).status,0);
  assert.ok(!f.calls().some(x=>x.name==='evidence'));
  assert.ok(f.calls().some(x=>x.args?.includes('state=failure')));
  assert.ok(!readFileSync(join(dir,'receipt.json'),'utf8').includes('SECRET_SENTINEL'));
});
test('hidden marker past the first page updates rather than creating a duplicate',t=>{
  const f=fixture(t),dir=f.result(undefined);
  f.replies['repos/jcoble/Assembly/issues/12/comments?per_page=100&page=1']=Array.from({length:100},()=>({body:'other'}));
  f.replies['repos/jcoble/Assembly/issues/12/comments?per_page=100&page=2']=[{id:92,user:{login:'jcoble'},body:'<!-- verify/acceptance -->'}];
  assert.equal(f.run('post.ts',[dir]).status,0);
  assert.ok(f.calls().some(x=>x.args.includes('repos/jcoble/Assembly/issues/comments/92')));
  assert.ok(!f.calls().some(x=>x.args.includes('POST')&&x.args.includes('repos/jcoble/Assembly/issues/12/comments')));
});
test('authorized-key replacement is idempotent and preserves unrelated keys',async()=>{
  const {authorizedKeys}=await import('./install.ts');
  const other='ssh-ed25519 AAAA other-key';
  const managed='restrict,command="node dispatch.ts" ssh-ed25519 BBBB assembly-verify-pr';
  assert.equal(authorizedKeys(other+'\n',managed),other+'\n'+managed+'\n');
  assert.equal(authorizedKeys(authorizedKeys(other+'\n',managed),managed),other+'\n'+managed+'\n');
  assert.equal(authorizedKeys(other+'\n'+managed+'\n'),other+'\n');
});
test('run refuses three heavy builds before creating a worktree',t=>{
  const f=fixture(t),dir=f.result(undefined);
  const r=f.run('run.ts',[dir,'--locked'],{BUILDS:'cargo\ndotnet\nrustc'});
  assert.notEqual(r.status,0); assert.match(r.stderr,/heavy/i);
  assert.ok(!f.calls().some(x=>x.name==='git'&&x.args.includes('add')));
});

for (const [name,tests,receipt] of [
  ['empty tests',[],undefined],
  ['missing receipt field',[{name:'edit tests',listed:3,ran:3,passed:3}],undefined],
  ['missing receipt file',pass.tests,undefined],
  ['empty receipt',pass.tests,''],
  ['receipt missing named tests',pass.tests,'different suite passed'],
  ['receipt outside report',[{...pass.tests[0],receipt:'../tests.log'}],'edit tests'],
] as const) test('PASS is REDO for '+name,t=>{
  const f=fixture(t),dir=f.result({...pass,tests});
  writeFileSync(join(f.config.workerHome,'result','after.png'),'capture');
  if(receipt!==undefined) writeFileSync(join(f.config.workerHome,'result','tests.log'),receipt);
  assert.equal(f.run('post.ts',[dir]).status,0);
  const saved=JSON.parse(readFileSync(join(dir,'receipt.json'),'utf8'));
  assert.equal(saved.label,'REDO'); assert.equal(saved.state,'failure');
});
test('valid test receipt is uploaded and labeled as the verifier receipt',t=>{
  const f=fixture(t),dir=f.result(pass);
  writeFileSync(join(f.config.workerHome,'result','after.png'),'capture');
  writeFileSync(join(f.config.workerHome,'result','tests.log'),'edit tests: 3 passed');
  assert.equal(f.run('post.ts',[dir]).status,0);
  const saved=JSON.parse(readFileSync(join(dir,'receipt.json'),'utf8'));
  assert.equal(saved.state,'success'); assert.match(saved.body,/Verifier.*receipts/i);
  assert.match(saved.body,/\[tests.log\]\(https:\/\/drive.google.com/);
});
test('NOT AFFECTED without tests cannot excuse a code diff',t=>{
  const f=fixture(t),dir=f.result({sha,summary:'Docs',behaviors:[{name:'docs',state:'NOT AFFECTED',details:'prose',proof:[]}],tests:[],proof:[]});
  assert.equal(f.run('post.ts',[dir]).status,0);
  assert.equal(JSON.parse(readFileSync(join(dir,'receipt.json'),'utf8')).label,'REDO');
});
test('dispatch leaves an unrecorded look-alike service alone',t=>{
  const f=fixture(t); assert.equal(f.run('dispatch.ts',[],{SSH_ORIGINAL_COMMAND:'12'}).status,0);
  assert.ok(!f.calls().some(x=>x.name==='systemctl'&&x.args.includes('stop')));
  assert.deepEqual(JSON.parse(readFileSync(join(f.config.state,'units.json'),'utf8')),['verify-pr-12.service']);
});

test('dispatch refuses to adopt an existing unrecorded unit',t=>{
  const f=fixture(t),r=f.run('dispatch.ts',[],{SSH_ORIGINAL_COMMAND:'12',SERVICE_LOAD:'loaded'});
  assert.notEqual(r.status,0);
  assert.ok(!f.calls().some(x=>x.name==='systemctl'&&x.args.includes('stop')));
  assert.ok(!existsSync(join(f.config.state,'units.json')));
});
for(const [file,state,details,label] of [
  ['CMakeLists.txt','NOT AFFECTED','only prose','REDO'],
  ['src/code.ts','BLOCKED','needs Mac','needs Mac'],
  ['src/code.ts','BLOCKED','no phone connected','BLOCKED'],
] as const) test('receipt requirement preserves '+label+' for '+file,t=>{
  const f=fixture(t),dir=f.result({sha,summary:'Review',behaviors:[{name:'change',state,details,proof:[]}],tests:[],proof:[]});
  assert.equal(f.run('post.ts',[dir],{DIFF_FILES:file}).status,0);
  assert.equal(JSON.parse(readFileSync(join(dir,'receipt.json'),'utf8')).label,label);
});

test('generated evidence does not invalidate a PASS',t=>{
  const f=fixture(t),dir=f.result(pass);
  writeFileSync(join(f.config.workerHome,'result','tests.log'),'edit tests');
  writeFileSync(join(f.config.workerHome,'result','after.png'),'capture');
  const r=f.run('post.ts',[dir],{DIRTY:'?? evidence/'+sha+'/four-way/summary.json\n?? evidence/'+sha+'/four-way/summary.md'});
  assert.equal(r.status,0,r.stderr);
  assert.equal(JSON.parse(readFileSync(join(dir,'receipt.json'),'utf8')).state,'success');
});
test('evidence exclusion still detects other mutations',t=>{
  const f=fixture(t),dir=f.result(pass);
  writeFileSync(join(f.config.workerHome,'result','tests.log'),'edit tests');
  writeFileSync(join(f.config.workerHome,'result','after.png'),'capture');
  const r=f.run('post.ts',[dir],{DIRTY:'?? evidence/summary.json\n M source.ts'});
  assert.equal(r.status,0,r.stderr);
  assert.equal(JSON.parse(readFileSync(join(dir,'receipt.json'),'utf8')).label,'REDO');
});

test('a second attempt at the same SHA prepares a new lane while preserving dirty evidence',t=>{
  const f=fixture(t), attempts: {directory:string;ctx:any}[]=[];
  for(let attempt=0;attempt<2;attempt++) {
    const dispatched=f.run('dispatch.ts',[],{SSH_ORIGINAL_COMMAND:'12'});
    assert.equal(dispatched.status,0,dispatched.stderr);
    const entry=readdirSync(f.config.state).find(x=>x.startsWith('run-')&&!attempts.some(a=>a.directory===join(f.config.state,x)))!;
    const directory=join(f.config.state,entry),ctx=JSON.parse(readFileSync(join(directory,'context.json'),'utf8'));
    const ran=f.run('run.ts',[directory,'--locked']);
    assert.equal(ran.status,0,ran.stderr);
    assert.equal(readFileSync(join(ctx.worktree,'.lane'),'utf8'),'LANE_NAME=verify-pr\nLANE_NUMBER=3\n');
    attempts.push({directory,ctx});
    const posted=f.run('post.ts',[directory],attempt===0?{DIRTY:'?? evidence/summary.md'}:{});
    assert.equal(posted.status,0,posted.stderr);
    if(attempt===0) assert.ok(existsSync(ctx.worktree));
  }
  assert.notEqual(attempts[0].ctx.worktree,attempts[1].ctx.worktree);
  assert.ok(existsSync(attempts[0].ctx.worktree),'dirty first lane remains');
  assert.ok(!existsSync(attempts[1].ctx.worktree),'clean second lane is removed');
  assert.equal(f.calls().filter(x=>x.name==='codex').length,2);
});

test('README-only head stays NOT AFFECTED when the base independently adds code',t=>{
  const repo=mkdtempSync(join(tmpdir(),'verify-pr-history-'));
  t.after(()=>rmSync(repo,{recursive:true,force:true}));
  const realGit=spawnSync('which',['git'],{encoding:'utf8'}).stdout.trim();
  function git(...args:string[]) {
    const r=spawnSync(realGit,['-C',repo,...args],{encoding:'utf8',env:{...process.env,
      GIT_AUTHOR_NAME:'Test',GIT_AUTHOR_EMAIL:'test@example.com',GIT_COMMITTER_NAME:'Test',GIT_COMMITTER_EMAIL:'test@example.com'}});
    assert.equal(r.status,0,r.stderr); return r.stdout.trim();
  }
  git('init'); writeFileSync(join(repo,'README.md'),'initial'); git('add','.'); git('commit','-m','initial');
  const ancestor=git('rev-parse','HEAD');
  writeFileSync(join(repo,'README.md'),'documentation update'); git('commit','-am','docs');
  const head=git('rev-parse','HEAD');
  git('checkout','--detach',ancestor); writeFileSync(join(repo,'code.ts'),'export const value=1;');
  git('add','.'); git('commit','-m','base code'); const base=git('rev-parse','HEAD');
  assert.match(git('diff','--name-only',base,head),/code.ts/);
  assert.equal(git('diff','--name-only',`${base}...${head}`),'README.md');
  const f=fixture(t,head,base),directory=f.result({sha:head,summary:'Docs',
    behaviors:[{name:'docs',state:'NOT AFFECTED',details:'README only',proof:[]}],tests:[],proof:[]});
  const r=f.run('post.ts',[directory],{DIFF_REPO:repo,REAL_GIT:realGit});
  assert.equal(r.status,0,r.stderr);
  const receipt=JSON.parse(readFileSync(join(directory,'receipt.json'),'utf8'));
  assert.equal(receipt.label,'NOT AFFECTED'); assert.equal(receipt.state,'success');
});
