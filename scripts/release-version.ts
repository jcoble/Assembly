// The one release version: the tag must name exactly the version desktop/Cargo.toml declares.
import { appendFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { desktopVersion } from './desktop-release.ts';

export function releaseVersion(ref: string, version: string): string {
  const expected = `refs/tags/assembly-v${version}`;
  if (ref !== expected) throw new Error(`Releases run only from the tag ${expected}, not ${ref || 'an unknown ref'}`);
  return version;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const version = releaseVersion(process.env.GITHUB_REF ?? '', desktopVersion());
  if (!process.env.GITHUB_OUTPUT) throw new Error('GITHUB_OUTPUT is required');
  appendFileSync(process.env.GITHUB_OUTPUT, `version=${version}\ntag=assembly-v${version}\n`);
}
