import { spawnSync } from 'node:child_process';
import { chmodSync, existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { homedir } from 'node:os';
import { randomUUID } from 'node:crypto';
import { pathToFileURL } from 'node:url';

export type Config = {
  repository: string; owner: string; state: string; clone: string; workerHome: string;
  worker: string; worktrees: string; skipDrafts: boolean; allowCommentReruns: boolean;
  ownerHome: string; workerPath: string;
  evidence: string;
  runtimeSeconds: number; minimumMemoryMB: number; livePorts: number[];
};
export type Context = {
  pr: number; sha: string; base: string; started: string; output: string; worktree: string;
  task: string; title: string; body: string; ci: unknown;
};
export const repository = 'jcoble/Assembly';
export const owner = 'jcoble';
export function configPath() {
  return process.env.VERIFY_CONFIG || join(homedir(), '.local/share/verify-pr/config.json');
}
export function config(): Config { return JSON.parse(readFileSync(configPath(), 'utf8')); }
export function atomic(path: string, value: unknown, mode = 0o600) {
  const temporary = path + '.' + randomUUID() + '.tmp';
  writeFileSync(temporary, JSON.stringify(value, null, 2) + '\n', { mode, flag: 'wx' });
  renameSync(temporary, path);
}
export function command(binary: string, args: string[], options: {
  cwd?: string; input?: string; env?: NodeJS.ProcessEnv; timeout?: number; stdinNull?: boolean; maxBuffer?:number;
} = {}) {
  const {stdinNull, ...settings} = options;
  const result = spawnSync(binary, args, { encoding: 'utf8', timeout: 60000,
    maxBuffer: 16 * 1024 * 1024, stdio: [stdinNull ? 'ignore' : 'pipe', 'pipe', 'pipe'], ...settings });
  // Never echo subprocess output: providers/gh may include credentials in diagnostics.
  if (result.error || result.status !== 0) throw Error(`${binary} failed (${result.error?.code || result.status})`);
  return result.stdout;
}
export function gh(c: Config, endpoint: string, fields: Record<string, string> = {}, method?: string): any {
  const args = ['api', '--hostname', 'github.com', ...(method ? ['--method', method] : []), endpoint];
  for (const [key, value] of Object.entries(fields)) args.push('-f', `${key}=${value}`);
  return JSON.parse(command('gh', args) || 'null');
}
export function status(c: Config, ctx: Context, state: string, description: string) {
  gh(c, `repos/${c.repository}/statuses/${ctx.sha}`, {
    state, context: 'verify/acceptance', description: description.slice(0, 140),
  }, 'POST');
}
export function ci(c:Config,sha:string) {
  const response=gh(c,`repos/${c.repository}/commits/${sha}/check-runs`);
  if(!Array.isArray(response?.check_runs)) throw Error('CI response has no jobs');
  return response.check_runs.map((job:any)=>({name:job.name,status:job.status,
    conclusion:job.conclusion,url:job.html_url}));
}
export function context(c: Config, directory: string): Context {
  if (!resolve(directory).startsWith(resolve(c.state) + '/run-')) throw Error('Run is outside configured state');
  const ctx: Context = JSON.parse(readFileSync(join(directory, 'context.json'), 'utf8'));
  if (!Number.isSafeInteger(ctx.pr) || ctx.pr < 1 || !/^[a-f0-9]{40}$/.test(ctx.sha) ||
      !/^[a-f0-9]{40}$/.test(ctx.base)) throw Error('Invalid saved context');
  const expected = join(c.worktrees, `verify-pr-${ctx.pr}-${ctx.sha}`);
  if (ctx.worktree !== expected || !resolve(ctx.output).startsWith(resolve(c.workerHome) + '/'))
    throw Error('Invalid saved lane paths');
  return ctx;
}
export function worker(c: Config, binary: string, args: string[], options: {
  cwd?: string; input?: string; timeout?: number; env?: NodeJS.ProcessEnv; stdinNull?: boolean; maxBuffer?:number;
} = {}) {
  const {env: overrides, ...settings} = options;
  const env = { HOME: c.workerHome, USER: c.worker, LOGNAME: c.worker,
    PATH: c.workerPath, ...overrides };
  return command('sudo', ['-n', '-u', c.worker, '--', 'env', '-i',
    ...Object.entries(env).map(([key, value]) => `${key}=${value}`), binary, ...args], settings);
}
export function workerRead(c:Config,path:string,maximum:number) {
  // Run the fixed reader as verify: owner cannot read the worker's mode-0600 results directly.
  // No-follow and bounded reads prevent symlink swaps and unbounded pipe/device output.
  const reader=`const fs=require('node:fs');
const fd=fs.openSync(process.argv[1],fs.constants.O_RDONLY|fs.constants.O_NOFOLLOW|fs.constants.O_NONBLOCK);
try { const stat=fs.fstatSync(fd), maximum=Number(process.argv[2]);
if(!stat.isFile()||stat.size>maximum) process.exit(1);
const bytes=Buffer.alloc(stat.size);let offset=0;
while(offset<bytes.length){const n=fs.readSync(fd,bytes,offset,bytes.length-offset,offset);if(!n)break;offset+=n;}
process.stdout.write(bytes.subarray(0,offset).toString('base64'));
} finally {fs.closeSync(fd);}`;
  return Buffer.from(worker(c,'node',['-e',reader,path,String(maximum)],{maxBuffer:maximum*2+1024}),'base64');
}
export function privateDirectory(path: string) { mkdirSync(path, { recursive: true, mode: 0o700 }); chmodSync(path, 0o700); }
export function main(url: string, action: () => void) {
  if (process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === url) {
    try { action(); } catch (error) {
      console.error(error instanceof Error ? error.message : 'Verification failed'); process.exitCode = 1;
    }
  }
}

// Only exact units durably recorded by this installation may be stopped.
export function recordedUnits(state:string): string[] {
  const path=join(state,'units.json');
  if(!existsSync(path)) return [];
  const units=JSON.parse(readFileSync(path,'utf8'));
  if(!Array.isArray(units) || !units.every(u=>typeof u==='string' && /^verify-pr-[1-9][0-9]*\.service$/.test(u)))
    throw Error('Invalid recorded units');
  return units;
}
export function stopUnit(unit:string) {
  try { command('systemctl',['--user','stop',unit]); }
  catch {
    if(command('systemctl',['--user','show',unit,'--property=LoadState','--value']).trim()!=='not-found')
      throw Error('Previous verifier unit could not be stopped');
  }
}
