import { execFile } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';

const execFileAsync = promisify(execFile);
const remoteRoot = '$HOME/dev/work/assembly-backend-dev';
const targetRoot = '$HOME/.cache/assembly-backend-dev-target';
const binary = 'mac-command-bar-webview-preview';

type CommandResult = { stdout: string; stderr: string };
export type CommandRunner = (file: string, args: string[]) => Promise<CommandResult>;
type DeployOptions = {
  checkoutRoot?: string;
  local?: CommandRunner;
  rsync?: CommandRunner;
  ssh?: CommandRunner;
  log?: (message: string) => void;
};

// Build output can run past Node's 1 MB default and would kill the step.
const execRunner: CommandRunner = async (file, args) => {
  const { stdout, stderr } = await execFileAsync(file, args, { maxBuffer: 64 * 1024 * 1024 });
  return { stdout, stderr };
};

function validateHost(host: string): void {
  if (!/^[A-Za-z0-9](?:[A-Za-z0-9_.@-]*[A-Za-z0-9])?$/.test(host)) {
    throw new Error(`Invalid SSH host: ${host}`);
  }
}

function failureMessage(error: unknown): string {
  if (typeof error === 'object' && error !== null && 'stderr' in error && typeof error.stderr === 'string' && error.stderr) {
    return error.stderr;
  }
  return error instanceof Error ? error.message : String(error);
}

async function step(label: string, runner: CommandRunner, file: string, args: string[], log: (message: string) => void): Promise<void> {
  log(`${label}…`);
  const started = Date.now();
  try {
    await runner(file, args);
  } catch (error) {
    throw new Error(failureMessage(error));
  } finally {
    log(`${label}: ${((Date.now() - started) / 1000).toFixed(1)}s`);
  }
}

export async function deployRemoteBackendDev(host: string, options: DeployOptions = {}): Promise<void> {
  validateHost(host);
  const checkoutRoot = options.checkoutRoot ?? path.resolve(fileURLToPath(new URL('../..', import.meta.url)));
  const local = options.local ?? execRunner;
  const rsync = options.rsync ?? execRunner;
  const ssh = options.ssh ?? execRunner;
  const log = options.log ?? console.log;
  const metadata = JSON.parse((await local('cargo', ['metadata', '--no-deps', '--format-version=1',
    '--manifest-path', path.join(checkoutRoot, 'tauri-svelte-preview/src-tauri/Cargo.toml')])).stdout) as {
      packages?: Array<{ name?: string; version?: string }>;
    };
  const version = metadata.packages?.find((item) => item.name === binary)?.version;
  if (!version) throw new Error('Could not read the backend package version');
  const revision = (await local('git', ['-C', checkoutRoot, 'rev-parse', '--short=8', 'HEAD'])).stdout.trim();
  const dirty = (await local('git', ['-C', checkoutRoot, 'status', '--porcelain'])).stdout.trim().length > 0;
  const manifest = JSON.stringify({ schemaVersion: 1, packageType: 'assembly-remote-backend-dev',
    buildKind: 'development', version, commit: `${revision}${dirty ? '-dirty' : ''}`,
    target: 'x86_64-unknown-linux-gnu' });
  const sshArgs = (command: string) => ['--', host, command];
  const build = `set -eu; if test -f "$HOME/.cargo/env"; then . "$HOME/.cargo/env"; fi; cd "${remoteRoot}/tauri-svelte-preview"; pnpm install --frozen-lockfile; pnpm build; CARGO_TARGET_DIR="${targetRoot}" cargo build --locked --release --manifest-path src-tauri/Cargo.toml --bin ${binary}`;
  const install = `set -eu; src="${targetRoot}/release/${binary}"; dst="$HOME/.local/bin/assembly-remote-server"; install -d "$HOME/.local/bin"; if test -f "$dst"; then cp -p "$dst" "$dst.prev.new"; mv "$dst.prev.new" "$dst.prev"; fi; install -m 755 "$src" "$dst.new"; mv "$dst.new" "$dst"; systemctl --user restart assembly-remote.service; manifest_dir="$HOME/.local/share/assembly"; install -d "$manifest_dir"; printf '%s\\n' '${manifest}' > "$manifest_dir/backend-manifest.json.new"; mv "$manifest_dir/backend-manifest.json.new" "$manifest_dir/backend-manifest.json"`;
  const verify = `set -eu; trap 'test $? -eq 0 || systemctl --user --no-pager status assembly-remote.service >&2' EXIT; for _ in $(seq 30); do systemctl --user is-active --quiet assembly-remote.service && test -n "$(ss -ltnH 'sport = :7777')" && break; sleep 1; done; systemctl --user is-active --quiet assembly-remote.service; listeners=$(ss -ltnH 'sport = :7777'); test -n "$listeners"; printf '%s\\n' "$listeners" | awk '$4 != "127.0.0.1:7777" { exit 1 } END { if (NR == 0) exit 1 }'; built=$(sha256sum "${targetRoot}/release/${binary}" | awk '{print $1}'); installed=$(sha256sum "$HOME/.local/bin/assembly-remote-server" | awk '{print $1}'); test "$installed" = "$built"; printf '%s\\n' "$installed"`;

  log('Development build — not a signed release');
  await step('Sync sources', rsync, 'rsync', ['-rlpcz', '--delete',
    // Ignored files (build output, local secrets) stay on this Mac.
    '--filter=:- .gitignore', '--exclude=.env*', '--exclude=.git', '--exclude=target/',
    '--exclude=node_modules/', '--exclude=build/', '--exclude=remote-backend-release/',
    `${checkoutRoot}/`, `${host}:~/dev/work/assembly-backend-dev/`], log);
  await step('Build backend', ssh, 'ssh', sshArgs(build), log);
  await step('Install and restart', ssh, 'ssh', sshArgs(install), log);
  await step('Verify service', ssh, 'ssh', sshArgs(verify), log);
}

async function main(): Promise<void> {
  const host = process.argv[2];
  if (!host) throw new Error('usage: deployRemoteBackendDev.ts <ssh-host>');
  await deployRemoteBackendDev(host);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error: unknown) => {
    const message = failureMessage(error);
    process.stderr.write(message.endsWith('\n') ? message : `${message}\n`);
    process.exitCode = 1;
  });
}
