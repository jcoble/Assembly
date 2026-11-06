// Builds this checkout into the same package a release publishes, for a remote machine, and prints the archive's path.
// The argument is the folder for it. One build per build inputs: the same inputs reuse the archive.
import { execFile } from 'node:child_process';
import { existsSync, promises as fs } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { writeRemoteBackendPackage, REMOTE_BACKEND_TARGET } from './remote-backend-release.ts';

const exec = promisify(execFile);
const root = fileURLToPath(new URL('../', import.meta.url));

// Everything scripts/dev-remote-tools.ts reaches: the toolchain and what it imports, the launcher and the packager.
export async function toolScripts(from: string) {
  const scripts = (await fs.readdir(join(from, 'scripts'))).filter(file => file.endsWith('.ts') && !file.endsWith('.test.ts'));
  return [...scripts.map(file => 'scripts/' + file), 'scripts/adapters/claude-launcher.ts', 'dotnet/scripts/agents-file.sh'];
}

if (import.meta.filename === process.argv[1]) {
  const folder = process.argv[2];
  const commit = (await exec('git', ['rev-parse', 'HEAD'], { cwd: root })).stdout.trim();
  const [identity, version] = (await exec(process.execPath, [join(root, 'scripts/dev-remote-identity.ts'), root])).stdout.trim().split(' ');
  const output = join(folder, identity);
  const archive = join(output, `assembly-remote-backend-${version}-${REMOTE_BACKEND_TARGET}.tar.gz`);
  if (!existsSync(archive)) {
    await fs.rm(folder, { recursive: true, force: true });
    const publish = join(folder, 'publish');
    await fs.mkdir(publish, { recursive: true });
    await exec('dotnet', ['publish', join(root, 'dotnet/Assembly.Remote'), '-c', 'Release', '-r', 'linux-x64', '--self-contained',
      '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:UseSharedCompilation=false', '-p:RunAnalyzersDuringBuild=false',
      `-p:SourceRevisionId=${commit}`, '-v', 'q', '--nologo', '-o', publish], { cwd: root, maxBuffer: 64 * 1024 * 1024 });
    await writeRemoteBackendPackage({ binaryPath: join(publish, 'Assembly.Remote'), outputDirectory: output, sourceDirectory: root, version, commit, extraTools: await toolScripts(root) });
    await fs.rm(publish, { recursive: true, force: true });
  }
  console.log(archive);
}
