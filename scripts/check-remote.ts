// Read only, authenticated lane preflight before a PR: an exact backend match. No deploy and no saved-machine lookup.
import { fileURLToPath } from 'node:url';
import { backendIdentity, lane, requireCleanBackend } from './dev-remote-identity.ts';
import { command, quote, sshOptions } from './dev-remote-process.ts';
import { probeSource, type Probe } from './dev-remote-probe.ts';

const root = fileURLToPath(new URL('../', import.meta.url));
try {
  const [name, number, selected = 'HEAD'] = process.argv.slice(2);
  const target = !name && number === '0'
    ? { name: '', number: 0, unit: 'assembly-dotnet', data: 'assembly-dotnet', port: 9797 }
    : lane(name ?? '', Number(number));
  const commit = await command('git', ['rev-parse', '--verify', `${selected}^{commit}`], { cwd: root });
  await requireCleanBackend(root);
  const identity = await backendIdentity(root, commit);
  const read = `export PATH="$HOME/.local/bin:$HOME/.dotnet:/usr/local/bin:/usr/bin:/bin"; node --input-type=module -e ${quote(probeSource)} ${quote(JSON.stringify(target))}`;
  const actual = JSON.parse(await command('ssh', [...sshOptions, 'workbox-test', read])) as Probe;
  const result = `remote ${actual.commit ?? 'not installed'}; desktop ${commit}`;
  if (!actual.backendIdentity || actual.backendIdentity !== identity) throw Error(`backend mismatch: ${result}; match them with dotnet/scripts/workbox.sh serve or the Deploy remote backend command`);
  console.log(`backend matches: ${result}`);
} catch (error) {
  console.error(error instanceof Error ? error.message : String(error));
  process.exitCode = 1;
}
