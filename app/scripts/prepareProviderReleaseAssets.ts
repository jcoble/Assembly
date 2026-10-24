import { copyFile, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';

const target = process.argv[2];
if (!/^(?:(aarch64|x86_64)-apple-darwin|x86_64-unknown-linux-gnu)$/.test(target ?? '')) {
  throw new Error('Expected a supported macOS target or x86_64-unknown-linux-gnu');
}

const projectRoot = new URL('../', import.meta.url);
const adapterDir = new URL('src-tauri/adapters/', projectRoot);
const outputDir = new URL(`provider-release/${target}/`, projectRoot);
interface ProviderManifest {
  adapters: { files: { path: string }[] }[];
}

const manifest: ProviderManifest = JSON.parse(await readFile(new URL('manifest.json', adapterDir), 'utf8'));

await rm(outputDir, { recursive: true, force: true });
await mkdir(outputDir, { recursive: true });

for (const adapter of manifest.adapters) {
  for (const file of adapter.files) {
    if (path.basename(file.path) !== file.path) {
      throw new Error(`Adapter manifest path must be a file name: ${file.path}`);
    }
    await copyFile(
      new URL(file.path, adapterDir),
      new URL(`provider-${target}-${file.path}`, outputDir)
    );
  }
}

await writeFile(
  new URL(`provider-manifest-${target}.json`, outputDir),
  `${JSON.stringify({ ...manifest, target }, null, 2)}\n`
);
console.log(`Prepared provider release assets for ${target}`);
