import assert from 'node:assert/strict';
import test from 'node:test';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { deployRemoteBackendDev, type CommandRunner } from './deployRemoteBackendDev.ts';

const ok = async () => ({ stdout: '', stderr: '' });
const quiet = () => {};
const cleanCheckout: CommandRunner = async (file, args) => {
  if (file === 'cargo') return { stdout: JSON.stringify({ packages: [{ name: 'mac-command-bar-webview-preview', version: '0.1.19' }] }), stderr: '' };
  if (args.includes('rev-parse')) return { stdout: '15add6b2\n', stderr: '' };
  return ok();
};

test('rejects an unsafe host before running a command', async () => {
  let calls = 0;
  const runner: CommandRunner = async () => { calls += 1; return ok(); };
  for (const host of ['host; reboot', 'host::module']) {
    await assert.rejects(deployRemoteBackendDev(host, {
      checkoutRoot: '/checkout', rsync: runner, ssh: runner, log: quiet
    }), /Invalid SSH host/);
  }
  assert.equal(calls, 0);
});

test('builds the exact rsync and ssh argument lists', async () => {
  const calls: Array<[string, string[]]> = [];
  const runner: CommandRunner = async (file, args) => {
    calls.push([file, args]);
    if (file === 'cargo') return { stdout: JSON.stringify({ packages: [{ name: 'mac-command-bar-webview-preview', version: '0.1.19' }] }), stderr: '' };
    if (args.includes('rev-parse')) return { stdout: '15add6b2\n', stderr: '' };
    if (args.includes('status')) return { stdout: ' M src-tauri/src/main.rs\n', stderr: '' };
    return ok();
  };
  await deployRemoteBackendDev('agent-workbox', {
    checkoutRoot: '/checkout', local: runner, rsync: runner, ssh: runner, log: quiet
  });
  assert.deepEqual(calls, [
    ['cargo', ['metadata', '--no-deps', '--format-version=1', '--manifest-path', '/checkout/tauri-svelte-preview/src-tauri/Cargo.toml']],
    ['git', ['-C', '/checkout', 'rev-parse', '--short=8', 'HEAD']],
    ['git', ['-C', '/checkout', 'status', '--porcelain']],
    ['rsync', ['-rlpcz', '--delete', '--filter=:- .gitignore', '--exclude=.env*', '--exclude=.git', '--exclude=target/', '--exclude=node_modules/',
      '--exclude=build/', '--exclude=remote-backend-release/', '/checkout/',
      'agent-workbox:~/dev/work/assembly-backend-dev/']],
    ['ssh', ['--', 'agent-workbox', 'set -eu; if test -f "$HOME/.cargo/env"; then . "$HOME/.cargo/env"; fi; cd "$HOME/dev/work/assembly-backend-dev/tauri-svelte-preview"; pnpm install --frozen-lockfile; pnpm build; CARGO_TARGET_DIR="$HOME/.cache/assembly-backend-dev-target" cargo build --locked --release --manifest-path src-tauri/Cargo.toml --bin mac-command-bar-webview-preview']],
    ['ssh', ['--', 'agent-workbox', 'set -eu; src="$HOME/.cache/assembly-backend-dev-target/release/mac-command-bar-webview-preview"; dst="$HOME/.local/bin/assembly-remote-server"; install -d "$HOME/.local/bin"; if test -f "$dst"; then cp -p "$dst" "$dst.prev.new"; mv "$dst.prev.new" "$dst.prev"; fi; install -m 755 "$src" "$dst.new"; mv "$dst.new" "$dst"; systemctl --user restart assembly-remote.service; manifest_dir="$HOME/.local/share/assembly"; install -d "$manifest_dir"; printf \'%s\\n\' \'{"schemaVersion":1,"packageType":"assembly-remote-backend-dev","buildKind":"development","version":"0.1.19","commit":"15add6b2-dirty","target":"x86_64-unknown-linux-gnu"}\' > "$manifest_dir/backend-manifest.json.new"; mv "$manifest_dir/backend-manifest.json.new" "$manifest_dir/backend-manifest.json"']],
    ['ssh', ['--', 'agent-workbox', 'set -eu; trap \'test $? -eq 0 || systemctl --user --no-pager status assembly-remote.service >&2\' EXIT; for _ in $(seq 30); do systemctl --user is-active --quiet assembly-remote.service && test -n "$(ss -ltnH \'sport = :7777\')" && break; sleep 1; done; systemctl --user is-active --quiet assembly-remote.service; listeners=$(ss -ltnH \'sport = :7777\'); test -n "$listeners"; printf \'%s\\n\' "$listeners" | awk \'$4 != "127.0.0.1:7777" { exit 1 } END { if (NR == 0) exit 1 }\'; built=$(sha256sum "$HOME/.cache/assembly-backend-dev-target/release/mac-command-bar-webview-preview" | awk \'{print $1}\'); installed=$(sha256sum "$HOME/.local/bin/assembly-remote-server" | awk \'{print $1}\'); test "$installed" = "$built"; printf \'%s\\n\' "$installed"']]
  ]);
});

test('reports the first failing step stderr and does not continue', async () => {
  let sshCalls = 0;
  const fail: CommandRunner = async () => { throw Object.assign(new Error('failed'), { stderr: 'rsync failed exactly\n' }); };
  const ssh: CommandRunner = async () => { sshCalls += 1; return ok(); };
  await assert.rejects(deployRemoteBackendDev('agent-workbox', {
    checkoutRoot: '/checkout', local: cleanCheckout, rsync: fail, ssh, log: quiet
  }), { message: 'rsync failed exactly\n' });
  assert.equal(sshCalls, 0);
});

test('does not install or restart when the build fails', async () => {
  let sshCalls = 0;
  const ssh: CommandRunner = async () => {
    sshCalls += 1;
    throw Object.assign(new Error('failed'), { stderr: 'cargo failed exactly\n' });
  };
  await assert.rejects(deployRemoteBackendDev('agent-workbox', {
    checkoutRoot: '/checkout', local: cleanCheckout, rsync: ok, ssh, log: quiet
  }), { message: 'cargo failed exactly\n' });
  assert.equal(sshCalls, 1);
});

test('syncs changed content without preserving source modification times', async () => {
  let flags = '';
  const rsync: CommandRunner = async (_file, args) => { flags = args[0]; return ok(); };
  await deployRemoteBackendDev('workbox-test', {
    checkoutRoot: '/checkout', local: cleanCheckout, rsync, ssh: ok, log: quiet
  });
  assert.ok(!flags.includes('a'), 'archive mode preserves modification times');
  assert.ok(!flags.includes('t'), 'modification times must not be preserved');
  assert.ok(flags.includes('c'), 'compare source content by checksum');
});

test('requires an explicit SSH host on the command line', () => {
  const result = spawnSync(process.execPath, [
    '--experimental-strip-types', fileURLToPath(new URL('./deployRemoteBackendDev.ts', import.meta.url))
  ], { encoding: 'utf8', env: { ...process.env, PATH: '' } });
  assert.equal(result.status, 1);
  assert.equal(result.stderr.trim(), 'usage: deployRemoteBackendDev.ts <ssh-host>');
  assert.equal(result.stdout, '');
});
