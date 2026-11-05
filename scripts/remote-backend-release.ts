// Builds the release package a production remote fetches straight from GitHub; the development package is
// remote-backend-package.ts. Input is Assembly.Remote published as one self-contained file. Output:
//   assembly-remote-backend-<version>-x86_64-unknown-linux-gnu.tar.gz and its .download.json record.
import { execFile } from 'node:child_process';
import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { copyFile, mkdir, mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';

const execFileAsync = promisify(execFile);
const repository = fileURLToPath(new URL('../', import.meta.url));

export const REMOTE_BACKEND_TARGET = 'x86_64-unknown-linux-gnu';
// Adapters come from their own GitHub release. The remote runs the one installer, at the paths it finds its sibling by.
const ADAPTER_TOOLS = ['dotnet/scripts/agents-file.sh', 'scripts/package-adapters.ts'] as const;

export type RemoteBackendManifest = {
  schemaVersion: 1;
  packageType: 'assembly-remote-backend';
  version: string;
  commit: string;
  target: typeof REMOTE_BACKEND_TARGET;
  files: Array<{ path: string; sha256: string; mode: '0755' | '0644' }>;
};

export type RemoteBackendPackageInput = {
  /** Scripts beyond the adapter tools, for a development package that installs its tools from them. */
  extraTools?: string[];
  binaryPath: string;
  outputDirectory: string;
  sourceDirectory: string;
  version: string;
  commit: string;
};

// $1 is the saved machine's port and $2 its token file under the remote's home: what a connection reads.
// $3 and $4 are the adapter release and the manifest hash the desktop verified against the pinned key.
// --wait-idle only waits for running sessions to end; after it, the final check before the stop exits 75 when one started
// anyway. --adapters installs adapters and restarts the same backend, for when only they are behind. --keep-adapters
// installs the backend and leaves adapters alone, for when the machine's are newer than the release's.
export const REMOTE_BACKEND_INSTALL_SCRIPT = `#!/bin/sh
set -eu

root=$HOME/assembly-host
PATH=$HOME/.local/bin:/usr/local/bin:/usr/bin:/bin
export PATH
mode=install
keep=

# Everything in the service's control group besides the host is a running session: an adapter, its CLI, a terminal.
running() {
  [ "$(systemctl --user is-active assembly-host.service || true)" = active ] || return 0
  main=$(systemctl --user show assembly-host.service -p MainPID --value)
  group=$(systemctl --user show assembly-host.service -p ControlGroup --value)
  if [ -z "$group" ] || [ ! -d "/sys/fs/cgroup$group" ]; then
    echo "Can't tell whether sessions are running on this machine, so nothing was stopped" >&2
    exit 1
  fi
  find "/sys/fs/cgroup$group" -name cgroup.procs -exec cat {} + | while read -r pid; do
    [ "$pid" = "$main" ] || [ ! -d "/proc/$pid" ] || echo "$pid"
  done
}

case "\${1:-}" in
  --wait-idle)
    said=
    while :; do
      pids=$(running)
      [ -n "$pids" ] || exit 0
      [ -n "$said" ] || { echo waiting; said=1; }
      # pidwait takes process exit events and reads the file's first ID; the next pass catches any others.
      file=$(mktemp ./.idle.XXXXXX)
      printf '%s\\n' "$pids" >"$file"
      pidwait -F "$file" || [ "$?" -eq 1 ]
      rm -f "$file"
    done ;;
esac
while :; do
  case "\${1:-}" in
    --adapters) mode=adapters; shift ;;
    --keep-adapters) keep=1; shift ;;
    *) break ;;
  esac
done

port=$1
case "$2" in /*) token_file=$2 ;; *) token_file=$HOME/$2 ;; esac

[ "$(uname -s)" = "Linux" ] || { echo "Assembly remote backend requires Linux" >&2; exit 1; }
[ "$(uname -m)" = "x86_64" ] || { echo "Unsupported Linux architecture: $(uname -m)" >&2; exit 1; }
for command in sha256sum systemctl install ss awk bash curl tar python3 node pidwait; do
  command -v "$command" >/dev/null 2>&1 || { echo "Required command is missing: $command" >&2; exit 1; }
done

# Directly before anything stops: a session that started during the steps above sends the install back to waiting.
refuse_if_running() {
  pids=$(running)
  if [ -n "$pids" ]; then
    echo "A session started on this machine, so nothing was stopped" >&2
    exit 75
  fi
}

sha256sum -c SHA256SUMS
install -d "$root/bin" "$root/serve" "$HOME/.config/systemd/user"
# The host makes the token itself on its first start, in the file the saved machine names.
(umask 077; mkdir -p "$(dirname "$token_file")")
[ -n "$keep" ] || ASSEMBLY_ADAPTER_TAG=$3 ASSEMBLY_ADAPTER_MANIFEST_SHA256=$4 bash payload/dotnet/scripts/agents-file.sh "$root/adapters" "$root/serve/agents.json"
refuse_if_running
if [ "$mode" = install ]; then
systemctl --user stop assembly-host.service >/dev/null 2>&1 || true
install -m 755 payload/assembly-remote-server "$root/bin/.assembly-remote-server.new"
mv "$root/bin/.assembly-remote-server.new" "$root/bin/assembly-remote-server"

cat >"$HOME/.config/systemd/user/.assembly-host.service.new" <<EOF
[Unit]
Description=Assembly Remote Service
After=network.target

[Service]
Type=simple
Environment=PATH=$PATH
EnvironmentFile=-$HOME/.config/assembly-host/phone.env
UMask=0077
WorkingDirectory=$root/serve
ExecStart=$root/bin/assembly-remote-server serve --port $port --home $root/serve --agents $root/serve/agents.json --token-file $token_file
Restart=on-failure
RestartSec=2

[Install]
WantedBy=default.target
EOF
mv "$HOME/.config/systemd/user/.assembly-host.service.new" "$HOME/.config/systemd/user/assembly-host.service"
systemctl --user daemon-reload
systemctl --user enable assembly-host.service
fi
systemctl --user restart assembly-host.service

# The first start after an update can migrate stored data, so wait while the service is still
# starting (systemd reports a restart in progress as activating), and stop at once if it exits.
waited=0
while :; do
  state=$(systemctl --user is-active assembly-host.service || true)
  case "$state" in
    active|activating) ;;
    *)
      systemctl --user status assembly-host.service --no-pager --lines=20 >&2 || true
      echo "Assembly remote backend stopped after $waited seconds while starting" >&2
      exit 1 ;;
  esac
  listeners=$(ss -ltnH "sport = :$port")
  if [ "$state" = active ] \\
    && [ -n "$listeners" ] \\
    && printf '%s\\n' "$listeners" | awk -v want="127.0.0.1:$port" '$4 != want { exit 1 } END { if (NR == 0) exit 1 }'; then
    if [ "$mode" = install ]; then
      install -m 644 manifest.json "$root/serve/.backend-manifest.new"
      mv "$root/serve/.backend-manifest.new" "$root/serve/backend-manifest.json"
    fi
    exit 0
  fi
  [ "$waited" -lt 120 ] || break
  waited=$((waited + 1))
  sleep 1
done
systemctl --user status assembly-host.service --no-pager --lines=20 >&2 || true
echo "Assembly remote backend did not start listening on 127.0.0.1:$port within $waited seconds" >&2
exit 1
`;

async function sha256(filePath: string): Promise<string> {
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(filePath)) hash.update(chunk);
  return hash.digest('hex');
}

async function protocolVersion(source: string): Promise<number> {
  const text = await readFile(path.join(source, 'dotnet/Assembly.Infrastructure/Socket/SocketConnection.cs'), 'utf8');
  const match = [...text.matchAll(/\bconst int Protocol = (\d+);/g)];
  if (match.length !== 1) throw new Error('Remote protocol version source is missing or ambiguous');
  return Number(match[0][1]);
}

function validatePackageInput(input: RemoteBackendPackageInput): void {
  if (!/^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$/.test(input.version)) {
    throw new Error(`Invalid backend version: ${input.version}`);
  }
  if (!/^[0-9a-f]{7,40}$/.test(input.commit)) {
    throw new Error(`Invalid Git commit: ${input.commit}`);
  }
}

export async function writeRemoteBackendPackage(input: RemoteBackendPackageInput): Promise<string> {
  validatePackageInput(input);
  const staging = await mkdtemp(path.join(tmpdir(), 'assembly-remote-package-'));
  const archiveName = `assembly-remote-backend-${input.version}-${REMOTE_BACKEND_TARGET}.tar.gz`;
  const archivePath = path.join(input.outputDirectory, archiveName);
  try {
    await mkdir(path.join(staging, 'payload'), { recursive: true });
    await mkdir(input.outputDirectory, { recursive: true });
    await copyFile(input.binaryPath, path.join(staging, 'payload/assembly-remote-server'));
    await writeFile(path.join(staging, 'install.sh'), REMOTE_BACKEND_INSTALL_SCRIPT, { mode: 0o755 });
    const placed: Array<[string, '0755' | '0644']> = [['payload/assembly-remote-server', '0755'], ['install.sh', '0755']];
    for (const tool of new Set([...ADAPTER_TOOLS, ...(input.extraTools ?? [])])) {
      const destination = path.join(staging, 'payload', tool);
      await mkdir(path.dirname(destination), { recursive: true });
      await copyFile(path.join(input.sourceDirectory, tool), destination);
      placed.push([`payload/${tool}`, tool.endsWith('.sh') ? '0755' : '0644']);
    }
    const files: RemoteBackendManifest['files'] = [];
    for (const [file, mode] of placed) files.push({ path: file, sha256: await sha256(path.join(staging, file)), mode });
    const manifest: RemoteBackendManifest = {
      schemaVersion: 1,
      packageType: 'assembly-remote-backend',
      version: input.version,
      commit: input.commit,
      target: REMOTE_BACKEND_TARGET,
      files
    };
    await writeFile(path.join(staging, 'manifest.json'), `${JSON.stringify(manifest, null, 2)}\n`);
    await writeFile(
      path.join(staging, 'SHA256SUMS'),
      `${files.map((file) => `${file.sha256}  ${file.path}`).join('\n')}\n`
    );
    await execFileAsync('tar', ['-czf', archivePath, '-C', staging, '.'], {
      env: { ...process.env, COPYFILE_DISABLE: '1' }
    });
    await writeFile(`${archivePath}.download.json`, `${JSON.stringify({
      schemaVersion: 1,
      version: input.version,
      protocolVersion: await protocolVersion(input.sourceDirectory),
      target: REMOTE_BACKEND_TARGET,
      archiveFile: archiveName,
      bytes: (await stat(archivePath)).size,
      sha256: await sha256(archivePath)
    })}\n`);
    return archivePath;
  } finally {
    await rm(staging, { recursive: true, force: true });
  }
}

async function main(): Promise<void> {
  if (process.platform !== 'linux' || process.arch !== 'x64') {
    throw new Error('Remote backend release packages must be built on Linux x86_64');
  }
  const [version, binaryPath, outputDirectory] = process.argv.slice(2);
  if (!version || !binaryPath || !outputDirectory) throw new Error('Usage: remote-backend-release.ts <version> <binary> <output>');
  const { stdout } = await execFileAsync('git', ['rev-parse', 'HEAD'], { cwd: repository });
  const archivePath = await writeRemoteBackendPackage({
    binaryPath,
    outputDirectory,
    sourceDirectory: repository,
    version,
    commit: stdout.trim()
  });
  console.log(`Prepared ${archivePath}`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  await main();
}
