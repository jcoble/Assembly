// Runs in provider-updates.yml between `pnpm update --latest` and signing. A new adapter release
// that looks like an npm account takeover fails the job, so nothing is published that night.
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const packages = ['@agentclientprotocol/codex-acp', '@agentclientprotocol/claude-agent-acp'];
const installScripts = ['preinstall', 'install', 'postinstall'];

// The fields of `npm view <pkg>@<version> --json` this check reads. npm prints people as
// `{ name, email }` objects; people are compared by name.
type NpmPerson = { name: string; email?: string };
export type NpmRelease = {
  _npmUser?: NpmPerson;
  maintainers?: NpmPerson[];
  scripts?: Record<string, string>;
  dist?: { attestations?: { provenance?: unknown } };
};

export function releaseConcerns(previous: NpmRelease, next: NpmRelease): string[] {
  const concerns: string[] = [];
  const maintainers = (previous.maintainers ?? []).map((person) => person.name);
  const publisher = next._npmUser?.name;
  // Both adapters publish from GitHub Actions trusted publishing, whose user is never a
  // maintainer, so the previous release's publisher counts as known too.
  if (publisher !== previous._npmUser?.name && !maintainers.includes(publisher ?? '')) {
    concerns.push(`was published by ${publisher ?? 'an unknown user'}, who is neither the previous `
      + `publisher (${previous._npmUser?.name}) nor one of its maintainers`);
  }
  const sorted = (names: string[]) => [...names].sort().join(', ');
  const nextMaintainers = (next.maintainers ?? []).map((person) => person.name);
  if (sorted(nextMaintainers) !== sorted(maintainers)) {
    concerns.push(`has its maintainers changed from [${sorted(maintainers)}] to [${sorted(nextMaintainers)}]`);
  }
  for (const script of installScripts) {
    if (next.scripts?.[script] && !previous.scripts?.[script]) {
      concerns.push(`adds a ${script} script: ${next.scripts[script]}`);
    }
  }
  // npm also issues a publish attestation for manual publishes; only `provenance` ties a release to
  // the CI build that made it.
  if (previous.dist?.attestations?.provenance && !next.dist?.attestations?.provenance) {
    concerns.push('has no npm provenance, but the previous release did');
  }
  return concerns;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  // `pnpm update --latest` rewrites package.json, so the committed file holds the previous pins.
  const pinned = JSON.parse(execFileSync('git', ['show', 'HEAD:./package.json'], { encoding: 'utf8' })).dependencies;
  const view = (spec: string): NpmRelease =>
    JSON.parse(execFileSync('npm', ['view', spec, '--json'], { encoding: 'utf8' }));
  let failed = false;
  for (const name of packages) {
    const before: string = pinned[name];
    const after: string = JSON.parse(readFileSync(`node_modules/${name}/package.json`, 'utf8')).version;
    if (before === after) {
      console.log(`${name} ${after}: unchanged`);
      continue;
    }
    const concerns = releaseConcerns(view(`${name}@${before}`), view(`${name}@${after}`));
    for (const concern of concerns) console.error(`${name} ${after} ${concern}.`);
    if (concerns.length === 0) console.log(`${name} ${before} -> ${after}: same publisher, maintainers and provenance`);
    failed ||= concerns.length > 0;
  }
  if (failed) {
    console.error('Not packaging the adapters: a new release may come from a taken-over npm account.');
    process.exit(1);
  }
}
