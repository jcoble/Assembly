// Which CI jobs a run needs. Chosen by job-level conditions (.github/workflows/ci.yml), so a skipped job still reports
// success to a required check. Anything this can't read runs everything: the jobs fail open.
import { appendFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';
import { backendInput } from './dev-remote-identity.ts';

export type Run = { event: string; draft: boolean; files: string[] };

export interface CiJobs {
  full: boolean;
  web: boolean;
  desktop: boolean;
  backend: boolean;
  scripts: boolean;
}

const ALL_JOBS: CiJobs = { full: true, web: true, desktop: true, backend: true, scripts: true };
const NO_JOBS: CiJobs = { full: false, web: false, desktop: false, backend: false, scripts: false };

// Draft pull requests run none of the suite; it runs when one is marked ready, on main and by manual dispatch.
export function jobs({ event, draft, files }: Run): CiJobs {
  if (event !== 'pull_request') return { ...ALL_JOBS };
  if (draft) return { ...NO_JOBS };
  const ciChanged = files.includes('.github/workflows/ci.yml');
  return {
    full: true,
    web: ciChanged || files.some(file => file.startsWith('web/') || file === 'package.json'),
    desktop: ciChanged || files.some(file => file.startsWith('desktop/') || file === 'scripts/shutdown-timing.test.sh'),
    backend: ciChanged || files.some(file => file.startsWith('dotnet/') || file === 'desktop/Cargo.toml' || backendInput(file)),
    scripts: ciChanged || files.some(file => file.startsWith('scripts/') || file.startsWith('.githooks/') || file === 'package.json'),
  };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const { EVENT = '', DRAFT = '', BASE = '', HEAD = '', GITHUB_OUTPUT } = process.env;
  const files = EVENT === 'pull_request' ? execFileSync('git', ['diff', '--name-only', `${BASE}...${HEAD}`], { encoding: 'utf8' }).split('\n').filter(Boolean) : [];
  const selected = jobs({ event: EVENT, draft: DRAFT === 'true', files });
  const output = `full=${selected.full}\nweb=${selected.web}\ndesktop=${selected.desktop}\nbackend=${selected.backend}\nscripts=${selected.scripts}\n`;
  if (GITHUB_OUTPUT) appendFileSync(GITHUB_OUTPUT, output);
  console.log(output.trim());
}
