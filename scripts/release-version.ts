// The one release version. It's set by hand in the source (desktop/Cargo.toml); a release only reads it. The tag must
// name exactly that version, and the version must be above every published release, so a release needs a bump first.
import { execFileSync } from 'node:child_process';
import { appendFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { desktopVersion } from './desktop-release.ts';

const published = /^assembly-v(\d+)\.(\d+)\.(\d+)$/;

export function releaseVersion(ref: string, version: string): string {
  const expected = `refs/tags/assembly-v${version}`;
  if (ref !== expected) throw new Error(`Releases run only from the tag ${expected}, not ${ref || 'an unknown ref'}`);
  return version;
}

function isAbove(version: number[], other: number[]): boolean {
  const difference = version.map((part, index) => part - other[index]).find(part => part !== 0);
  return difference !== undefined && difference > 0;
}

export function requireBump(version: string, publishedTags: string[]): void {
  const parts = version.split('.').map(Number);
  for (const tag of publishedTags) {
    const match = published.exec(tag);
    if (match && !isAbove(parts, match.slice(1).map(Number)))
      throw new Error(`${version} is not above the published ${tag}: bump the version in desktop/Cargo.toml first`);
  }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const version = releaseVersion(process.env.GITHUB_REF ?? '', desktopVersion());
  const releases = execFileSync('gh', ['api', '--paginate', `repos/${process.env.GITHUB_REPOSITORY}/releases`, '--jq', '.[] | select(.draft | not) | .tag_name'], { encoding: 'utf8' });
  requireBump(version, releases.split('\n').filter(Boolean));
  if (!process.env.GITHUB_OUTPUT) throw new Error('GITHUB_OUTPUT is required');
  appendFileSync(process.env.GITHUB_OUTPUT, `version=${version}\ntag=assembly-v${version}\n`);
}
