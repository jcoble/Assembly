import assert from 'node:assert/strict';
import { test } from 'node:test';
import { lane } from './dev-remote-identity.ts';
import { cleanLane, hostResources, type LaneOwner } from './dev-lane-clean.ts';
import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdtempSync, mkdirSync, writeFileSync, existsSync, rmSync, realpathSync } from 'node:fs';
import { tmpdir, hostname } from 'node:os';
import { join, dirname } from 'node:path';
import { profileFor } from './seed-dev.ts';

test('lane lifecycle refuses main and protected names before any host operation', () => {
  for (const name of ['', 'main', 'base', 'rail', 'remote', 'assembly-dotnet', 'assembly-dotnet-base', 'assembly-dotnet-rail', 'assembly-remote'])
    assert.throws(() => lane(name, 1), /refuses|protected/);
  assert.throws(() => lane('example', 0));
  assert.equal(lane('example', 2).data, 'assembly-dotnet-example');
});

test('one lane cleanup removes only its owned unit, data, token, profile and outputs; busy and unmarked lanes stay intact', async () => {
  const fixture = mkdtempSync(join(realpathSync(tmpdir()), 'lane-clean-'));
  const home = join(fixture, 'local'), remoteHome = join(fixture, 'remote'), bin = join(fixture, 'bin');
  const root = join(home, 'dev/work/worktrees/Assembly/example');
  const write = (file: string, text: string, mode = 0o600) => { mkdirSync(dirname(file), { recursive: true }); writeFileSync(file, text, { mode }); };
  const priorPath = process.env.PATH, priorRemote = process.env.CLEAN_TEST_REMOTE, priorBusy = process.env.CLEAN_TEST_BUSY;
  const owner: LaneOwner = { version: 1, name: 'example', number: 2, owner: randomUUID(), host: hostname(), worktree: root };
  try {
    write(join(root, 'desktop/Cargo.toml'), '');
    write(join(root, 'dotnet/Assembly.Desktop/Assembly.Desktop.csproj'), '<Project/>');
    write(join(root, '.lane'), `LANE_NAME=example\nLANE_NUMBER=2\nLANE_OWNER=${owner.owner}\n`);
    execFileSync('git', ['init', '-q'], { cwd: root });
    write(join(home, 'assembly-dev/lanes/example.json'), JSON.stringify(owner));
    const profile = profileFor(root, home).home;
    write(join(profile, 'assembly.db'), 'owned profile');
    write(join(home, 'assembly-dotnet-example/mac/old-data'), 'owned local data');
    write(join(root, 'desktop/target/debug/assembly-desktop'), 'owned build');
    write(join(root, 'dotnet/Assembly.Desktop/obj/generated'), 'owned build');
    const remoteRoot = join(remoteHome, 'assembly-dotnet-example');
    write(join(remoteRoot, 'lane-owner.json'), JSON.stringify(owner));
    write(join(remoteRoot, 'lane-number'), '2');
    write(join(remoteRoot, 'bin/old-release/backend.dll'), 'owned old release');
    const unit = join(remoteHome, '.config/systemd/user/assembly-dotnet-example.service');
    write(unit, 'owned unit');
    write(join(remoteHome, '.config/assembly-dotnet-example/env'), 'test token');
    const box = join(remoteHome, 'assembly.git');
    mkdirSync(remoteHome, { recursive: true });
    execFileSync('git', ['init', '-q', '--bare', box]);
    const tree = execFileSync('git', ['-C', box, 'hash-object', '-t', 'tree', '-w', '--stdin'], { input: '', encoding: 'utf8' }).trim();
    const commit = execFileSync('git', ['-C', box, '-c', 'user.name=t', '-c', 'user.email=t@test.invalid', 'commit-tree', tree, '-m', 'fixture'], { encoding: 'utf8' }).trim();
    for (const branch of ['dev-sync-example', 'dev-sync-other']) execFileSync('git', ['-C', box, 'update-ref', 'refs/heads/' + branch, commit]);
    const leftovers = ['assembly-dotnet', 'assembly-dotnet-base', 'assembly-dotnet-rail', 'assembly-remote', 'assembly-dotnet-unmarked'];
    for (const name of leftovers) write(join(remoteHome, name, 'sentinel'), 'never delete');
    write(join(bin, 'lsof'), '#!/bin/sh\nexit 0\n', 0o755);
    write(join(bin, 'ssh'), '#!/bin/bash\nexec env HOME="$CLEAN_TEST_REMOTE" bash -c "${@: -1}"\n', 0o755);
    write(join(remoteHome, '.local/bin/node'), `#!/bin/sh\nexec '${process.execPath}' "$@"\n`, 0o755);
    write(join(remoteHome, '.local/bin/systemctl'), '#!/bin/sh\ncase "$*" in *show*) printf "MainPID=0\\nControlGroup=\\n";; esac\n', 0o755);
    write(join(remoteHome, '.local/bin/lsof'), '#!/bin/sh\nif [ "$CLEAN_TEST_BUSY" = 1 ]; then echo p123; exit 0; fi\nexit 1\n', 0o755);
    process.env.PATH = bin + ':' + priorPath;
    process.env.CLEAN_TEST_REMOTE = remoteHome;
    process.env.CLEAN_TEST_BUSY = '1';
    await assert.rejects(cleanLane(root, false, home), /busy/);
    assert.ok(existsSync(unit)); assert.ok(existsSync(profile));
    process.env.CLEAN_TEST_BUSY = '0';
    const unmarked = { ...owner, name: 'unmarked' };
    await assert.rejects(hostResources(unmarked, 'clean', remoteHome), /managed owner/);
    await cleanLane(root, false, home);
    for (const path of [remoteRoot, unit, profile, join(home, 'assembly-dotnet-example'), join(root, 'desktop/target'), join(root, 'dotnet/Assembly.Desktop/obj')])
      assert.equal(existsSync(path), false, path);
    assert.ok(existsSync(join(root, 'desktop/Cargo.toml')), 'cleanup must preserve source work');
    assert.deepEqual(execFileSync('git', ['-C', box, 'for-each-ref', '--format=%(refname:short)'], { encoding: 'utf8' }).trim().split('\n'), ['dev-sync-other']);
    for (const name of leftovers) assert.ok(existsSync(join(remoteHome, name, 'sentinel')), name);
  } finally {
    process.env.PATH = priorPath;
    if (priorRemote === undefined) delete process.env.CLEAN_TEST_REMOTE; else process.env.CLEAN_TEST_REMOTE = priorRemote;
    if (priorBusy === undefined) delete process.env.CLEAN_TEST_BUSY; else process.env.CLEAN_TEST_BUSY = priorBusy;
    rmSync(fixture, { recursive: true, force: true });
  }
});

test('a checkout with no ownership record is refused by name and nothing is touched', async () => {
  const fixture = mkdtempSync(join(realpathSync(tmpdir()), 'lane-clean-unowned-'));
  const home = join(fixture, 'local'), root = join(home, 'dev/work/worktrees/Assembly/example');
  try {
    mkdirSync(root, { recursive: true }); writeFileSync(join(root, '.lane'), 'LANE_NAME=example\nLANE_NUMBER=9\n');
    await assert.rejects(cleanLane(root, false, home), /example has no ownership record.*nothing was touched/);
    assert.ok(existsSync(join(root, '.lane')));
  } finally { rmSync(fixture, { recursive: true, force: true }); }
});
