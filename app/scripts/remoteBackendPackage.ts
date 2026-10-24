import { execFile } from 'node:child_process';
import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { copyFile, mkdir, mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';

const execFileAsync = promisify(execFile);

export const REMOTE_BACKEND_TARGET = 'x86_64-unknown-linux-gnu';
export const REMOTE_BACKEND_PAYLOAD_FILES = [
  'payload/assembly-remote-server',
  'install.sh'
] as const;

export type RemoteBackendManifest = {
  schemaVersion: 1;
  packageType: 'assembly-remote-backend';
  version: string;
  commit: string;
  target: typeof REMOTE_BACKEND_TARGET;
  files: Array<{ path: string; sha256: string; mode: '0755' | '0644' }>;
};

export type RemoteBackendPackageInput = {
  binaryPath: string;
  outputDirectory: string;
  version: string;
  commit: string;
};

export const REMOTE_BACKEND_INSTALL_SCRIPT = `#!/bin/sh
set -eu

[ "$(uname -s)" = "Linux" ] || { echo "Assembly remote backend requires Linux" >&2; exit 1; }
[ "$(uname -m)" = "x86_64" ] || { echo "Unsupported Linux architecture: $(uname -m)" >&2; exit 1; }
for command in sha256sum systemctl openssl install ss awk; do
  command -v "$command" >/dev/null 2>&1 || { echo "Required command is missing: $command" >&2; exit 1; }
done

sha256sum -c SHA256SUMS
install -d "$HOME/.local/bin" "$HOME/.local/share/assembly" "$HOME/.config/assembly" "$HOME/.config/systemd/user"
systemctl --user stop assembly-remote.service >/dev/null 2>&1 || true
install -m 755 payload/assembly-remote-server "$HOME/.local/bin/.assembly-remote-server.new"
mv "$HOME/.local/bin/.assembly-remote-server.new" "$HOME/.local/bin/assembly-remote-server"
rm -rf "$HOME/.local/bin/assembly-adapters"

token=$(sed -n 's/^ASSEMBLY_SERVER_TOKEN=//p' "$HOME/.config/assembly/server.env" 2>/dev/null || true)
[ "\${#token}" -ge 32 ] || token=$(openssl rand -hex 32)
# Created readable only by this user, so the token is never readable by others, even mid-install.
(umask 077 && rm -f "$HOME/.config/assembly/.server.env.new" && cat >"$HOME/.config/assembly/.server.env.new" <<EOF
ASSEMBLY_SERVER_BIND=127.0.0.1:7777
ASSEMBLY_SERVER_TOKEN=$token
ASSEMBLY_SERVER_DATA_DIR=$HOME/.local/share/assembly
PATH=$HOME/.local/bin:/usr/local/bin:/usr/bin:/bin
EOF
)
mv "$HOME/.config/assembly/.server.env.new" "$HOME/.config/assembly/server.env"

cat >"$HOME/.config/systemd/user/.assembly-remote.service.new" <<EOF
[Unit]
Description=Assembly Remote Service
After=network.target

[Service]
Type=simple
EnvironmentFile=$HOME/.config/assembly/server.env
EnvironmentFile=-$HOME/.config/assembly/phone.env
ExecStart=$HOME/.local/bin/assembly-remote-server --assembly-server
Restart=on-failure
RestartSec=2

[Install]
WantedBy=default.target
EOF
mv "$HOME/.config/systemd/user/.assembly-remote.service.new" "$HOME/.config/systemd/user/assembly-remote.service"
systemctl --user daemon-reload
systemctl --user enable assembly-remote.service
systemctl --user restart assembly-remote.service

# The first start after an update can migrate stored data, so wait while the service is still
# starting (systemd reports a restart in progress as activating), and stop at once if it exits.
waited=0
while :; do
  state=$(systemctl --user is-active assembly-remote.service || true)
  case "$state" in
    active|activating) ;;
    *)
      systemctl --user status assembly-remote.service --no-pager --lines=20 >&2 || true
      echo "Assembly remote backend stopped after $waited seconds while starting" >&2
      exit 1 ;;
  esac
  listeners=$(ss -ltnH 'sport = :7777')
  if [ "$state" = active ] \
    && [ -n "$listeners" ] \
    && printf '%s\n' "$listeners" | awk '$4 != "127.0.0.1:7777" { exit 1 } END { if (NR == 0) exit 1 }'; then
    install -m 644 manifest.json "$HOME/.local/share/assembly/.backend-manifest.new"
    mv "$HOME/.local/share/assembly/.backend-manifest.new" "$HOME/.local/share/assembly/backend-manifest.json"
    exit 0
  fi
  [ "$waited" -lt 120 ] || break
  waited=$((waited + 1))
  sleep 1
done
systemctl --user status assembly-remote.service --no-pager --lines=20 >&2 || true
echo "Assembly remote backend did not start listening on 127.0.0.1:7777 within $waited seconds" >&2
exit 1
`;

async function sha256(filePath: string): Promise<string> {
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(filePath)) hash.update(chunk);
  return hash.digest('hex');
}

async function protocolVersion(): Promise<number> {
  const source = await readFile(fileURLToPath(new URL('../src-tauri/src/agent_conversation/remote/mod.rs', import.meta.url)), 'utf8');
  const match = source.match(/const PROTOCOL_VERSION: u16 = (\d+);/g);
  if (!match || match.length !== 1) throw new Error('Remote protocol version source is missing or ambiguous');
  return Number(match[0].match(/\d+(?=;)/)?.[0]);
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
  const payload = path.join(staging, 'payload');
  const archiveName = `assembly-remote-backend-${input.version}-${REMOTE_BACKEND_TARGET}.tar.gz`;
  const archivePath = path.join(input.outputDirectory, archiveName);
  try {
    await mkdir(payload, { recursive: true });
    await mkdir(input.outputDirectory, { recursive: true });
    const binary = path.join(payload, 'assembly-remote-server');
    await copyFile(input.binaryPath, binary);
    const installScript = path.join(staging, 'install.sh');
    await writeFile(installScript, REMOTE_BACKEND_INSTALL_SCRIPT, { mode: 0o755 });
    const files: RemoteBackendManifest['files'] = [
      { path: 'payload/assembly-remote-server', sha256: await sha256(binary), mode: '0755' },
      { path: 'install.sh', sha256: await sha256(installScript), mode: '0755' }
    ];
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
      protocolVersion: await protocolVersion(),
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
  const version = process.argv[2];
  if (!version) throw new Error('Remote backend version argument is required');
  const projectRoot = fileURLToPath(new URL('../', import.meta.url));
  const { stdout: commitOutput } = await execFileAsync('git', ['rev-parse', 'HEAD'], { cwd: projectRoot });
  const archivePath = await writeRemoteBackendPackage({
    binaryPath: path.join(projectRoot, 'src-tauri/target/release/mac-command-bar-webview-preview'),
    outputDirectory: path.join(projectRoot, 'remote-backend-release'),
    version,
    commit: commitOutput.trim()
  });
  console.log(`Prepared ${archivePath}`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  await main();
}
