// The bench's build step: the desktop and the backend, each only when its inputs changed (dev-build-cache.ts).
// Run by scripts/bench.sh with the lane's env (DATA) and from the checkout's root; prints one line per part.
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { join } from 'node:path';
import { ensureBuild } from '../dev-build-cache.ts';

const root = process.cwd();
const data = process.env.DATA;
if (!data) throw new Error('DATA is not set; source scripts/lane-env.sh');

async function run(tool: string, args: string[], env: NodeJS.ProcessEnv): Promise<void> {
  const child = spawn('nice', ['-n', '10', tool, ...args], { cwd: root, env: { ...process.env, ...env }, stdio: 'inherit' });
  const [code, signal] = await once(child, 'exit');
  if (code !== 0) throw new Error(`${tool} ${args[0]} failed (${signal ?? code})`);
}

const desktop = join(data, 'target/debug/assembly-desktop');
const built = await ensureBuild(root, 'desktop', [desktop], () =>
  run('cargo', ['build', '--locked', '--manifest-path', 'desktop/Cargo.toml'], { CARGO_TARGET_DIR: join(data, 'target') }));
console.log(built ? 'desktop built' : 'desktop unchanged');

const backend = join(data, 'ui-check/backend');
const published = await ensureBuild(root, 'backend', [join(backend, 'Assembly.Desktop')], () =>
  run('dotnet', ['publish', '-c', 'Release', '-o', backend, 'dotnet/Assembly.Desktop'],
    { MSBUILDDISABLENODEREUSE: '1', NUGET_HTTP_CACHE_PATH: join(data, 'ui-check/nuget-http-cache') }));
console.log(published ? 'backend published' : 'backend unchanged');
