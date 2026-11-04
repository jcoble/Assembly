// Which CI jobs a run needs. Chosen by job-level conditions (.github/workflows/ci.yml), so a skipped job still reports
// success to a required check. Anything this can't read runs everything: the jobs fail open.
import { appendFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';
import { backendInput } from './dev-remote-identity.ts';

export type Run = { event: string; draft: boolean; files: string[] };

// Draft pull requests run none of the suite; it runs when one is marked ready, on main and by manual dispatch.
export function jobs({ event, draft, files }: Run) {
  if (event !== 'pull_request') return { full: true, backend: true };
  if (draft) return { full: false, backend: false };
  return { full: true, backend: files.some(file => file.startsWith('dotnet/') || file === '.github/workflows/ci.yml' || file === 'desktop/Cargo.toml' || backendInput(file)) };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const { EVENT = '', DRAFT = '', BASE = '', HEAD = '', GITHUB_OUTPUT } = process.env;
  const files = EVENT === 'pull_request' ? execFileSync('git', ['diff', '--name-only', `${BASE}...${HEAD}`], { encoding: 'utf8' }).split('\n').filter(Boolean) : [];
  const { full, backend } = jobs({ event: EVENT, draft: DRAFT === 'true', files });
  const output = `full=${full}\nbackend=${backend}\n`;
  if (GITHUB_OUTPUT) appendFileSync(GITHUB_OUTPUT, output);
  console.log(output.trim());
}
