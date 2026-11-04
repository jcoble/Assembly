// Shared local/remote development dependency check. Installs exact versions in lane-owned immutable directories.
import { constants as fsConstants, promises as fs } from 'node:fs';
import { createHash, randomUUID } from 'node:crypto';
import { join, dirname } from 'node:path';
import { command } from './dev-remote-process.ts';
// Workbox Test has no npm, so a machine without one gets this pinned copy in the lane's own folder.
export const npmPin = { version: '11.17.0', integrity: 'sha512-PurxiZexEHDTE4SSaLI3ZrnbAGiZfeyUcQcxcP5D+hfytNAze/D1IzDuInTn9XVLIbAQUnQuSPXJx02LHjLvQw==' };
export const packages = ['@agentclientprotocol/claude-agent-acp', '@agentclientprotocol/codex-acp', '@anthropic-ai/claude-code', '@openai/codex'] as const;
export type ToolVersions = Record<typeof packages[number], string>;
export async function latestTools(signal: AbortSignal): Promise<ToolVersions> {
  return Object.fromEntries(await Promise.all(packages.map(async name => {
    const response = await fetch('https://registry.npmjs.org/' + encodeURIComponent(name) + '/latest', { signal: AbortSignal.any([signal, AbortSignal.timeout(8000)]) });
    if (!response.ok) throw Error('cannot check ' + name + ': registry ' + response.status);
    const version = (await response.json() as { version?: string }).version;
    if (!version || !/^\d+\.\d+\.\d+$/.test(version)) throw Error('invalid stable version for ' + name);
    return [name, version];
  }))) as ToolVersions;
}
export function toolKey(versions: ToolVersions, launcher: string, target: string) {
  return createHash('sha256').update(JSON.stringify({ versions: packages.map(name => [name, versions[name]]), launcher, target })).digest('hex');
}
export async function toolsCurrent(adapters: string, descriptor: string, key: string) {
  try {
    if (await fs.realpath(adapters) !== adapters) return false;
    const state = JSON.parse(await fs.readFile(join(adapters, 'dev-tools.json'), 'utf8'));
    if (state.key !== key || state.descriptorSha !== createHash('sha256').update(await fs.readFile(descriptor)).digest('hex')) return false;
    for (const file of state.packages) {
      if (!file.path.startsWith(adapters + '/') || !(await fs.realpath(file.path)).startsWith(adapters + '/')) return false;
      if (JSON.parse(await fs.readFile(file.path, 'utf8')).version !== file.version) return false;
    }
    if (!state.launcherPath?.startsWith(adapters + '/') || !(await fs.realpath(state.launcherPath)).startsWith(adapters + '/')) return false;
    if (createHash('sha256').update(await fs.readFile(state.launcherPath)).digest('hex') !== state.launcherSha) return false;
    for (const file of state.entries) if (!file.startsWith(adapters + '/') || !(await fs.realpath(file)).startsWith(adapters + '/') || !(await fs.stat(file)).isFile()) return false;
    return true;
  } catch { return false; }
}
async function readAgents(descriptor: string): Promise<Record<string, { cli?: string; args: string[] }>> {
  return Object.fromEntries(JSON.parse(await fs.readFile(descriptor, 'utf8')).map((agent: { id: string }) => [agent.id, agent]));
}
export async function ensureNpm(adapters: string, signal: AbortSignal, run = command, pin = npmPin, path = process.env.PATH ?? '') {
  const home = join(adapters, 'npm', pin.version), bin = join(home, 'bin');
  for (const directory of [bin, ...path.split(':').filter(Boolean)]) {
    try { await fs.access(join(directory, 'npm'), fsConstants.X_OK); return directory; } catch { }
  }
  const stage = home + '.' + randomUUID(), archive = join(stage, 'npm.tgz');
  await fs.mkdir(join(stage, 'bin'), { recursive: true });
  try {
    await run('curl', ['-fsSL', '--connect-timeout', '10', '-o', archive, `https://registry.npmjs.org/npm/-/npm-${pin.version}.tgz`], { signal });
    const actual = 'sha512-' + createHash('sha512').update(await fs.readFile(archive)).digest('base64');
    if (actual !== pin.integrity) throw Error(`npm ${pin.version} archive differs from its pinned integrity`);
    await run('tar', ['-xzf', archive, '-C', stage], { signal });
    await fs.rm(archive);
    await fs.symlink('../package/bin/npm-cli.js', join(stage, 'bin/npm'));
    await fs.rename(stage, home);
    return bin;
  } finally { await fs.rm(stage, { recursive: true, force: true }); }
}
export async function ensureTools(root: string, adapters: string, descriptor: string, versions: ToolVersions, signal: AbortSignal,
  run = command, target = `${process.platform}-${process.arch}`, provideNpm = ensureNpm) {
  const { safePath } = await import('./seed-dev.ts');
  for (const path of [adapters, descriptor, join(adapters, 'clis'), join(adapters, 'assets'), join(adapters, 'bundles')]) safePath(path, false);
  for (const name of packages) if (!/^\d+\.\d+\.\d+$/.test(versions[name] ?? '')) throw Error('invalid managed version for ' + name);
  const launcher = createHash('sha256').update(await fs.readFile(join(root, 'scripts/adapters/claude-launcher.ts'))).digest('hex');
  const key = toolKey(versions, launcher, target);
  if (await toolsCurrent(adapters, descriptor, key)) return { key, changed: false };
  await fs.mkdir(adapters, { recursive: true });
  const npmPath = await provideNpm(adapters, signal, run) + ':' + process.env.PATH;
  const cliKey = createHash('sha256').update(JSON.stringify([versions['@anthropic-ai/claude-code'], versions['@openai/codex'], target])).digest('hex');
  const cli = join(adapters, 'clis', cliKey);
  safePath(cli, false);
  try {
    for (const name of packages.slice(2)) if (JSON.parse(await fs.readFile(join(cli, 'node_modules', name, 'package.json'), 'utf8')).version !== versions[name]) throw Error('CLI version differs');
  } catch {
    const stage = cli + '.' + randomUUID();
    await fs.mkdir(stage, { recursive: true });
    try {
      await run('npm', ['install', '--prefix', stage, '--no-audit', '--no-fund', '--save-exact', '--registry=https://registry.npmjs.org', ...packages.slice(2).map(name => name + '@' + versions[name])], { signal, live: true, env: { PATH: npmPath } });
      await fs.mkdir(dirname(cli), { recursive: true });
      await fs.rename(stage, cli);
    } finally { await fs.rm(stage, { recursive: true, force: true }); }
  }
  const environment = { ...process.env, PATH: join(cli, 'node_modules/.bin') + ':' + npmPath, ASSEMBLY_MANAGED_CLIS: '1' };
  const adapterVersions = { 'claude-agent-acp': versions[packages[0]], 'codex-acp': versions[packages[1]] };
  let keepAdapters = false;
  try {
    const { claude: { args: [claude] }, codex: { args: [codex] } } = await readAgents(descriptor);
    if (claude.startsWith(adapters + '/bundles/') && codex.startsWith(adapters + '/bundles/')) {
      const bundle = dirname(claude);
      keepAdapters = createHash('sha256').update(await fs.readFile(claude)).digest('hex') === launcher;
      for (const name of packages.slice(0, 2)) keepAdapters &&= JSON.parse(await fs.readFile(join(bundle, 'node_modules', name, 'package.json'), 'utf8')).version === versions[name];
    }
  } catch { }
  const adapterKey = createHash('sha256').update(JSON.stringify([adapterVersions, launcher, target])).digest('hex');
  const assets = join(adapters, 'assets', adapterKey);
  safePath(assets, false);
  // Reuse the existing packager, steering smoke proof, archive validator and atomic descriptor writer.
  if (!keepAdapters) {
  // The packager writes an unproven manifest before its smoke checks, so only a finished build reaches this path.
  try { await fs.access(join(assets, `assembly-adapters-${target}.json`)); }
  catch {
    const stage = assets + '.' + randomUUID();
    try {
      await run(process.execPath, [join(root, 'scripts/package-adapters.ts'), 'build', target, JSON.stringify(adapterVersions), stage], { cwd: root, signal, live: true, env: { PATH: npmPath } });
      await fs.rename(stage, assets);
    } finally { await fs.rm(stage, { recursive: true, force: true }); }
  }
  }
  await run('bash', [join(root, 'dotnet/scripts/agents-file.sh'), adapters, descriptor, ...(keepAdapters ? [] : [assets])], { cwd: root, signal, live: true, env: { ...environment, ASSEMBLY_KEEP_CURRENT_ADAPTERS: keepAdapters ? '1' : '' } });
  const agents = await readAgents(descriptor);
  const packageRecords = [] as { path: string; version: string }[];
  const entries = [] as string[];
  for (const [index, id] of ['claude', 'codex'].entries()) {
    const agent = agents[id];
    if (agent?.cli !== join(cli, 'node_modules/.bin', id) || !agent.args?.[0]?.startsWith(adapters + '/bundles/')) throw Error('managed provider descriptor did not adopt current tools');
    const bundle = index === 0 ? dirname(agent.args[0]) : agent.args[0].slice(0, -'/node_modules/@agentclientprotocol/codex-acp/dist/index.js'.length);
    packageRecords.push({ path: join(bundle, 'node_modules', packages[index], 'package.json'), version: versions[packages[index]] });
    packageRecords.push({ path: join(cli, 'node_modules', packages[index + 2], 'package.json'), version: versions[packages[index + 2]] });
    entries.push(agent.args[0], agent.cli);
  }
  const state = { key, versions, launcherPath: agents.claude.args[0], launcherSha: launcher, packages: packageRecords, entries, descriptorSha: createHash('sha256').update(await fs.readFile(descriptor)).digest('hex') };
  const temporary = join(adapters, 'dev-tools.' + randomUUID());
  await fs.writeFile(temporary, JSON.stringify(state), { mode: 0o600, flag: 'wx' });
  await fs.rename(temporary, join(adapters, 'dev-tools.json'));
  if (!await toolsCurrent(adapters, descriptor, key)) throw Error('managed toolchain verification failed');
  return { key, changed: true };
}
