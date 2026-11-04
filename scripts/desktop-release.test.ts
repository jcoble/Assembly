import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { archiveName, desktopVersion, latestJson, PACKAGED_CONFIG, writeDesktopPackage } from './desktop-release.ts';

function fixture(t: { after: (fn: () => void) => void }) {
  const root = mkdtempSync(path.join(tmpdir(), 'desktop-release-test-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const bin = path.join(root, 'bin');
  mkdirSync(bin);
  // Stands in for codesign on Linux and records what was signed, in order.
  writeFileSync(path.join(bin, 'codesign'), `#!/bin/sh\necho "$*" >>"${root}/signed"\n`, { mode: 0o755 });
  const window = path.join(root, 'window'), backend = path.join(root, 'backend');
  writeFileSync(window, 'window-bytes');
  writeFileSync(backend, 'backend-bytes');
  return { root, bin, window, backend };
}

function packaged(t: { after: (fn: () => void) => void }) {
  const f = fixture(t);
  const previous = process.env.PATH;
  process.env.PATH = `${f.bin}:${previous}`;
  t.after(() => { process.env.PATH = previous; });
  const archive = writeDesktopPackage({ windowBinary: f.window, backendBinary: f.backend, outputDirectory: path.join(f.root, 'out'), version: '1.2.3', identity: '-' });
  const extracted = path.join(f.root, 'extracted');
  mkdirSync(extracted);
  execFileSync('tar', ['-xzf', archive, '-C', extracted]);
  return { ...f, archive, contents: path.join(extracted, 'Assembly.app/Contents') };
}

test('the archive keeps the old asset name and holds one .app at its root', t => {
  const f = packaged(t);
  assert.equal(path.basename(f.archive), 'Assembly_1.2.3_aarch64.app.tar.gz');
  assert.equal(archiveName('1.2.3'), path.basename(f.archive));
  const top = new Set(execFileSync('tar', ['-tzf', f.archive], { encoding: 'utf8' }).trim().split('\n').map(name => name.split('/')[0]));
  assert.deepEqual([...top], ['Assembly.app']);
});

test('the bundle runs the window, finds its backend and themes, and carries no lane state', t => {
  const f = packaged(t);
  assert.equal(readFileSync(path.join(f.contents, 'MacOS/assembly-desktop'), 'utf8'), 'window-bytes');
  assert.equal(statSync(path.join(f.contents, 'MacOS/assembly-desktop')).mode & 0o777, 0o755);
  assert.equal(readFileSync(path.join(f.contents, 'Resources/backend/Assembly.Desktop'), 'utf8'), 'backend-bytes');
  assert.equal(statSync(path.join(f.contents, 'Resources/backend/Assembly.Desktop')).mode & 0o777, 0o755);
  assert.deepEqual(JSON.parse(readFileSync(path.join(f.contents, 'Resources/backend/packaged.json'), 'utf8')), PACKAGED_CONFIG);
  assert.throws(() => statSync(path.join(f.contents, 'Resources/backend/lane.json')), { code: 'ENOENT' });
  assert.ok(statSync(path.join(f.contents, 'Resources/icon.icns')).size > 0);
  assert.ok(statSync(path.join(f.contents, 'Resources/themes')).isDirectory());
});

test('Info.plist keeps the old identity with the new program and this version', t => {
  const plist = readFileSync(path.join(packaged(t).contents, 'Info.plist'), 'utf8');
  assert.match(plist, /<key>CFBundleExecutable<\/key>\s*<string>assembly-desktop<\/string>/);
  assert.match(plist, /<key>CFBundleIdentifier<\/key>\s*<string>dev\.blackcolours\.MacCommandBarWebviewPreview<\/string>/);
  assert.match(plist, /<key>CFBundleShortVersionString<\/key>\s*<string>1\.2\.3<\/string>/);
  assert.match(plist, /<key>CFBundleVersion<\/key>\s*<string>1\.2\.3<\/string>/);
  assert.match(plist, /<string>assembly<\/string>/);
  assert.doesNotMatch(plist, /@VERSION@|mac-command-bar-webview-preview/);
});

test('the nested backend is signed before the bundle, with the identity it was given', t => {
  const lines = readFileSync(path.join(packaged(t).root, 'signed'), 'utf8').trim().split('\n');
  assert.equal(lines.length, 2);
  assert.match(lines[0], /--sign - .*Resources\/backend\/Assembly\.Desktop$/);
  assert.match(lines[1], /--sign - .*Assembly\.app$/);
});

test('a failed signature leaves no archive', t => {
  const f = fixture(t);
  writeFileSync(path.join(f.bin, 'codesign'), '#!/bin/sh\nexit 1\n', { mode: 0o755 });
  const previous = process.env.PATH;
  process.env.PATH = `${f.bin}:${previous}`;
  t.after(() => { process.env.PATH = previous; });
  const out = path.join(f.root, 'out');
  assert.throws(() => writeDesktopPackage({ windowBinary: f.window, backendBinary: f.backend, outputDirectory: out, version: '1.2.3', identity: '-' }));
  assert.throws(() => statSync(path.join(out, archiveName('1.2.3'))), { code: 'ENOENT' });
});

test('latest.json has the same shape as the old release', () => {
  const when = new Date('2026-10-15T00:23:12.260Z');
  assert.deepEqual(JSON.parse(latestJson('0.1.6', 'notes', 'sig-text', 'https://example/asset', when)), {
    version: '0.1.6',
    notes: 'notes',
    pub_date: '2026-10-15T00:23:12.260Z',
    platforms: {
      'darwin-aarch64': { signature: 'sig-text', url: 'https://example/asset' },
      'darwin-aarch64-app': { signature: 'sig-text', url: 'https://example/asset' }
    }
  });
});

test('the version comes from the package section of desktop/Cargo.toml', t => {
  const f = fixture(t);
  mkdirSync(path.join(f.root, 'desktop'));
  writeFileSync(path.join(f.root, 'desktop/Cargo.toml'), '[package]\nname = "x"\nversion = "4.5.6"\n\n[dependencies]\nserde = { version = "=1.0.0" }\n');
  assert.equal(desktopVersion(f.root), '4.5.6');
  assert.match(desktopVersion(), /^\d+\.\d+\.\d+$/);
  writeFileSync(path.join(f.root, 'desktop/Cargo.toml'), '[package]\nname = "x"\n\n[dependencies]\nversion = "1.0.0"\n');
  assert.throws(() => desktopVersion(f.root), /no stable package version/);
});
