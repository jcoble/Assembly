export type Behavior = { name: string; state: 'PASS' | 'FAIL' | 'BLOCKED' | 'NOT AFFECTED'; details: string; proof: string[] };
export type Result = { sha: string; summary: string; behaviors: Behavior[];
  tests: { name: string; listed: number; ran: number; passed: number }[];
  proof: { file: string; description: string; seconds?: number }[] };
export type Verdict = { state: 'success' | 'failure' | 'pending' | 'error'; label: string; result?: Result };
const text = (x: unknown): x is string => typeof x === 'string' && x.trim().length > 0 && x.length < 20000;
export function validate(value: any, sha: string): Result {
  if (!value || value.sha !== sha || !text(value.summary) || !Array.isArray(value.behaviors) ||
      value.behaviors.length < 1 || value.behaviors.length > 100 || !Array.isArray(value.tests) ||
      !Array.isArray(value.proof) || value.proof.length > 6) throw Error('Malformed result');
  for (const b of value.behaviors) {
    if (!b || !text(b.name) || !text(b.details) || !['PASS','FAIL','BLOCKED','NOT AFFECTED'].includes(b.state) ||
        !Array.isArray(b.proof) || !b.proof.every(text)) throw Error('Malformed behavior');
  }
  for (const t of value.tests) {
    if (!t || !text(t.name) || ![t.listed,t.ran,t.passed].every(x=>Number.isSafeInteger(x) && x >= 0) ||
        t.passed > t.ran || t.ran > t.listed) throw Error('Malformed test count');
  }
  let screenshots = 0, videos = 0;
  for (const p of value.proof) {
    if (!p || !text(p.description) || !/^[A-Za-z0-9][A-Za-z0-9_.-]*\.(png|jpg|jpeg|mp4|webm|txt|log|md|html)$/.test(p.file))
      throw Error('Malformed proof path');
    if (/\.(png|jpg|jpeg)$/.test(p.file)) screenshots++;
    if (/\.(mp4|webm)$/.test(p.file)) {
      videos++;
      if (typeof p.seconds !== 'number' || p.seconds <= 0 || p.seconds > 120) throw Error('Video exceeds proof budget');
    }
  }
  if (screenshots > 4 || videos > 1 || new Set(value.proof.map((p:any)=>p.file)).size !== value.proof.length)
    throw Error('Invalid proof budget');
  return value;
}
export function verdict(result: Result, uploaded: Record<string, string>): Verdict {
  const failed = result.behaviors.some(b=>b.state === 'FAIL') ||
    result.tests.some(t=>t.listed === 0 || t.ran !== t.listed || t.passed !== t.ran) ||
    result.proof.some(p=>!uploaded[p.file]) ||
    result.behaviors.some(b=>b.state === 'PASS' && (b.proof.length === 0 || b.proof.some(p=>!uploaded[p])));
  if (failed) return {state:'failure', label:'REDO', result};
  const blocked = result.behaviors.filter(b=>b.state === 'BLOCKED');
  if (blocked.some(b=>!(/needs Mac/i.test(b.details)))) return {state:'failure', label:'BLOCKED', result};
  if (blocked.length) return {state:'pending', label:'needs Mac', result};
  return {state:'success', label:result.behaviors.every(b=>b.state === 'NOT AFFECTED') ? 'NOT AFFECTED' : 'PASS', result};
}
