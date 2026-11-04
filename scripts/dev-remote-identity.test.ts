import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { adapterInput, adapterSources, backendIdentity, backendInput, sourcesDigest, remoteContract, remoteContractInput, workingBackendIdentity, workingRemoteContract } from './dev-remote-identity.ts';

for (const [file, onList] of [
  ['dotnet/Assembly.Infrastructure/Socket/SocketFrames.cs', true],
  ['dotnet/Assembly.Application/Features/Chat/Rows/RowStore.cs', true],
  ['dotnet/Assembly.Engine/Persistence/EngineSchema.cs', true],
  ['dotnet/Assembly.Engine/EngineCommands.cs', true],
  ['dotnet/Assembly.Infrastructure/Chat/Files/FileOwner.cs', true],
  ['dotnet/Assembly.Infrastructure/Git/GitOperations.cs', true],
  ['dotnet/Assembly.Infrastructure/Terminal/UnixPty.cs', true],
  ['dotnet/Assembly.Application/Common/Acp/AcpProtocol.cs', true],
  ['dotnet/Assembly.Infrastructure/Updates/RemoteCliCapture.cs', true],
  ['dotnet/Assembly.Remote/Serve/ServeRun.cs', true],
  ['dotnet/Directory.Packages.props', true],
  ['dotnet/Assembly.Engine/packages.lock.json', true],
  ['scripts/dev-remote-build-profile.ts', true],
  ['dotnet/Assembly.Desktop/Serve/DesktopBackend.cs', false],
  ['dotnet/Assembly.Tests/Socket/SocketTests.cs', false],
  ['dotnet/Assembly.Infrastructure/Remote/RemoteMachine.cs', false],
  ['dotnet/Assembly.Infrastructure/Remote/Install/RemoteInstallJobs.cs', false],
  ['dotnet/Assembly.Infrastructure/Notion/NotionCalls.cs', false],
  ['dotnet/Assembly.Application/Features/Notion/NotionCache.cs', false],
  ['dotnet/Assembly.Application/Features/Workspaces/WorkspaceStore.cs', false],
  ['dotnet/Assembly.Infrastructure/Clock/BackendClock.cs', false],
  ['dotnet/Assembly.Infrastructure/Updates/UpdateCalls.cs', false],
  ['dotnet/Assembly.Infrastructure/Workflows/RemoteWorkflowMirror.cs', false],
  ['dotnet/Assembly.Engine/README.md', false],
  ['dotnet/notes/reconnect-later-remote.md', false],
  ['scripts/adapters/claude-launcher.ts', false],
  ['scripts/package-adapters.ts', false],
  ['dotnet/scripts/agents-file.sh', false],
  ['scripts/dev-toolchain.ts', false],
  ['scripts/workbox-serve.ts', false],
  ['scripts/dev-remote-identity.ts', false],
  ['desktop/src/main.rs', false],
  ['desktop/Cargo.toml', false],
] as const) test(`${file} is ${onList ? 'on' : 'off'} the list`, () => assert.equal(remoteContractInput(file), onList));

test('everything on the list is also a build input, so an exact match implies a list match', () => {
  for (const file of ['dotnet/Assembly.Engine/EngineCommands.cs', 'dotnet/global.json'])
    assert.ok(remoteContractInput(file) && backendInput(file));
});

test('adapters, launchers and their scripts have their own check, and an exact match still covers them', async () => {
  for (const file of ['scripts/adapters/claude-launcher.ts', 'scripts/package-adapters.ts', 'dotnet/scripts/agents-file.sh', 'scripts/dev-toolchain.ts'])
    assert.ok(adapterInput(file) && backendInput(file));
  const digest = (launcher: string) => sourcesDigest(adapterSources, async file => file.endsWith('claude-launcher.ts') ? launcher : 'same');
  assert.equal(await digest('a'), await digest('a'));
  assert.notEqual(await digest('a'), await digest('b'));
  assert.notEqual(await sourcesDigest(adapterSources, async () => undefined), await digest('a'));
});

test('the list identity ignores what is off the list, the release version moves both, and uncommitted edits count', async () => {
  const root = mkdtempSync(join(tmpdir(), 'backend-contract-'));
  const git = (...args: string[]) => execFileSync('git', args, { cwd: root, encoding: 'utf8' }).trim();
  const commit = () => { git('add', '.'); git('-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'fixture'); return git('rev-parse', 'HEAD'); };
  try {
    git('init', '-q');
    mkdirSync(join(root, 'dotnet/Assembly.Remote'), { recursive: true });
    mkdirSync(join(root, 'dotnet/Assembly.Infrastructure/Notion'), { recursive: true });
    mkdirSync(join(root, 'desktop'));
    writeFileSync(join(root, 'dotnet/Assembly.Remote/Assembly.Remote.csproj'), 'backend');
    writeFileSync(join(root, 'dotnet/Assembly.Infrastructure/Notion/NotionCalls.cs'), 'first');
    writeFileSync(join(root, 'desktop/Cargo.toml'), '[package]\nname = "x"\nversion = "0.1.0"\n[dependencies]\nversion = "9"\n');
    const first = commit();
    const contract = await remoteContract(root, first), exact = await backendIdentity(root, first);
    assert.equal(await workingRemoteContract(root, first), contract);
    writeFileSync(join(root, 'dotnet/Assembly.Infrastructure/Notion/NotionCalls.cs'), 'second');
    const unrelated = commit();
    assert.equal(await remoteContract(root, unrelated), contract);
    assert.notEqual(await backendIdentity(root, unrelated), exact);
    writeFileSync(join(root, 'desktop/Cargo.toml'), '[package]\nname = "changed"\nversion = "0.1.0"\n');
    const manifest = commit();
    assert.equal(await remoteContract(root, manifest), contract);
    writeFileSync(join(root, 'desktop/Cargo.toml'), '[package]\nname = "changed"\nversion = "0.2.0"\n');
    const release = commit();
    assert.notEqual(await remoteContract(root, release), contract);
    assert.equal(await workingRemoteContract(root, release), await remoteContract(root, release));
    writeFileSync(join(root, 'dotnet/Assembly.Remote/new.cs'), 'pending');
    assert.notEqual(await workingRemoteContract(root, release), await remoteContract(root, release));
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('build identity ignores Rust-only revisions and detects uncommitted backend inputs', async () => {
  const root = mkdtempSync(join(tmpdir(), 'backend-identity-'));
  const git = (...args: string[]) => execFileSync('git', args, { cwd: root, encoding: 'utf8' }).trim();
  const commit = () => { git('add', '.'); git('-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'fixture'); return git('rev-parse', 'HEAD'); };
  try {
    git('init', '-q');
    mkdirSync(join(root, 'dotnet/Assembly.Remote'), { recursive: true });
    mkdirSync(join(root, 'desktop'));
    const input = join(root, 'dotnet/Assembly.Remote/Assembly.Remote.csproj');
    writeFileSync(input, 'backend');
    const first = commit();
    writeFileSync(join(root, 'desktop/native.rs'), 'rust only');
    const second = commit();
    const identity = await backendIdentity(root, first);
    assert.equal(await backendIdentity(root, second), identity);
    assert.equal(await workingBackendIdentity(root, second), identity);
    writeFileSync(input, 'uncommitted backend');
    assert.notEqual(await workingBackendIdentity(root, second), identity);
    const third = commit();
    assert.equal(await workingBackendIdentity(root, third), await backendIdentity(root, third));
    assert.notEqual(await backendIdentity(root, third), identity);
  } finally { rmSync(root, { recursive: true, force: true }); }
});
