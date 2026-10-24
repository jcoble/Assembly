import { execFileSync } from 'node:child_process';
import { appendFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const prefix = 'assembly-backend-v';
const stableTag = /^assembly-backend-v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;

export function backendReleaseVersion(ref: string, remoteTags: string): string {
  const tags = remoteTags.trim().split(/\s+/).filter((value) => value.startsWith('refs/tags/'))
    .map((value) => value.slice('refs/tags/'.length));
  if (ref.startsWith('refs/tags/')) {
    const tag = ref.slice('refs/tags/'.length);
    if (!stableTag.test(tag) || !tags.includes(tag)) throw new Error('Expected an existing stable backend tag');
    return tag.slice(prefix.length);
  }
  if (ref !== 'refs/heads/main') {
    throw new Error('Backend releases require main or an existing backend tag');
  }
  const versions = tags.flatMap((tag) => {
    const match = stableTag.exec(tag);
    return match ? [match.slice(1).map(Number)] : [];
  }).sort((a, b) => b[0] - a[0] || b[1] - a[1] || b[2] - a[2]);
  const [major, minor, patch] = versions[0] ?? [0, 1, -1];
  return `${major}.${minor}.${patch + 1}`;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const tags = execFileSync('git', ['ls-remote', '--tags', 'origin', `${prefix}*`], { encoding: 'utf8' });
  const version = backendReleaseVersion(process.env.GITHUB_REF ?? '', tags);
  if (!process.env.GITHUB_OUTPUT) throw new Error('GITHUB_OUTPUT is required');
  appendFileSync(process.env.GITHUB_OUTPUT, `version=${version}\ntag=${prefix}${version}\n`);
}
