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

// $1 is the saved machine's port. $2 and $3 are the adapter release and the manifest hash the desktop verified against the
// pinned key. The token file isn't an argument: this install has one, so it can't be pointed at another install's.
// --wait-idle only waits for running sessions to end; after it, the final check before the stop exits 75 when one started
// anyway. --adapters installs adapters and restarts the same backend, for when only they are behind, on the port its unit
// already has. --keep-adapters installs the backend and leaves adapters alone, for when the machine's are newer than the
// release's. A backend older than the one installed is refused, and so is anything once a session starts, with nothing
// changed. From the stop until the new backend is listening, everything it replaces is kept, and any failure puts it back and
// starts it again. Nothing is replaced until all of it is kept, and an install stopped too hard to undo itself is undone by the next.
export const REMOTE_BACKEND_INSTALL_SCRIPT = `#!/bin/sh
set -eu

root=$HOME/assembly-host
unit=$HOME/.config/systemd/user/assembly-host.service
before=$root/before-update
token_file=$HOME/.config/assembly-host/env
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

# One name at a time, so what's there is always whole. Only a kept file is ever put back.
keep() {
  [ ! -e "$1" ] || { cp -p --reflink=auto "$1" "$before/.$2.partial" && mv -f "$before/.$2.partial" "$before/$2"; }
}
put_back() {
  [ ! -e "$before/$1" ] || { cp -p --reflink=auto "$before/$1" "$2.restoring" && mv -f "$2.restoring" "$2"; }
}
# Can stop at any point and run again: nothing kept goes until the mark does.
restore() {
  put_back agents.json "$root/serve/agents.json" &&
    put_back assembly-remote-server "$root/bin/assembly-remote-server" &&
    put_back assembly-host.service "$unit" &&
    put_back backend-manifest.json "$root/serve/backend-manifest.json" || return 1
  if [ -e "$before/assembly.db" ]; then
    rm -f "$root/serve/assembly.db" "$root/serve/assembly.db-wal" "$root/serve/assembly.db-shm" &&
      put_back assembly.db-wal "$root/serve/assembly.db-wal" &&
      put_back assembly.db "$root/serve/assembly.db" || return 1
  fi
  rm -f "$before/complete"
}

refuse_if_running() {
  pids=$(running)
  if [ -n "$pids" ]; then
    echo "A session started on this machine, so nothing was stopped" >&2
    [ -e "$before/complete" ] || rm -rf "$before"
    exit 75
  fi
}

# An earlier run was killed with things replaced. It comes first: everything below reads what's installed.
if [ -e "$before/complete" ]; then
  # The backend it left may have come up and been connected to. A session in it isn't ended for this; the mark waits.
  refuse_if_running
  echo "An earlier install stopped partway, so what it replaced is being put back" >&2
  systemctl --user stop assembly-host.service >/dev/null 2>&1 || true
  restore || { echo "What an earlier install replaced couldn't be put back, and it's kept in $before" >&2; exit 1; }
  if [ -e "$before/first-install" ]; then
    systemctl --user disable assembly-host.service >/dev/null 2>&1 || true
    rm -f "$unit" "$root/bin/assembly-remote-server"
    rm -rf "$before"
    systemctl --user daemon-reload
  else
    rm -rf "$before"
    systemctl --user daemon-reload
    systemctl --user restart assembly-host.service
  fi
fi

# Decided here, from the real binary: a signal can arrive before anything has been kept.
had_server=
[ ! -e "$root/bin/assembly-remote-server" ] || had_server=1

port=$1
if [ "$mode" = adapters ]; then
  # The host keeps running on what its unit says, whatever the saved machine says now.
  port=$(sed -n 's/^ExecStart=.* --port \\([0-9][0-9]*\\).*/\\1/p' "$unit")
  [ -n "$port" ] || { echo "The installed service names no port, so nothing was changed" >&2; exit 1; }
fi

[ "$(uname -s)" = "Linux" ] || { echo "Assembly remote backend requires Linux" >&2; exit 1; }
[ "$(uname -m)" = "x86_64" ] || { echo "Unsupported Linux architecture: $(uname -m)" >&2; exit 1; }
for command in sha256sum systemctl install ss awk bash curl tar python3 node pidwait sed sort; do
  command -v "$command" >/dev/null 2>&1 || { echo "Required command is missing: $command" >&2; exit 1; }
done

# Directly before anything changes: a session that started during the steps above sends the install back to waiting.
version_of() { sed -n 's/^ *"version": *"\\([^"]*\\)".*/\\1/p' "$1" 2>/dev/null | head -n 1; }

sha256sum -c SHA256SUMS
if [ "$mode" = install ]; then
  installed=$(version_of "$root/serve/backend-manifest.json")
  offered=$(version_of manifest.json)
  if [ -n "$installed" ] && [ "$installed" != "$offered" ] && [ "$(printf '%s\\n%s\\n' "$installed" "$offered" | sort -V | tail -n 1)" = "$installed" ]; then
    echo "This machine's backend is $installed, newer than $offered, so nothing was changed" >&2
    exit 1
  fi
fi
install -d "$root/bin" "$root/serve" "$HOME/.config/systemd/user"
# The host makes the token itself on its first start.
(umask 077; mkdir -p "$(dirname "$token_file")")
refuse_if_running
rm -rf "$before"
mkdir -p "$before"
new_agents=
if [ -z "$keep" ]; then
  # Written aside from a copy, which keeps its settings, and put in place only past the last check. Bundles are never changed
  # in place, and the one in use stays until the install after this, so a session in one is unaffected.
  [ ! -e "$root/serve/agents.json" ] || cp -p "$root/serve/agents.json" "$before/agents.new.json"
  ASSEMBLY_ADAPTER_TAG=$2 ASSEMBLY_ADAPTER_MANIFEST_SHA256=$3 bash payload/dotnet/scripts/agents-file.sh "$root/adapters" "$before/agents.new.json"
  new_agents=1
  refuse_if_running
fi

listening() {
  listeners=$(ss -ltnH "sport = :$1")
  [ -n "$listeners" ] && printf '%s\\n' "$listeners" | awk -v want="127.0.0.1:$1" '$4 != want { exit 1 } END { if (NR == 0) exit 1 }'
}

# Any way out from here but success. Another signal can't stop it; anything harder leaves the mark for the next run.
undo() {
  code=$?
  trap - EXIT
  trap '' HUP INT TERM
  set +e
  systemctl --user stop assembly-host.service >/dev/null 2>&1
  if [ -e "$before/complete" ] && ! restore; then
    echo "What this install replaced couldn't be put back, and it's kept in $before; the next install tries again" >&2
    exit "$code"
  fi
  if [ "$mode" = install ] && [ -z "$had_server" ]; then
    systemctl --user disable assembly-host.service >/dev/null 2>&1
    rm -f "$unit" "$root/bin/assembly-remote-server"
    systemctl --user daemon-reload
    rm -rf "$before"
    echo "The install failed and there was no earlier one to go back to, so the service was removed" >&2
    exit "$code"
  fi
  # Nothing can have connected to the new backend yet, so all it can have written to the database is its own migration.
  rm -rf "$before"
  systemctl --user daemon-reload
  systemctl --user enable assembly-host.service >/dev/null 2>&1
  systemctl --user restart assembly-host.service
  old_port=$(sed -n 's/^ExecStart=.* --port \\([0-9][0-9]*\\).*/\\1/p' "$unit")
  waited=0
  while [ "$waited" -lt 120 ]; do
    if [ "$(systemctl --user is-active assembly-host.service)" = active ] && listening "$old_port"; then
      echo "The previous backend is running again on port $old_port" >&2
      exit "$code"
    fi
    waited=$((waited + 1))
    sleep 1
  done
  echo "The previous backend is back in place but did not start listening on port $old_port" >&2
  exit "$code"
}
trap undo EXIT
trap 'exit 1' HUP INT TERM

if [ "$mode" = install ]; then
systemctl --user stop assembly-host.service >/dev/null 2>&1 || true
keep "$root/bin/assembly-remote-server" assembly-remote-server
keep "$unit" assembly-host.service
keep "$root/serve/backend-manifest.json" backend-manifest.json
# Stopped, so these files are the whole database. The first start of a new version can migrate it. The -shm file is only an
# index of the log, and SQLite rebuilds it.
keep "$root/serve/assembly.db" assembly.db
keep "$root/serve/assembly.db-wal" assembly.db-wal
fi
[ -z "$new_agents" ] || keep "$root/serve/agents.json" agents.json
[ "$mode" != install ] || [ -n "$had_server" ] || : >"$before/first-install"
: >"$before/complete"
[ -z "$new_agents" ] || mv -f "$before/agents.new.json" "$root/serve/agents.json"
if [ "$mode" = install ]; then
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
mv "$HOME/.config/systemd/user/.assembly-host.service.new" "$unit"
systemctl --user daemon-reload
systemctl --user enable assembly-host.service
fi
systemctl --user restart assembly-host.service

# The first start after an update can migrate stored data, so wait while the service is still
# starting (systemd reports a restart in progress as activating), and stop at once if it exits
# or keeps exiting: a backend that dies on start would otherwise be restarted for the whole wait.
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
  restarts=$(systemctl --user show assembly-host.service -p NRestarts --value || true)
  if [ "\${restarts:-0}" -ge 3 ]; then
    systemctl --user status assembly-host.service --no-pager --lines=20 >&2 || true
    echo "Assembly remote backend kept exiting while starting ($restarts restarts in $waited seconds)" >&2
    exit 1
  fi
  if [ "$state" = active ] && listening "$port"; then
    if [ "$mode" = install ]; then
      install -m 644 manifest.json "$root/serve/.backend-manifest.new"
      mv "$root/serve/.backend-manifest.new" "$root/serve/backend-manifest.json"
    fi
    trap - EXIT HUP INT TERM
    rm -f "$before/complete"
    rm -rf "$before"
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
