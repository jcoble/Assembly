import {test} from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync, readFileSync, writeFileSync, rmSync, existsSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {spawnSync} from 'node:child_process';

const workflow=readFileSync(new URL('../../.github/workflows/verify.yml',import.meta.url),'utf8');
const shell=workflow.split('        run: |\n')[1].split('\n').map(line=>line.slice(10)).join('\n');
function fixture(t:any) {
  const root=mkdtempSync(join(tmpdir(),'verify-workflow-')); t.after(()=>rmSync(root,{recursive:true,force:true}));
  const event=join(root,'event.json'),calls=join(root,'ssh.json');
  writeFileSync(join(root,'ssh'),`#!${process.execPath}\nrequire('node:fs').writeFileSync(process.env.CALLS,JSON.stringify(process.argv.slice(2)));\n`,{mode:0o755});
  function run(payload:any, overrides:Record<string,string>={}) {
    writeFileSync(event,JSON.stringify(payload));
    return spawnSync('bash',['-c',shell],{encoding:'utf8',env:{...process.env,
      PATH:root+':'+process.env.PATH,CALLS:calls,GITHUB_EVENT_PATH:event,GITHUB_EVENT_NAME:'pull_request_target',
      VERIFY_PR_SSH_KEY:'SECRET_SENTINEL',WORKBOX_HOST:'pinned.example',WORKBOX_SSH_USER:'service',
      WORKBOX_PINNED_HOST_KEY:'pinned.example ssh-ed25519 AAAA',...overrides}});
  }
  return {run,calls};
}
test('workflow dispatches only the numeric PR argument, pins SSH and keeps secrets out of output',t=>{
  const f=fixture(t),r=f.run({pull_request:{number:12}}); assert.equal(r.status,0,r.stderr);
  const args=JSON.parse(readFileSync(f.calls,'utf8'));
  assert.equal(args.at(-1),'12'); assert.ok(args.includes('StrictHostKeyChecking=yes'));
  assert.ok(args.some((x:string)=>x.startsWith('UserKnownHostsFile=')));
  assert.ok(!JSON.stringify(args).includes('SECRET_SENTINEL')); assert.ok(!r.stdout.includes('SECRET_SENTINEL'));
});
test('missing secret skips even before reading a malformed event',t=>{
  const f=fixture(t),r=f.run({}, {VERIFY_PR_SSH_KEY:''}); assert.equal(r.status,0,r.stderr);
  assert.ok(!existsSync(f.calls));
});
test('unset host-key pin fails before SSH',t=>{
  const f=fixture(t),r=f.run({pull_request:{number:12}}, {WORKBOX_PINNED_HOST_KEY:'FILL_AT_INSTALL_WORKBOX_KNOWN_HOSTS_LINE'});
  assert.notEqual(r.status,0); assert.ok(!existsSync(f.calls));
});
for(const author of ['someone-else','jcoble; $(touch /tmp/never-verify)'])
  test('unauthorized comment skips safely for '+JSON.stringify(author),t=>{
    const f=fixture(t),r=f.run({issue:{number:12},comment:{id:99,user:{login:author},body:'@verify'}}, {GITHUB_EVENT_NAME:'issue_comment'});
    assert.equal(r.status,0,r.stderr); assert.ok(!existsSync(f.calls));
  });
test('owner comment path passes PR and comment digits, not the comment body',t=>{
  const f=fixture(t),r=f.run({issue:{number:12},comment:{id:99,user:{login:'jcoble'},body:'@verify $(echo unsafe)'}},
    {GITHUB_EVENT_NAME:'issue_comment'});
  assert.equal(r.status,0,r.stderr); assert.equal(JSON.parse(readFileSync(f.calls,'utf8')).at(-1),'12 99');
});
test('untrusted PR number cannot become a shell command',t=>{
  const f=fixture(t),r=f.run({pull_request:{number:'12; echo unsafe'}});
  assert.notEqual(r.status,0); assert.ok(!existsSync(f.calls));
});
