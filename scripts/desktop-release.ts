// What tauri-action did for the Mac release, for the GPUI window and its C# backend: the signed .app archive and the
// updater's latest.json, under the same names.
//   desktop-release.ts version                                  the version, from desktop/Cargo.toml
//   desktop-release.ts package <window> <backend> <output>      Assembly_<version>_aarch64.app.tar.gz
//   desktop-release.ts latest-json <version> <notes> <sig> <url> <output>
import { execFileSync } from 'node:child_process';
import { chmodSync, copyFileSync, cpSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repository = fileURLToPath(new URL('../', import.meta.url));

// Paths under the user's home, where the packaged app keeps main's data and reaches main's remote.
export const PACKAGED_CONFIG = {
  port: 18797,
  home: 'assembly-dotnet/mac',
  tokenFile: '.config/assembly-dotnet/env',
  remotePort: 9797,
  remoteTokenFile: '.config/assembly-dotnet/env'
};

export function desktopVersion(source = repository): string {
  const manifest = readFileSync(path.join(source, 'desktop/Cargo.toml'), 'utf8');
  const version = /^\[package\][^[]*?^version = "(\d+\.\d+\.\d+)"$/m.exec(manifest)?.[1];
  if (!version) throw new Error('desktop/Cargo.toml has no stable package version');
  return version;
}

export function archiveName(version: string): string {
  return `Assembly_${version}_aarch64.app.tar.gz`;
}

export type PackageInput = {
  windowBinary: string;
  backendBinary: string;
  outputDirectory: string;
  version: string;
  identity: string;
  source?: string;
};

export function writeDesktopPackage(input: PackageInput): string {
  const source = input.source ?? repository;
  const staging = mkdtempSync(path.join(tmpdir(), 'assembly-desktop-package-'));
  try {
    const app = path.join(staging, 'Assembly.app');
    const macos = path.join(app, 'Contents/MacOS');
    const resources = path.join(app, 'Contents/Resources');
    const backend = path.join(resources, 'backend');
    mkdirSync(macos, { recursive: true });
    mkdirSync(backend, { recursive: true });
    writeFileSync(path.join(app, 'Contents/Info.plist'),
      readFileSync(path.join(source, 'desktop/resources/Info.plist'), 'utf8').replaceAll('@VERSION@', input.version));
    copyFileSync(path.join(source, 'desktop/resources/icon.icns'), path.join(resources, 'icon.icns'));
    cpSync(path.join(source, 'desktop/themes'), path.join(resources, 'themes'), { recursive: true });
    const window = path.join(macos, 'assembly-desktop');
    copyFileSync(input.windowBinary, window);
    chmodSync(window, 0o755);
    const program = path.join(backend, 'Assembly.Desktop');
    copyFileSync(input.backendBinary, program);
    chmodSync(program, 0o755);
    writeFileSync(path.join(backend, 'packaged.json'), `${JSON.stringify(PACKAGED_CONFIG, null, 2)}\n`);
    // The nested program first: signing the bundle seals what's inside it.
    for (const target of [program, app]) execFileSync('codesign', ['--force', '--timestamp=none', '--sign', input.identity, target]);
    mkdirSync(input.outputDirectory, { recursive: true });
    const archive = path.join(input.outputDirectory, archiveName(input.version));
    execFileSync('tar', ['-czf', archive, '-C', staging, 'Assembly.app'], { env: { ...process.env, COPYFILE_DISABLE: '1' } });
    return archive;
  } finally {
    rmSync(staging, { recursive: true, force: true });
  }
}

// The signature is the .sig file's text. The two platform keys are the ones the old release published.
export function latestJson(version: string, notes: string, signature: string, url: string, now = new Date()): string {
  const platform = { signature, url };
  return `${JSON.stringify({
    version,
    notes,
    pub_date: now.toISOString(),
    platforms: { 'darwin-aarch64': platform, 'darwin-aarch64-app': platform }
  }, null, 2)}\n`;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [command, ...args] = process.argv.slice(2);
  if (command === 'version') {
    console.log(desktopVersion());
  } else if (command === 'package' && args.length === 3) {
    if (process.platform !== 'darwin' || process.arch !== 'arm64') throw new Error('The desktop release must be built on macOS arm64');
    const identity = process.env.APPLE_SIGNING_IDENTITY;
    if (!identity) throw new Error('APPLE_SIGNING_IDENTITY is required');
    const [windowBinary, backendBinary, outputDirectory] = args;
    console.log(writeDesktopPackage({ windowBinary, backendBinary, outputDirectory, version: desktopVersion(), identity }));
  } else if (command === 'latest-json' && args.length === 5) {
    const [version, notes, signature, url, output] = args;
    writeFileSync(output, latestJson(version, notes, readFileSync(signature, 'utf8'), url));
  } else {
    throw new Error('Usage: desktop-release.ts version | package <window> <backend> <output> | latest-json <version> <notes> <sig> <url> <output>');
  }
}
