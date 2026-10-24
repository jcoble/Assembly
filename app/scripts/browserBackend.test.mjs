import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

globalThis.$state = (value) => value;

const { createFakeBrowserBackend } = await import(
  '../src/lib/shell/browser/browserBackend.ts'
);

function testTauriBackendWrapsEveryCommandInInputKey() {
  const source = readFileSync(new URL('../src/lib/shell/browser/browserBackend.ts', import.meta.url), 'utf8');
  const commands = [...source.matchAll(/invokeBrowserCommandFromTauri<[^>]*>\('([a-z_]+)'/g)].map((m) => m[1]);
  assert.equal(commands.length, 16, `expected 16 tauri browser commands, saw ${commands.length}`);
  const unwrapped = [...source.matchAll(/invokeBrowserCommandFromTauri<[^>]*>\('([a-z_]+)',\s*(?!\{ input \})[^\s)]/g)].map((m) => m[1]);
  assert.deepEqual(unwrapped, [], `these commands pass their payload unwrapped: ${unwrapped.join(', ')}`);
}

testTauriBackendWrapsEveryCommandInInputKey();

const backend = createFakeBrowserBackend();
const target = { workspaceId: 'workspace-1', tabId: 'tab-1', generation: 1 };
backend.create_browser_tab({ ...target, url: 'https://example.com/' });
backend.navigate_browser_tab({ ...target, url: 'https://example.com/next' });
backend.go_back_browser_tab(target);
backend.go_forward_browser_tab(target);
backend.set_browser_tab_viewport({
  ...target,
  viewport: { preset: 'mobile-m', width: 375, height: 667 }
});
const capture = backend.capture_browser_viewport(target);

assert.deepEqual(
  backend.calls.map((call) => call.command),
  [
    'create_browser_tab',
    'navigate_browser_tab',
    'go_back_browser_tab',
    'go_forward_browser_tab',
    'set_browser_tab_viewport',
    'capture_browser_viewport'
  ]
);
assert.equal(backend.tabs.get('tab-1').url, 'https://example.com/next');
assert.equal(backend.tabs.get('tab-1').viewport.preset, 'mobile-m');
assert.deepEqual(capture.bytes.slice(0, 4), [137, 80, 78, 71]);
assert.equal(capture.sourceHash, 'fake-capture:tab-1:1');

backend.hide_browser_workspace({ workspaceId: 'workspace-1' });
assert.equal(backend.tabs.get('tab-1').visible, false);
backend.show_browser_tab(target);
assert.equal(backend.tabs.get('tab-1').visible, true);
backend.close_browser_tab(target);
assert.equal(backend.tabs.has('tab-1'), false);

console.log('browserBackend: all tests passed');
