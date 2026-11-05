// A machine on a newer .NET SDK asks for a newer ILLink package than the one this repo last saw, and nothing about that
// may show up in git: no build or publish may leave a checkout dirty, or the identity check would refuse every deploy.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { cpSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const repo = fileURLToPath(new URL('../', import.meta.url));

test('restoring on a newer SDK leaves the checkout clean', { timeout: 300_000 }, () => {
  const root = mkdtempSync(join(realpathSync(tmpdir()), 'restore-clean-'));
  try {
    const tracked = execFileSync('git', ['ls-files', '-z', 'dotnet'], { cwd: repo, encoding: 'utf8' }).split('\0');
    // Restore reads only project files and shared settings, and a lock file if one is tracked.
    for (const file of tracked.filter(file => /(\.csproj|\.slnx|\.props|\.targets|packages\.lock\.json)$/.test(file))) {
      mkdirSync(dirname(join(root, file)), { recursive: true });
      cpSync(join(repo, file), join(root, file));
    }
    const git = (...args: string[]) => execFileSync('git', args, { cwd: root, encoding: 'utf8' });
    git('init', '-q');
    writeFileSync(join(root, '.git/info/exclude'), 'bin/\nobj/\n');
    git('add', '-A');
    git('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '-qm', 'projects');
    // Outside the checkout, as a newer SDK's own list is. The real SDK machinery reads it to pick the package.
    const sdk = join(root, '.git/newer-sdk.targets');
    writeFileSync(sdk, `<Project><ItemGroup><KnownILLinkPack Update="Microsoft.NET.ILLink.Tasks" ILLinkPackVersion="10.0.13" /></ItemGroup></Project>`);
    execFileSync('dotnet', ['restore', 'dotnet/Assembly.Engine', '-v', 'q', '--nologo', `-p:CustomAfterMicrosoftCommonTargets=${sdk}`],
      { cwd: root, encoding: 'utf8', env: { ...process.env, MSBUILDDISABLENODEREUSE: '1' }, stdio: 'pipe' });
    assert.match(readFileSync(join(root, 'dotnet/Assembly.Engine/obj/project.assets.json'), 'utf8'), /Microsoft\.NET\.ILLink\.Tasks\/10\.0\.13/, 'the stand-in for a newer SDK took no effect');
    assert.equal(git('status', '--porcelain'), '');
  } finally { rmSync(root, { recursive: true, force: true }); }
});
