import {test} from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,mkdirSync,writeFileSync,readFileSync,existsSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {spawnSync} from 'node:child_process';

function fixture(t:any) {
  const root=mkdtempSync(join(tmpdir(),'verify-install-'));t.after(()=>rmSync(root,{recursive:true,force:true}));
  const bin=join(root,'bin');mkdirSync(bin);mkdirSync(join(root,'.ssh'));
  writeFileSync(join(root,'.ssh/authorized_keys'),'ssh-ed25519 OTHER unrelated\n');
  const state=join(root,'machine.json'),log=join(root,'calls.jsonl');
  writeFileSync(state,JSON.stringify({rules:[],secret:false}));
  const preload=join(root,'linux.cjs');
  writeFileSync(preload,`Object.defineProperty(process,'platform',{value:'linux'});
const fs=require('node:fs'),original=fs.writeFileSync;
fs.writeFileSync=function(p,...args){if(process.env.FAIL==='copy'&&String(p).endsWith('/dispatch.ts.verify-pr.tmp'))throw Error('copy failure');return original.call(this,p,...args)};
const rename=fs.renameSync;fs.renameSync=function(a,b){if(process.env.FAIL==='authorized'&&String(b).endsWith('/authorized_keys'))throw Error('rename failure');if(process.env.FAIL==='config'&&String(b).endsWith('/config.json'))throw Error('config failure');return rename.call(this,a,b)};
require('node:module').syncBuiltinESMExports();`);
  const fake=`#!${process.execPath}
const fs=require('node:fs'),path=require('node:path');const name=path.basename(process.argv[1]),a=process.argv.slice(2);
const s=JSON.parse(fs.readFileSync(process.env.MACHINE));fs.appendFileSync(process.env.CALLS,JSON.stringify({name,args:a})+'\\n');
function save(){fs.writeFileSync(process.env.MACHINE,JSON.stringify(s))}
function fail(step){if(process.env.FAIL===step)process.exit(1)}
if(name==='which'){console.log(path.join(process.env.BIN,a[0]));process.exit(0)}
if(name==='git'){if(a.includes('--show-current'))console.log('main');if(a.includes('get-url'))console.log('https://github.com/jcoble/Assembly.git');if(a.includes('rev-parse'))console.log('aaa');}
if(name==='id'){if(a.length===1)process.exit(s.account?0:1);console.log(a.includes('-nG')?'verify':'1002');}
if(name==='getent')console.log('verify:x:1002:1002::/home/verify:/bin/bash');
if(name==='loginctl'){fail('linger');console.log('yes')}
if(name==='ssh-keygen'){fail('key');const p=a[a.indexOf('-f')+1];fs.writeFileSync(p,'PRIVATE');fs.writeFileSync(p+'.pub','ssh-ed25519 NEW assembly-verify-pr');}
if(name==='gh'){if(a.includes('list'))console.log(JSON.stringify(s.secret?[{name:'VERIFY_PR_SSH_KEY'}]:[]));if(a.includes('set')){fail('secret-before');s.secret=true;save();fail('secret')}if(a.includes('delete')){if(!s.secret)process.exit(1);s.secret=false;save()}}
if(name==='sudo'){
 if(a.includes('useradd')){fail('account');s.account=true;save()}
 if(a.includes('verify-read-audit')){fail('audit');console.log('DENIED')}
 if(a.includes('iptables')||a.includes('ip6tables')){const tool=a[1],r=tool+':'+a.slice(3).join(' ');if(a.includes('-C'))process.exit(s.rules.includes(r)?0:1);
 if(a.includes('-I')){fail(tool);s.rules.push(r);save()}if(a.includes('-D')){s.rules=s.rules.filter(x=>x!==r);save()}}
 if(a.includes('env')){const i=a.indexOf('env'),rest=a.slice(i+2);while(rest[0]?.includes('='))rest.shift();const cmd=rest.shift();
 if(cmd==='which')fail('worker');if(cmd==='mkdir')fail('mkdir');if(cmd==='git'&&rest.includes('rev-parse'))process.exit(1);if(cmd==='git'&&rest.includes('clone'))fail('clone');}
}
if(name==='systemctl'&&a.includes('list-units'))console.log('verify-pr-999.service loaded active running unrelated');
`;
  for(const name of ['which','git','id','getent','loginctl','ssh-keygen','gh','sudo','systemctl'])writeFileSync(join(bin,name),fake,{mode:0o755});
  const destination=join(root,'.local/share/verify-pr');
  function run(uninstall=false,fail='') {return spawnSync(process.execPath,['--require',preload,join(import.meta.dirname,'install.ts'),...(uninstall?['--uninstall','--approve-uninstall']:['--approve-account','--live-ports=5000'])],{encoding:'utf8',env:{...process.env,HOME:root,PATH:bin+':'+process.env.PATH,BIN:bin,MACHINE:state,CALLS:log,FAIL:fail,VERIFY_CONFIG:join(destination,'config.json')}})}
  return {root,state,log,destination,run};
}
for(const step of ['account','audit','iptables','ip6tables','worker','mkdir','clone','copy','config','key','authorized','secret-before','secret','linger',''])
 test('uninstall recovers installation after '+(step||'success'),t=>{
  const f=fixture(t),r=f.run(false,step);assert.equal(r.status,step?1:0,r.stderr);
  assert.ok(existsSync(join(f.destination,'install-state.json')),'journal exists before mutation');
  const stopped='verify-pr-12.service';const runtime=join(f.root,'.local/state/verify-pr');mkdirSync(runtime,{recursive:true});
  writeFileSync(join(runtime,'units.json'),JSON.stringify([stopped]));
  const u=f.run(true);assert.equal(u.status,0,u.stderr);
  const machine=JSON.parse(readFileSync(f.state,'utf8'));assert.deepEqual(machine.rules,[]);assert.equal(machine.secret,false);
  assert.equal(readFileSync(join(f.root,'.ssh/authorized_keys'),'utf8'),'ssh-ed25519 OTHER unrelated\n');
  assert.ok(!existsSync(f.destination));
  const calls=readFileSync(f.log,'utf8').trim().split('\n').map(x=>JSON.parse(x));
  assert.deepEqual(calls.filter(x=>x.name==='systemctl'&&x.args.includes('stop')).map(x=>x.args.at(-1)),[stopped]);
  assert.equal(f.run(true).status,0);
 });

test('uninstall preserves a pre-existing firewall rule',t=>{
  const f=fixture(t),rule='iptables:OUTPUT -p tcp --dport 5000 -m owner --uid-owner 1002 -m comment --comment assembly-verify-pr -j REJECT';
  writeFileSync(f.state,JSON.stringify({rules:[rule],secret:false}));
  const r=f.run(); assert.equal(r.status,0,r.stderr);
  assert.equal(f.run(true).status,0);
  assert.deepEqual(JSON.parse(readFileSync(f.state,'utf8')).rules,[rule]);
});
