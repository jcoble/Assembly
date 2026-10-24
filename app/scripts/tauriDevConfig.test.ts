import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const packageJson = JSON.parse(await readFile(new URL('../package.json', import.meta.url), 'utf8'));
const defaultConfig = JSON.parse(await readFile(new URL('../src-tauri/tauri.conf.json', import.meta.url), 'utf8'));
const attachConfig = JSON.parse(await readFile(new URL('../src-tauri/tauri.dev.attach.conf.json', import.meta.url), 'utf8'));
const viteConfigSource = await readFile(new URL('../vite.config.ts', import.meta.url), 'utf8');
const providerCapabilitiesSource = await readFile(
  new URL('../src-tauri/src/agent_conversation/capabilities.rs', import.meta.url),
  'utf8'
);
const providerUpdateServiceSource = await readFile(
  new URL('../src/lib/shell/providers/providerUpdateService.svelte.ts', import.meta.url),
  'utf8'
);
const cargoManifest = await readFile(new URL('../src-tauri/Cargo.toml', import.meta.url), 'utf8');
const desktopCapabilities = JSON.parse(
  await readFile(new URL('../src-tauri/capabilities/default.json', import.meta.url), 'utf8')
);
const releaseWorkflow = await readFile(
  new URL('../../.github/workflows/release.yml', import.meta.url),
  'utf8'
);

assert.equal(defaultConfig.build.beforeDevCommand, 'pnpm dev');
assert.equal(defaultConfig.build.beforeBuildCommand, 'pnpm build');
assert.equal(defaultConfig.build.devUrl, 'http://127.0.0.1:5177/');
assert.equal(defaultConfig.bundle.active, true);
assert.equal(defaultConfig.bundle.targets, 'app');
assert.equal(defaultConfig.bundle.createUpdaterArtifacts, true);
assert.deepEqual(defaultConfig.bundle.resources, []);
assert.deepEqual(defaultConfig.plugins.updater.endpoints, [
  'https://github.com/jcoble/mac-command-bar/releases/latest/download/latest.json'
]);
assert.match(defaultConfig.plugins.updater.pubkey, /^[A-Za-z0-9+/]+=*$/);
assert.equal(defaultConfig.app.windows[0].url, '/');
assert.equal(defaultConfig.app.windows[0].width, 1600, 'native preview should open wide enough for laptop layout work');
assert.equal(defaultConfig.app.windows[0].height, 1000, 'native preview should open tall enough for docked panes');
assert.equal(packageJson.scripts['tauri:dev'], 'sh scripts/dev-app.sh');
assert.equal(packageJson.scripts['tauri:dev:next'], undefined);
assert.equal(packageJson.scripts['tauri:dev:next:alt'], undefined);
assert.ok(packageJson.dependencies['@agentclientprotocol/codex-acp']);
assert.ok(packageJson.dependencies['@agentclientprotocol/claude-agent-acp']);
assert.equal(packageJson.dependencies['@tauri-apps/plugin-updater'], '2.11.0');
assert.equal(packageJson.dependencies['@tauri-apps/plugin-process'], '2.3.1');
assert.match(cargoManifest, /tauri-plugin-updater = "2\.11\.0"/);
assert.match(cargoManifest, /tauri-plugin-process = "2\.3\.1"/);
assert.ok(desktopCapabilities.permissions.includes('updater:default'));
assert.ok(desktopCapabilities.permissions.includes('process:allow-restart'));
assert.match(releaseWorkflow, /TAURI_SIGNING_PRIVATE_KEY:/);
assert.match(releaseWorkflow, /releaseDraft: false/);
assert.match(providerCapabilitiesSource, /CODEX_ACP_VERSION: &str = "\d+\.\d+\.\d+"/);
assert.match(providerCapabilitiesSource, /CLAUDE_AGENT_ACP_VERSION: &str = "\d+\.\d+\.\d+"/);
assert.match(providerUpdateServiceSource, /status\.restartRequired \? 'restart'/,
  'adapter installs leave restart explicit');
assert.equal(
  packageJson.scripts['test:tauri-app'],
  'pnpm test:tauri-source && pnpm test:tauri-config && pnpm build && pnpm test:native-lsp && cargo build --manifest-path src-tauri/Cargo.toml',
  'expected a repeatable real Tauri app validation script'
);

const attachScript = packageJson.scripts['tauri:dev:attach'];
assert.ok(attachScript, 'expected a Tauri attach-mode dev script');
assert.match(attachScript, /^tauri dev\b/);
assert.match(attachScript, /--config src-tauri\/tauri\.dev\.attach\.conf\.json\b/);

assert.equal(attachConfig.build.beforeDevCommand, '');
assert.equal(attachConfig.build.devUrl, 'http://127.0.0.1:5177/');
assert.equal(attachConfig.build.frontendDist, '../build');

assert.ok(
  viteConfigSource.includes('function createLocalSourceBridgeMiddleware'),
  'expected a reusable local source bridge middleware'
);
assert.ok(
  viteConfigSource.includes('configurePreviewServer'),
  'expected the local source bridge to run under vite preview'
);
assert.match(
  viteConfigSource,
  /configurePreviewServer\(server\)\s*\{\s*server\.middlewares\.use\(createLocalSourceBridgeMiddleware\(\)\);/s,
  'vite preview should mount the same local source bridge used during development'
);

console.log('tauri dev config checks passed');
