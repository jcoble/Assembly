import {test} from 'node:test';
import assert from 'node:assert/strict';
import {promises as fs} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {backendIdentity} from './dev-remote-identity.ts';
import {installedRelease,pruneReleases} from './dev-remote-build.ts';
const exec=promisify(execFile);

test('a completed backend is hash-verified and reused without another build; corruption refuses reuse',async t=>{
  const root=await fs.mkdtemp(join(tmpdir(),'assembly-prepared-'));
  t.after(()=>fs.rm(root,{recursive:true,force:true}));
  const source=join(root,'source'),lane=join(root,'lane'),bin=join(root,'bin'),calls=join(root,'calls');
  for(const directory of [source,lane,bin])await fs.mkdir(directory);
  const files={
    'dotnet/Assembly.Remote/Assembly.Remote.csproj':'<Project/>',
    'dotnet/Assembly.Infrastructure/Socket/SocketConnection.cs':'const int Protocol = 1;',
    'dotnet/Assembly.Application/Features/Chat/Rows/RowStore.cs':'const int FormatVersion = 5;',
  };
  for(const [name,content] of Object.entries(files)){await fs.mkdir(join(source,name,'..'),{recursive:true});await fs.writeFile(join(source,name),content)}
  await exec('git',['init','-q'],{cwd:source});await exec('git',['add','.'],{cwd:source});
  await exec('git',['-c','user.name=Fixture','-c','user.email=fixture@example.invalid','commit','-qm','backend fixture'],{cwd:source});
  const commit=(await exec('git',['rev-parse','HEAD'],{cwd:source})).stdout.trim();
  const identity=await backendIdentity(source,commit);
  await fs.writeFile(join(bin,'dotnet'),`#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
const out=process.argv[process.argv.indexOf('-o')+1];
if(process.env.DIRTY_SOURCE)fs.appendFileSync('dotnet/Assembly.Remote/Assembly.Remote.csproj','unexpected source edit');
fs.appendFileSync(process.env.BUILD_CALLS,'build\\n');fs.mkdirSync(out,{recursive:true});
for(const [file,content] of Object.entries({'Assembly.Remote.dll':'fake committed backend','Assembly.Remote.deps.json':'{}','Assembly.Remote.runtimeconfig.json':JSON.stringify({runtimeOptions:{frameworks:[{name:'Microsoft.NETCore.App',version:'10.0.0'}]}})}))fs.writeFileSync(path.join(out,file),content);
`,{mode:0o755});
  const helper=join(root,'run.ts');
  await fs.writeFile(helper,`import {prepareBackend} from ${JSON.stringify(new URL('./dev-remote-build.ts',import.meta.url).href)};\nconsole.log('PREPARED '+JSON.stringify(await prepareBackend(${JSON.stringify(lane)},${JSON.stringify({identity,commit})},undefined,new AbortController().signal)));`);
  const env={...process.env,PATH:bin+':'+process.env.PATH,BUILD_CALLS:calls};
  const first=await exec(process.execPath,[helper],{cwd:source,env});
  const prepared=JSON.parse(first.stdout.split('\n').find(line=>line.startsWith('PREPARED '))!.slice(9));
  assert.equal(await fs.readFile(calls,'utf8'),'build\n');
  const second=await exec(process.execPath,[helper],{cwd:source,env});
  assert.match(second.stdout,/"cacheHit":true/);
  assert.equal(await fs.readFile(calls,'utf8'),'build\n');
  assert.deepEqual(JSON.parse(second.stdout.split('\n').find(line=>line.startsWith('PREPARED '))!.slice(9)),prepared);
  const dll=join(prepared.directory,'backend/Assembly.Remote.dll');
  const original=await fs.readFile(dll);await fs.writeFile(dll,Buffer.alloc(original.length,120));
  await assert.rejects(exec(process.execPath,[helper],{cwd:source,env}),error=>/prepared backend cache file changed/.test(String(error)));
  assert.equal(await fs.readFile(calls,'utf8'),'build\n');
  await fs.writeFile(dll,original);
  await fs.writeFile(join(prepared.directory,'unlisted.dll'),'unlisted');
  await assert.rejects(exec(process.execPath,[helper],{cwd:source,env}),error=>/cache closure changed/.test(String(error)));
  const secondLane=join(root,'second-lane');await fs.mkdir(secondLane);
  const dirtyHelper=join(root,'dirty.ts');
  await fs.writeFile(dirtyHelper,(await fs.readFile(helper,'utf8')).replace(JSON.stringify(lane),JSON.stringify(secondLane)));
  await assert.rejects(exec(process.execPath,[dirtyHelper],{cwd:source,env:{...env,DIRTY_SOURCE:'1'}}),error=>/build modified tracked backend source/.test(String(error)));
  await assert.rejects(fs.stat(join(secondLane,'prepared-backend.json')),{code:'ENOENT'});
});

test('a verified deploy keeps the running and replaced releases and leaves anything else in bin alone',async t=>{
  const root=await fs.mkdtemp(join(tmpdir(),'assembly-releases-'));
  t.after(()=>fs.rm(root,{recursive:true,force:true}));
  const release=(digit:string)=>digit.repeat(40)+'-'+digit.repeat(64);
  const names=[release('a'),release('b'),release('c'),'e94965a5','current'];
  for(const name of names)await fs.mkdir(join(root,'bin',name),{recursive:true});
  await pruneReleases(root,[join(root,'bin',release('b')),join(root,'bin',release('c'))]);
  assert.deepEqual((await fs.readdir(join(root,'bin'))).sort(),[release('b'),release('c'),'current','e94965a5'].sort());
});

test('a host from before the release layout has nothing to reuse, and a damaged manifest still refuses',async t=>{
  const root=await fs.mkdtemp(join(tmpdir(),'assembly-releases-'));
  t.after(()=>fs.rm(root,{recursive:true,force:true}));
  const current=join(root,'bin','release','backend');
  await fs.mkdir(current,{recursive:true});
  const old=join(root,'bin','current','Assembly.Remote.dll');
  assert.equal(await installedRelease(old),undefined);
  const dll=join(current,'Assembly.Remote.dll');
  await fs.writeFile(join(root,'bin','release','manifest.json'),JSON.stringify({files:[]}));
  assert.deepEqual((await installedRelease(dll))?.manifest,{files:[]});
  await fs.writeFile(join(root,'bin','release','manifest.json'),'not json');
  await assert.rejects(installedRelease(dll));
});
