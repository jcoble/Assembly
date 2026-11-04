import { existsSync, readFileSync, writeFileSync, renameSync, rmSync, mkdtempSync, lstatSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { homedir } from 'node:os';
import { atomic, command, worker, privateDirectory, repository, owner, main, recordedUnits, stopUnit } from './common.ts';
import type { Config } from './common.ts';
import { auditAccount, auditFirewall, firewallRules, keyMarker } from './isolation.ts';

const files=['common.ts','dispatch.ts','run.ts','post.ts','result.ts','isolation.ts','prompt.md','install.ts'];
function replace(path:string, text:string, mode=0o600) {
  const temporary=path+'.verify-pr.tmp';
  try { writeFileSync(temporary,text,{mode,flag:'wx'}); renameSync(temporary,path); }
  finally { rmSync(temporary,{force:true}); }
}
export function authorizedKeys(previous:string,line?:string) {
  const kept=previous.split('\n').filter(x=>!x.endsWith(' '+keyMarker)).filter(Boolean);
  return [...kept,...(line?[line]:[])].join('\n')+'\n';
}
function authorized(home:string, line?:string) {
  const directory=join(home,'.ssh'), path=join(directory,'authorized_keys');
  if (existsSync(directory) && lstatSync(directory).isSymbolicLink()) throw Error('Refusing symlink SSH directory');
  privateDirectory(directory);
  if (existsSync(path) && !lstatSync(path).isFile()) throw Error('Refusing non-regular authorized_keys');
  const previous=existsSync(path)?readFileSync(path,'utf8'):'';
  replace(path,authorizedKeys(previous,line));
}
type InstallState = { config:Config; firewall:{tool:string;rule:string[]}[]; authorized:boolean; secret:boolean; completed:string[]; laneReservation?:number };
function firewall(state:InstallState, save:()=>void) {
  for(const tool of ['iptables','ip6tables']) for(const rule of firewallRules(state.config)) {
    let present=true;
    try { command('sudo',['-n',tool,'-C',...rule]); } catch { present=false; }
    if(present) continue;
    // Record intent before the command: even a command failing after mutation is recoverable.
    if(!state.firewall.some(x=>x.tool===tool && JSON.stringify(x.rule)===JSON.stringify(rule))) {
      state.firewall.push({tool,rule}); save();
    }
    command('sudo',['-n',tool,'-I',...rule]);
    save();
  }
}
export function install(args:string[]) {
  const home=homedir(), destination=join(home,'.local/share/verify-pr');
  if(args.includes('--uninstall')) {
    if(!args.includes('--approve-uninstall')) throw Error('Uninstall requires explicit --approve-uninstall approval');
    const journal=join(destination,'install-state.json');
    if(!existsSync(journal)) {
      if(existsSync(destination)) throw Error('Missing install state; refusing to guess which changes belong to this installation');
      console.log('Already uninstalled'); return;
    }
    const state:InstallState=JSON.parse(readFileSync(journal,'utf8'));
    const save=()=>atomic(journal,state);
    // Undo only resources recorded by this installation.
    if(state.secret) {
      const secrets=JSON.parse(command('gh',['secret','list','--repo',repository,'--json','name']));
      if(!Array.isArray(secrets)) throw Error('Invalid repository secret list');
      if(secrets.some(s=>s.name==='VERIFY_PR_SSH_KEY')) command('gh',['secret','delete','VERIFY_PR_SSH_KEY','--repo',repository]);
      state.secret=false; save();
    }
    if(state.authorized) { authorized(home); state.authorized=false; save(); }
    for(const unit of recordedUnits(state.config.state)) stopUnit(unit);
    rmSync(join(state.config.state,'units.json'),{force:true});
    while(state.firewall.length) {
      const {tool,rule}=state.firewall.at(-1)!;
      let present=true;
      try { command('sudo',['-n',tool,'-C',...rule]); } catch { present=false; }
      if(present) command('sudo',['-n',tool,'-D',...rule]);
      state.firewall.pop(); save();
    }
    if(state.laneReservation) {
      command('ssh',['workbox-test',`if [ "$(cat ~/assembly-dotnet-verify-pr/lane-number 2>/dev/null)" = ${state.laneReservation} ]; then rm -f ~/assembly-dotnet-verify-pr/lane-number; fi`]);
      delete state.laneReservation; save();
    }
    rmSync(destination,{recursive:true,force:true});
    console.log('Uninstalled trigger, firewall rules and owned lane reservation; verifier account, clone and receipts preserved'); return;
  }
  const ports=args.find(x=>x.startsWith('--live-ports='))?.slice('--live-ports='.length).split(',').map(Number);
  if(!args.includes('--approve-account') || !ports?.length ||
      !ports.every(p=>Number.isInteger(p)&&p>0&&p<=65535))
    throw Error('Installation requires explicit --approve-account approval and reviewed --live-ports=PORT,PORT');
  if(process.platform!=='linux') throw Error('Install only on Workbox Linux after merge');
  const checkout=resolve(import.meta.dirname,'../..');
  if(command('git',['-C',checkout,'branch','--show-current']).trim()!=='main' ||
      command('git',['-C',checkout,'status','--porcelain']).trim()) throw Error('Install from a clean main checkout');
  if(command('git',['-C',checkout,'remote','get-url','origin']).trim()!=='https://github.com/jcoble/Assembly.git')
    throw Error('Unexpected main repository origin');
  command('git',['-C',checkout,'fetch','origin','main']);
  if(command('git',['-C',checkout,'rev-parse','HEAD']).trim()!==command('git',['-C',checkout,'rev-parse','origin/main']).trim())
    throw Error('Main checkout is not current origin/main');
  if(![home,destination,process.execPath].every(x=>/^[A-Za-z0-9_./-]+$/.test(x))) throw Error('Unsupported install path');
  // Fail before key generation if required programs are absent. Never install packages here.
  for(const executable of ['node','gh','git','sudo','systemd-run','systemctl','flock','free','ps',
    'ssh','pnpm','ssh-keygen','iptables','ip6tables','Xvfb','xdotool','ffmpeg','evidence','codex','claude'])
    command('which',[executable]);
  const journal=join(destination,'install-state.json');
  const previous:InstallState|undefined=existsSync(journal)?JSON.parse(readFileSync(journal,'utf8')):undefined;
  const c:Config={repository,owner,state:join(home,'.local/state/verify-pr'),clone:'/home/verify/dev/work/Assembly',
    ownerHome:home,workerPath:'/home/verify/.local/bin:/usr/local/bin:/usr/bin:/bin',
    evidence:command('which',['evidence']).trim(),
    workerHome:'/home/verify',worker:'verify',worktrees:'/home/verify/dev/work/worktrees/Assembly',
    laneNumber:previous?.config.laneNumber || 0,runtimeSeconds:7200,minimumMemoryMB:2048,livePorts:ports};
  privateDirectory(destination);
  const state:InstallState=previous ||
    {config:c,firewall:[],authorized:false,secret:false,completed:[]};
  if(JSON.stringify(state.config)!==JSON.stringify(c)) throw Error('Uninstall existing configuration before changing install settings');
  const save=()=>atomic(journal,state);
  const completed=(step:string)=>{ if(!state.completed.includes(step)) state.completed.push(step); save(); };
  save(); // Durable before the first account, firewall, file or credential change.
  const reserved=command('ssh',['workbox-test','cat ~/assembly-dotnet-verify-pr/lane-number 2>/dev/null || true']).trim();
  if(reserved && !/^[1-9]$/.test(reserved)) throw Error('Invalid verify-pr lane reservation');
  if(reserved) {
    if(c.laneNumber && c.laneNumber!==Number(reserved)) throw Error('Verifier lane reservation changed');
    c.laneNumber=Number(reserved);
  } else {
    const used=command('ssh',['workbox-test','cat ~/assembly-dotnet-*/lane-number 2>/dev/null || true']).trim().split(/\s+/);
    const number=Array.from({length:9},(_,i)=>i+1).find(n=>!used.includes(String(n)));
    if(!number) throw Error('all 9 lane numbers are taken on Workbox Test; remove a lane first');
    if(c.laneNumber && c.laneNumber!==number) throw Error('Verifier lane reservation disappeared; uninstall before changing lanes');
    c.laneNumber=number; state.config=c; state.laneReservation=number; save();
    command('ssh',['workbox-test',`mkdir -p ~/assembly-dotnet-verify-pr && echo ${number} > ~/assembly-dotnet-verify-pr/lane-number.tmp && mv ~/assembly-dotnet-verify-pr/lane-number.tmp ~/assembly-dotnet-verify-pr/lane-number`]);
  }
  state.config=c; completed('lane');
  try { command('id',['verify']); }
  catch { command('sudo',['-n','useradd','--create-home','--home-dir','/home/verify','--shell','/bin/bash','verify']); }
  const actualHome=command('getent',['passwd','verify']).trim().split(':')[5];
  if(actualHome!==c.workerHome) throw Error('Existing verify user has unexpected home');
  completed('account');
  auditAccount(c); completed('account audit');
  firewall(state,save); auditFirewall(c); completed('firewall');
  for(const executable of ['node','git','pnpm','codex','claude','Xvfb','xdotool','ffmpeg']) worker(c,'which',[executable]);
  completed('worker tools');
  worker(c,'mkdir',['-p',join(c.workerHome,'dev/work'),c.worktrees,join(c.workerHome,'results')]);
  completed('worker directories');
  let cloned=true;
  try { worker(c,'git',['-C',c.clone,'rev-parse','--git-dir']); } catch { cloned=false; }
  if(!cloned) worker(c,'git',['-c','credential.helper=','clone','https://github.com/jcoble/Assembly.git',c.clone]);
  completed('clone');
  privateDirectory(destination); privateDirectory(c.state);
  for(const file of files) {
    replace(join(destination,file),readFileSync(join(import.meta.dirname,file),'utf8')); completed('file '+file);
  }
  atomic(join(destination,'config.json'),c); completed('config');
  const temporary=mkdtempSync(join(c.state,'key-')),key=join(temporary,'id_ed25519');
  try {
    command('ssh-keygen',['-q','-t','ed25519','-N','','-C',keyMarker,'-f',key]);
    completed('key');
    const publicKey=readFileSync(key+'.pub','utf8').trim();
    state.authorized=true; save();
    authorized(home,`restrict,command="${process.execPath} ${join(destination,'dispatch.ts')}" ${publicKey}`);
    completed('authorized key');
    // The private key goes through stdin only and is deleted even if gh fails.
    state.secret=true; save();
    command('gh',['secret','set','VERIFY_PR_SSH_KEY','--repo',repository],{input:readFileSync(key,'utf8')});
    completed('secret');
    if(command('loginctl',['show-user',process.env.USER || '', '-p','Linger','--value']).trim()!=='yes')
      throw Error('Linger is disabled; parent must enable it before rollout');
    completed('linger');
    console.log('Installed. Parent must pin WORKBOX_PINNED_HOST_KEY, confirm linger=yes, sign in verify providers, audit sudo/network and run live acceptance.');
  } finally { rmSync(temporary,{recursive:true,force:true}); }
}
main(import.meta.url,()=>install(process.argv.slice(2)));
