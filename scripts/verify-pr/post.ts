import { existsSync, readFileSync, mkdirSync, writeFileSync, renameSync } from 'node:fs';
import { join } from 'node:path';
import { config, context, atomic, command, gh, status, worker, workerRead, main, ci } from './common.ts';
import type { Config, Context } from './common.ts';
import { validate, verdict } from './result.ts';
import type { Verdict } from './result.ts';

const marker = '<!-- verify/acceptance -->';
function upload(c: Config, ctx: Context, directory: string, proof: {file:string; description:string}[]) {
  const links: Record<string, string> = {};
  if (!/^TSK-[0-9]+$/.test(ctx.task)) return links;
  const assets = join(directory, 'assets'); mkdirSync(assets, { recursive:true, mode:0o700 });
  for (const p of proof) {
    try {
      const bytes=workerRead(c,join(ctx.output,p.file),100*1024*1024);
      const destination=join(assets,p.file),temporary=destination+'.upload';
      writeFileSync(temporary,bytes,{mode:0o600}); renameSync(temporary,destination);
      const output = command(c.evidence, ['add', join(assets,p.file), '--task',ctx.task,
        '--desc',p.description,'--project','Assembly'], { cwd:directory, env: evidenceEnv() });
      const link = output.match(/https:\/\/drive\.google\.com\/(?:file\/d\/[A-Za-z0-9_-]+\/view|uc\?export=view&id=[A-Za-z0-9_-]+)/)?.[0];
      if (!link) throw Error('Upload did not return an asset link');
      links[p.file] = link;
    } catch { /* Missing or failed uploads downgrade PASS below. Never expose uploader diagnostics. */ }
  }
  return links;
}
// Explicit environment excludes GitHub repository overrides; evidence must not edit an unrelated PR.
function evidenceEnv() {
  const env = { ...process.env };
  for (const key of Object.keys(env)) if (/^(GH_REPO|GITHUB_REPOSITORY|GIT_DIR|GIT_WORK_TREE)$/.test(key)) delete env[key];
  return env;
}
function makeReceipt(c: Config, ctx: Context, directory: string) {
  // Only the service can write this run directory; never read blockers from ctx.output.
  const blockerPath=join(directory,'blocker.json');
  const blocker=existsSync(blockerPath) ? JSON.parse(readFileSync(blockerPath,'utf8')).reason : undefined;
  let outcome: Verdict = {state:'error',label:blocker || 'no result'};
  let links: Record<string,string> = {};
  if(!blocker) try {
    const result = validate(JSON.parse(workerRead(c,join(ctx.output,'result.json'),1024*1024).toString('utf8')),ctx.sha);
    const changed=worker(c,'git',['-C',ctx.worktree,'status','--porcelain','--untracked-files=all','--','.',':!evidence']).trim();
    const tested=worker(c,'git',['-C',ctx.worktree,'rev-parse','HEAD']).trim();
    if(changed || tested!==ctx.sha) result.behaviors.push({name:'Verification-only boundary',state:'FAIL',
      details:'Verifier changed the lane or its HEAD; the declared PR commit was not verified cleanly.',proof:[]});
    const changedFiles=worker(c,'git',['-C',ctx.worktree,'diff','--name-only',`${ctx.base}...${ctx.sha}`]).trim().split('\n').filter(Boolean);
    const codeChanged=changedFiles.some(file=>!(/\.(md|rst)$/i.test(file) || /^(docs\/.*\.txt|(?:README|LICENSE|NOTICE)(?:\.[^/]*)?)$/i.test(file)));
    let receiptsValid=true;
    const receipts: {file:string;description:string}[]=[];
    for(const test of result.tests) {
      try {
        if(!test.receipt || !/^[A-Za-z0-9][A-Za-z0-9_.-]*\.(txt|log|md|html)$/.test(test.receipt)) throw Error('Invalid receipt');
        const contents=workerRead(c,join(ctx.output,test.receipt),1024*1024).toString('utf8');
        if(!contents.trim() || !contents.includes(test.name)) throw Error('Missing named tests');
        if(!receipts.some(p=>p.file===test.receipt)) receipts.push({file:test.receipt,description:`Verifier's test receipt: ${test.name}`});
      } catch { receiptsValid=false; }
    }
    links = upload(c,ctx,directory,[...result.proof.filter(p=>!receipts.some(r=>r.file===p.file)),...receipts]);
    outcome = verdict(result,links,codeChanged,receiptsValid);
  } catch { /* Missing, malformed, killed or timed-out runs are explicit no-result. */ }
  const details = outcome.result;
  let latestCI:unknown;
  try { latestCI=ci(c,ctx.sha); }
  catch { latestCI='BLOCKED: gh could not read CI jobs; dispatch snapshot follows'; }
  const body = [marker, `## verify/acceptance: ${outcome.label}`,
    `PR #${ctx.pr} · tested commit \`${ctx.sha}\` · Workbox · started ${ctx.started}`,
    blocker || details?.summary || 'No usable result. The verifier crashed, timed out, or returned an invalid result.',
    ...(details?.behaviors.map(b=>`- **${b.state}** ${b.name}: ${b.details}${b.proof.map(p=>links[p] ? ` [${p}](${links[p]})` : '').join('')}`) || []),
    '', "Verifier's test receipts (listed / ran / passed; reported by the verifier):",
    ...(details?.tests.map(t=>`- ${t.listed===0||t.ran!==t.listed||t.passed!==t.ran?'FAIL: ':''}${t.name}: ${t.listed} / ${t.ran} / ${t.passed}${t.receipt && links[t.receipt] ? ` [${t.receipt}](${links[t.receipt]})` : ' (no validated receipt)'}`) || []),
    '', 'CI jobs at posting:', '```json', JSON.stringify(latestCI,null,2), '```',
    'CI jobs at dispatch:', '```json', JSON.stringify(ctx.ci,null,2), '```',
    `Task: ${ctx.task || 'BLOCKED: no task key found'}`,
    'The task must also link the proof and record acceptance; this service does not edit Notion.',
  ].join('\n');
  return {state:outcome.state, label:outcome.label, body, links};
}
function comment(c: Config, ctx: Context, body: string) {
  // Follow pages so an old marker does not create duplicate comments after 100 discussions.
  let found: any;
  for (let page=1; page<=100; page++) {
    const notes = gh(c,`repos/${c.repository}/issues/${ctx.pr}/comments?per_page=100&page=${page}`);
    if (!Array.isArray(notes)) throw Error('Comments response is invalid');
    found = notes.find(n=>n.user?.login===c.owner && typeof n.body==='string' && n.body.includes(marker));
    if (found || notes.length < 100) break;
    if(page===100) throw Error('Comment pagination exceeded limit');
  }
  if (found) gh(c,`repos/${c.repository}/issues/comments/${found.id}`,{body},'PATCH');
  else gh(c,`repos/${c.repository}/issues/${ctx.pr}/comments`,{body},'POST');
}
export function post(directory: string, locked=false) {
  const c=config(), ctx=context(c,directory);
  if (existsSync(join(directory,'posted.json'))) return;
  if(!locked) {
    command('flock',['--exclusive',join(directory,'post.lock'),process.execPath,
      join(import.meta.dirname,'post.ts'),directory,'--locked'],{timeout:120000});
    return;
  }
  const receiptPath=join(directory,'receipt.json');
  const receipt=existsSync(receiptPath) ? JSON.parse(readFileSync(receiptPath,'utf8')) : makeReceipt(c,ctx,directory);
  atomic(receiptPath,receipt); // Durable before any network mutation, including a failed status write.
  let posted=false;
  for (let attempt=0;attempt<3;attempt++) {
    try { status(c,ctx,receipt.state,receipt.label); comment(c,ctx,receipt.body); posted=true; break; }
    catch { /* Bounded retries; next valid dispatch also flushes this saved receipt. */ }
  }
  if (!posted) throw Error('Posting failed after 3 attempts; saved receipt awaits next dispatch');
  atomic(join(directory,'posted.json'),{at:new Date().toISOString()});
  try {
    if (worker(c,'git',['-C',ctx.worktree,'status','--porcelain','--untracked-files=all']).trim()) return;
    worker(c,'git',['-C',c.clone,'worktree','remove',ctx.worktree]);
  } catch { /* Preserve missing/dirty lanes and their proof; never force removal. */ }
}
main(import.meta.url,()=>post(process.argv[2] || '',process.argv[3]==='--locked'));
