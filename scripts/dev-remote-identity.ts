import { createHash } from 'node:crypto';
import { command } from './dev-remote-process.ts';

// The Remote -> Infrastructure -> Application/Engine -> Domain project closure. Common MSBuild/NuGet inputs
// and the ACP 2 launcher contract are included; Rust, Desktop, tests and deployment tooling are not build inputs.
export function backendInput(file: string) {
  return /^dotnet\/Assembly\.(Remote|Infrastructure|Application|Engine|Domain)\//.test(file)
    || /^(dotnet\/)?(Directory\.[^/]+\.(props|targets)|[Nn]u[Gg]et\.[Cc]onfig|global\.json)$/.test(file)
    || ['scripts/adapters/claude-launcher.ts', 'scripts/package-adapters.ts'].includes(file);
}
export async function backendIdentity(root: string, commit: string, signal?: AbortSignal) {
  if (!/^[a-f0-9]{40}$/.test(commit)) throw new Error('unknown running backend commit');
  const tree = await command('git', ['ls-tree', '-r', commit], { cwd: root, signal });
  const inputs = tree.split('\n').filter(line => backendInput(line.split('\t')[1] ?? ''));
  if (!inputs.some(line => line.endsWith('dotnet/Assembly.Remote/Assembly.Remote.csproj')))
    throw new Error('remote backend source is missing from the selected commit');
  return createHash('sha256').update('assembly-dev-remote-v1:ACP2\n' + inputs.join('\n')).digest('hex');
}
export async function requireCleanBackend(root: string, signal?: AbortSignal) {
  const paths = await command('git', ['diff', '--name-only', 'HEAD'], { cwd: root, signal });
  const untracked = await command('git', ['ls-files', '--others', '--exclude-standard'], { cwd: root, signal });
  if ([...paths.split('\n'), ...untracked.split('\n')].some(backendInput))
    throw new Error('uncommitted backend changes: Workbox Test stays behind this checkout; commit them before launching again');
}
export type Lane = { name: string; number: number; unit: string; data: string; port: number };
export function lane(name: string, number: number): Lane {
  if (!/^[a-z0-9][a-z0-9-]*$/.test(name) || !Number.isInteger(number) || number < 1 || number > 9)
    throw new Error('automatic remote sync refuses main or an invalid lane');
  return { name, number, unit: `assembly-dotnet-${name}`, data: `assembly-dotnet-${name}`, port: 9797 + number * 10 };
}
