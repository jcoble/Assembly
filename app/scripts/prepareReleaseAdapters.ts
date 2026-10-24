import { execFile } from 'node:child_process';
import { createHash } from 'node:crypto';
import { chmod, copyFile, mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { promisify } from 'node:util';
import { createReadStream } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

const execFileAsync = promisify(execFile);
const projectRoot = new URL('../', import.meta.url);
const adapterDir = new URL('src-tauri/adapters/', projectRoot);
const stopController = new AbortController();

for (const signalName of ['SIGINT', 'SIGTERM']) {
  process.once(signalName, () => stopController.abort(new Error(`Adapter build stopped by ${signalName}`)));
}

async function run(command: string, args: string[]) {
  const { stdout, stderr } = await execFileAsync(command, args, {
    cwd: projectRoot,
    signal: stopController.signal,
    maxBuffer: 8 * 1024 * 1024
  });
  if (stdout.trim()) process.stdout.write(stdout);
  if (stderr.trim()) process.stderr.write(stderr);
}

async function packageVersion(packageName: string) {
  const packageJson = JSON.parse(
    await readFile(new URL(`node_modules/${packageName}/package.json`, projectRoot), 'utf8')
  );
  return packageJson.version;
}

async function sha256(path: URL) {
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(path)) hash.update(chunk);
  return hash.digest('hex');
}

export function launcher({ cli, displayName, environment, runtime }: { cli: string; displayName: string; environment: string; runtime: string }) {
  const environmentLines = environment === 'PATH'
    ? 'PATH="$(dirname "$cli_path"):$PATH"\nexport PATH'
    : `${environment}="$cli_path"\nexport ${environment}`;
  return `#!/bin/sh
set -eu
adapter_dir=$(CDPATH= cd -- "$(dirname "$0")" && pwd)
cli_path=$(command -v ${cli} 2>/dev/null || true)
if [ ! -x "$cli_path" ] && [ -x /bin/zsh ]; then
  cli_path=$(/bin/zsh -lic 'command -v ${cli}' 2>/dev/null | tail -n 1)
fi
if [ ! -x "$cli_path" ]; then
  echo "${displayName} CLI was not found. Install it, then restart Assembly." >&2
  exit 127
fi
${environmentLines}
exec "$adapter_dir/${runtime}" "$@"
`;
}

function denoTarget() {
  const key = `${process.platform}-${process.arch}`;
  const targets: Record<string, string> = {
    'darwin-arm64': 'aarch64-apple-darwin',
    'darwin-x64': 'x86_64-apple-darwin',
    'linux-arm64': 'aarch64-unknown-linux-gnu',
    'linux-x64': 'x86_64-unknown-linux-gnu'
  };
  const target = targets[key];
  if (!target) throw new Error(`Release adapters do not support ${key}`);
  return target;
}

// Bundle one entry to a single ESM file, then compile it into a self-contained binary.
async function compileAdapter(entry: string, output: URL, workDir: string) {
  const bundle = join(workDir, `${output.pathname.split('/').pop()}.mjs`);
  await run('deno', ['bundle', '--platform=deno', '--minify', '-o', bundle, entry]);
  await run('deno', ['compile', '--no-remote', '--no-npm', '-A', `--target=${denoTarget()}`, `--output=${output.pathname}`, bundle]);
}

async function main() {
  await mkdir(adapterDir, { recursive: true });
  if (process.platform === 'win32') {
    throw new Error('Release adapter launchers are not implemented for Windows');
  }
  const codexOutput = new URL('codex-acp-runtime', adapterDir);
  const claudeOutput = new URL('claude-agent-acp-runtime', adapterDir);
  const agyOutput = new URL('agy_acp_server.par', adapterDir);
  const agyHarness = new URL('localharness_external', adapterDir);
  const codexLauncher = new URL('codex-acp', adapterDir);
  const claudeLauncher = new URL('claude-agent-acp', adapterDir);
  const agyLauncher = new URL('agy-acp', adapterDir);

  const bundleDir = await mkdtemp(join(tmpdir(), 'assembly-adapter-bundles-'));
  try {
    await compileAdapter('node_modules/@agentclientprotocol/codex-acp/dist/index.js', codexOutput, bundleDir);
    await compileAdapter('scripts/claudeAcpLauncher.ts', claudeOutput, bundleDir);
  } finally {
    await rm(bundleDir, { recursive: true, force: true });
  }
  // Resolve only Google's official ACP distribution, then package its two files.
  const registry = await fetch('https://cdn.agentclientprotocol.com/registry/v1/latest/registry.json', { signal: stopController.signal });
  if (!registry.ok) throw new Error(`ACP registry: HTTP ${registry.status}`);
  const catalog = await registry.json() as { agents: Array<{ id: string; version: string; distribution: { binary: Record<string, { archive: string }> } }> };
  const official = catalog.agents.find(agent => agent.id === 'antigravity-acp');
  const platform = `${process.platform}-${process.arch === 'arm64' ? 'aarch64' : 'x86_64'}`;
  const archive = official?.distribution.binary[platform]?.archive;
  if (!official || !archive || new URL(archive).origin !== 'https://dl.google.com') {
    throw new Error(`Official Antigravity download unavailable for ${platform}`);
  }
  const downloadDir = await mkdtemp(join(tmpdir(), 'assembly-official-agy-'));
  try {
    const zip = join(downloadDir, 'adapter.zip');
    await run('curl', ['--fail', '--location', '--proto', '=https', '--output', zip, archive]);
    await run('unzip', ['-q', zip, 'agy_acp_server.par', 'localharness_external', '-d', downloadDir]);
    await copyFile(join(downloadDir, 'agy_acp_server.par'), agyOutput);
    await copyFile(join(downloadDir, 'localharness_external'), agyHarness);
  } finally {
    await rm(downloadDir, { recursive: true, force: true });
  }
  await writeFile(codexLauncher, launcher({
    cli: 'codex',
    displayName: 'Codex',
    environment: 'CODEX_PATH',
    runtime: 'codex-acp-runtime'
  }));
  await writeFile(claudeLauncher, launcher({
    cli: 'claude',
    displayName: 'Claude Code',
    environment: 'CLAUDE_CODE_EXECUTABLE',
    runtime: 'claude-agent-acp-runtime'
  }));
  await writeFile(agyLauncher, `#!/bin/sh
set -eu
adapter_dir=$(CDPATH= cd -- "$(dirname "$0")" && pwd -P)
exec "$adapter_dir/agy_acp_server.par" ${process.platform === 'linux' ? '--uid= ' : ''}"$@"
`);
  for (const path of [codexOutput, claudeOutput, agyOutput, agyHarness, codexLauncher, claudeLauncher, agyLauncher]) {
    await chmod(path, 0o755);
  }
  // Remove the superseded generated community binary so it cannot ship again.
  await rm(new URL('agy-acp-runtime', adapterDir), { force: true });

  const adapters = [
    {
      provider: 'codex',
      id: 'codex-acp',
      version: await packageVersion('@agentclientprotocol/codex-acp'),
      executable: 'codex-acp',
      files: [
        { path: 'codex-acp', sha256: await sha256(codexLauncher) },
        { path: 'codex-acp-runtime', sha256: await sha256(codexOutput) }
      ]
    },
    {
      provider: 'claude',
      id: 'claude-agent-acp',
      version: await packageVersion('@agentclientprotocol/claude-agent-acp'),
      executable: 'claude-agent-acp',
      files: [
        { path: 'claude-agent-acp', sha256: await sha256(claudeLauncher) },
        { path: 'claude-agent-acp-runtime', sha256: await sha256(claudeOutput) }
      ]
    },
    {
      provider: 'antigravity',
      id: 'agy-acp',
      version: official.version,
      executable: 'agy-acp',
      files: [
        { path: 'agy-acp', sha256: await sha256(agyLauncher) },
        { path: 'agy_acp_server.par', sha256: await sha256(agyOutput) },
        { path: 'localharness_external', sha256: await sha256(agyHarness) }
      ]
    }
  ];
  await writeFile(
    new URL('manifest.json', adapterDir),
    `${JSON.stringify({ schemaVersion: 1, adapters }, null, 2)}\n`
  );
  console.log(`Prepared ${adapters.length} release adapters in ${adapterDir.pathname}`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  await main();
}
