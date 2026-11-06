import { join } from 'node:path';
import { command } from './common.ts';
import type { Config } from './common.ts';

export const keyMarker='assembly-verify-pr';
export function firewallRules(c:Config) {
  const uid=command('id',['-u',c.worker]).trim();
  if(!/^[0-9]+$/.test(uid) || !c.livePorts.length) throw Error('Account audit failed: missing UID or live ports');
  return c.livePorts.map(port=>['OUTPUT','-p','tcp','--dport',String(port),'-m','owner','--uid-owner',uid,
    '-m','comment','--comment',keyMarker,'-j','REJECT']);
}
export function auditAccount(c:Config) {
  const home=c.ownerHome;
  for(const path of [home,...['.ssh','.config','.local','.codex','.claude','dev/work/Assembly'].map(x=>join(home,x))]) {
    // An unavailable sudo/audit command must block, never masquerade as a successful denial.
    const access=command('sudo',['-n','-u',c.worker,'--','/bin/sh','-c',
      'if [ -r "$1" ] || [ -x "$1" ]; then echo READABLE; else echo DENIED; fi','verify-read-audit',path]).trim();
    console.log(`${access}: ${path}`);
    if(access!=='DENIED') throw Error('Account audit failed: owner data is readable or audit is invalid');
  }
  const groups=command('id',['-nG',c.worker]).trim().split(/\s+/);
  if(groups.some(x=>['sudo','wheel','docker','lxd','adm','systemd-journal'].includes(x)))
    throw Error('Account audit failed: privileged group membership');
}
export function auditFirewall(c:Config) {
  for(const rule of firewallRules(c)) for(const tool of ['iptables','ip6tables']) {
    try { command('sudo',['-n',tool,'-C',...rule]); }
    catch { throw Error('Account audit failed: missing live-port firewall rule'); }
  }
}
