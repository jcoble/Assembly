import { toolsCurrent } from './dev-toolchain.ts';
// This source runs on Workbox Test, including before the checkout has been transferred. It never emits a token.
export const probeSource = String.raw`
import fs from 'node:fs';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import path from 'node:path';
process.stdin.resume();
process.stdin.once('end', () => process.exit(1));
const lane = JSON.parse(process.argv[1]);
const root = path.join(process.env.HOME, lane.data);
if (fs.realpathSync(root) !== root) throw Error('lane root must not be a symlink');
if (lane.number !== 0 && fs.readFileSync(root + '/lane-number', 'utf8').trim() !== String(lane.number)) throw Error('lane reservation differs');
const unitFile = path.join(process.env.HOME, '.config/systemd/user', lane.unit + '.service');
function adapters() {
const agents = JSON.parse(fs.readFileSync(root + '/serve/agents.json', 'utf8'));
let adapterOK = true, launcherSha = '';
const contracts = [
 ['claude','claude-agent-acp','claude-launcher.ts'],
 ['codex','codex-acp','node_modules/@agentclientprotocol/codex-acp/dist/index.js']
];
for (const [id, name, relative] of contracts) {
 const entry = agents.find(a=>a.id===id)?.args?.[0];
 if (typeof entry !== 'string' || !entry.startsWith(root + '/adapters/bundles/')) throw Error('adapter is outside the lane bundle');
 if (!fs.realpathSync(entry).startsWith(root + '/adapters/bundles/')) throw Error('adapter resolves outside lane');
 if (!entry.endsWith('/' + relative)) throw Error('adapter entrypoint differs from contract');
 const bundle = entry.slice(0, -(relative.length + 1));
 const manifest = JSON.parse(fs.readFileSync(bundle + '/provider-manifest.json','utf8'));
 const installed = JSON.parse(fs.readFileSync(bundle + '/node_modules/@agentclientprotocol/' + name + '/package.json','utf8'));
 adapterOK &&= manifest.smoke?.[name]?.protocolVersion === 2 && manifest.smoke?.[name]?.canSteer === true
   && installed.version === manifest.versions?.[name] && manifest.smoke?.[name]?.info?.version === installed.version;

}
launcherSha = createHash('sha256').update(fs.readFileSync(agents.find(a=>a.id===contracts[0][0]).args[0])).digest('hex');
return {adapterOK,launcherSha};
}
if (process.argv[2] === '--adapters') { console.log(JSON.stringify(adapters())); process.exit(0); }
if (!fs.existsSync(unitFile)) { console.log(JSON.stringify({missing:true})); process.exit(0); }
if (fs.realpathSync(unitFile) !== unitFile) throw Error('unit must not be a symlink');
const unit = fs.readFileSync(unitFile, 'utf8');
const lines = unit.split('\n').filter(x => x.startsWith('ExecStart='));
if (lines.length !== 1) throw Error('unit must have exactly one ExecStart');
const unitDll = lines[0].slice(10).split(' ')[1];
const args = lines[0].slice(10).replaceAll('%h', process.env.HOME).split(' ');
const dll = args[1], flag = name => args[args.indexOf(name) + 1];
if (!dll?.startsWith(root + '/bin/') || !dll.endsWith('/Assembly.Remote.dll') || args[2] !== 'serve'
 || flag('--home') !== root + '/serve' || flag('--port') !== String(lane.port)
 || ![root + '/serve/env', path.join(process.env.HOME, '.config', lane.data, 'env')].includes(flag('--token-file')))
 throw Error('existing unit is outside this lane or has an unsupported shape');
if (!fs.realpathSync(dll).startsWith(root + '/bin/')) throw Error('backend resolves outside lane bin');
const pid = execFileSync('systemctl', ['--user', 'show', lane.unit, '-p', 'MainPID', '--value'], {encoding:'utf8'}).trim();
const cmdline = fs.readFileSync('/proc/' + pid + '/cmdline', 'utf8').split('\0');
if (cmdline[1] !== dll || !cmdline.includes('serve')) throw Error('running process differs from unit');
const token = fs.readFileSync(flag('--token-file'), 'utf8').split('\n').find(x=>x.startsWith('ASSEMBLY_TOKEN='))?.slice(15);
if (!token) throw Error('lane token is unavailable');
let adapterOK=false,launcherSha='';try{({adapterOK,launcherSha}=adapters())}catch{}
let toolsKey, adapterSources, seedCohort;
try {
 const state=JSON.parse(fs.readFileSync(root+'/adapters/dev-tools.json','utf8'));
 const valid=await (async()=>{const fs=await import('node:fs/promises');const {join}=await import('node:path');return await (${toolsCurrent.toString()})(root+'/adapters',root+'/serve/agents.json',state.key)})();
 if(valid) { toolsKey=state.key; adapterSources=state.sources; }
} catch {}
try {if(fs.existsSync(root+'/serve/assembly.db')) seedCohort=JSON.parse(fs.readFileSync(root+'/serve/dev-seed-cohort.json','utf8')).cohort;} catch {}
const socket = new WebSocket('ws://127.0.0.1:' + lane.port + '/socket');
// A deadline for one handshake, never a retry or a poll.
const deadline = setTimeout(()=>{console.error('host welcome timed out');process.exit(1)},8000);
socket.onerror=()=>{console.error('host socket unreachable');process.exit(1)};
socket.onopen=()=>socket.send(JSON.stringify({t:'hello',protocol:1,token}));
socket.onmessage=e=>{
 const frame=JSON.parse(String(e.data));
 if(frame.t!=='welcome' || frame.machine!=='workbox-test' || frame.protocol!==1 || frame.format!==5 || !/^[a-f0-9]{40}$/.test(frame.commit??'')) {
  console.error('host did not provide a compatible authenticated identity');process.exit(1);
 }
 clearTimeout(deadline);socket.close();process.stdin.pause();
 console.log(JSON.stringify({commit:frame.commit,backendIdentity:frame.backendIdentity,remoteContract:frame.remoteContract,engine:frame.engine,dll,unitDll,pid,unitSha:createHash('sha256').update(unit).digest('hex'),adapterOK,launcherSha,toolsKey,adapterSources,seedCohort}));
};
`;
export type Probe = { missing?: boolean; commit: string; backendIdentity?: string; remoteContract?: string; toolsKey?: string; adapterSources?: string; seedCohort?: string; engine: string; dll: string; unitDll?: string; pid: string; unitSha: string; adapterOK: boolean; launcherSha: string };
