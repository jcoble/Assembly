/**
 * How the editor panel and its status chip are wired together.
 *
 * Run it with:
 *   node --experimental-strip-types \
 *     src/lib/shell/files/components/editor/editorPanelLanguageServer.test.ts
 *
 * The words on the chip and the waiting rules are tested for real in
 * `languageServerStatus.test.ts`. What cannot be tested that way is the wiring
 * inside the Svelte components, so it is read out of their source here. These
 * are the promises that would be silently broken by an ordinary-looking
 * edit:
 *
 *  0. The project's switch lives in the editor status bar, and the
 *     editor's file-tab row keeps only the open file's own controls. The panel
 *     remains the single owner of the status pipeline; the switch only reads
 *     what the panel publishes.
 *  1. Nothing is asked on a timer. The panel reads the status when a file
 *     opens and otherwise waits to be told — a poll would put the desktop app
 *     back under the load this whole change exists to remove.
 *  2. Pushed updates are only listened for when the desktop build says it
 *     sends them, so an older build is left exactly as it was.
 *  3. The editor's own inline-hint callback is replaced by the one that waits,
 *     and it is set AFTER the callback set is spread in — set before, the
 *     spread would put the original straight back.
 *  4. Nothing about the chip is painted from a colour written into the file.
 */
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));
const panelSource = readFileSync(path.join(here, '..', 'EditorPanel.svelte'), 'utf8');
const chipSource = readFileSync(path.join(here, '..', '..', '..', 'components', 'LanguageServerStatusChip.svelte'), 'utf8');
const intelligenceSource = readFileSync(
  path.join(here, '..', '..', 'editor', 'sourceIntelligence.ts'),
  'utf8'
);
const controlsSource = readFileSync(
  path.join(here, '..', '..', '..', 'components', 'LanguageIntelligenceControls.svelte'),
  'utf8'
);
const barSource = readFileSync(
  path.join(here, '..', '..', 'editor', 'languageIntelligenceBar.svelte.ts'),
  'utf8'
);

test('the global mode control is a compact switch', () => {
  assert.match(controlsSource, /import \{ Switch \} from '\$lib\/components\/ui\/switch\/index\.js'/);
  assert.match(controlsSource, /<span class="mode-label">Supercharged<\/span>/);
  assert.match(controlsSource, /size="sm"/);
  assert.match(controlsSource, /checked=\{settings\.intelligence\.languageServers\}/);
  assert.match(controlsSource, /onCheckedChange=\{\(checked\) => void chooseMode\(checked\)\}/);
  assert.ok(
    !controlsSource.includes('<LanguageServerStatusChip'),
    'the status pipeline remains separate from the global mode choice'
  );
});

test('the switch stays in the waiting colour until the server reports ready', () => {
  assert.match(controlsSource, /readLanguageServerState\(languageIntelligenceBar\.status\)/);
  assert.match(controlsSource, /serverState === 'ready'[\s\S]*?'running'[\s\S]*?'waiting'/);
  assert.match(controlsSource, /data-tone=\{tone\}/);
  assert.match(controlsSource, /--switch-track-waiting/);
  assert.ok(
    !controlsSource.includes(':has(') && !controlsSource.includes('has-['),
    'no `:has()` selectors'
  );
});

test('the mode choice uses the shared async controller', () => {
  assert.match(controlsSource, /await setLanguageServersEnabled\(enabled\)/);
  assert.match(
    controlsSource,
    /if \(result\.supported\) \{[\s\S]*?settings\.intelligence\.languageServers = enabled;[\s\S]*?\}/,
    'unsupported builds must not change the saved mode'
  );
});

test('only the open file\'s own controls stay in the file-tab title row', () => {
  const titleRow = panelSource.slice(
    panelSource.indexOf('<div class="editor-header">'),
    panelSource.indexOf('<div class="editor-canvas">')
  );
  assert.ok(titleRow.length > 0, 'the editor title row must still exist');
  assert.match(
    titleRow,
    /aria-label=\{activeFileIsHtml \? 'HTML view' : 'Markdown view'\}/,
    'the Source/Preview toggle is about the open file, so it stays with the tabs'
  );
  assert.ok(
    !panelSource.includes('<LanguageServerStatusChip'),
    'the chip is about the project and has moved to the top strip'
  );
  assert.ok(
    !panelSource.includes('class="intelligence"'),
    'the switch is about the project and has moved to the top strip'
  );
  assert.doesNotMatch(panelSource, /editor-status-bar|status-slot/);
});

test('the status pipeline has one owner', () => {
  assert.match(panelSource, /publishLanguageIntelligenceBar\(/);
  for (const call of ['invoke', 'listen(', 'read_source_lsp_status']) {
    assert.ok(
      !controlsSource.includes(call) && !barSource.includes(call),
      `the top-strip controls must not run the status pipeline a second time (${call})`
    );
  }
});

test('the global mode stays usable without an open project', () => {
  assert.ok(
    !controlsSource.includes('{#if languageIntelligenceBar.hasProject}'),
    'the mode choice must not remove itself from the pill group'
  );
  assert.doesNotMatch(
    controlsSource,
    /disabled=\{[^}]*!languageIntelligenceBar\.hasProject/,
    'a global setting remains selectable before opening a project'
  );
  assert.match(controlsSource, /A language server starts when you open a supported project file\./);
});

test('the chip is only rendered when there is something truthful to say', () => {
  assert.match(
    chipSource,
    /\{#if chip\}/,
    'with no status — a browser tab, or an older desktop build — the chip must not render at all'
  );
});

test('nothing is asked on a repeating timer', () => {
  for (const [name, source] of [
    ['EditorPanel.svelte', panelSource],
    ['LanguageServerStatusChip.svelte', chipSource],
    ['LanguageIntelligenceControls.svelte', controlsSource]
  ] as const) {
    assert.ok(!source.includes('setInterval'), `${name} must not poll the desktop app`);
  }
});

test('the panel reads the status when a file opens', () => {
  assert.match(panelSource, /readSourceLspStatusFromTauri/);
  assert.match(panelSource, /refreshLanguageServerStatus\(\)/);
});

test('pushed updates are only listened for when the build says it sends them', () => {
  assert.match(intelligenceSource, /'source-lsp-status-changed'/);
  assert.match(intelligenceSource, /hasBackendCapability\('lspStatusEvents'\)/);
  assert.match(panelSource, /sourceIntelligence\.subscribeToLanguageServerStatus\(/);
});

test('a pushed update about another project or language is ignored', () => {
  assert.match(panelSource, /statusMessageIsAboutThisFile\(/);
});

test('held questions are released only by a ready update for the open file', () => {
  assert.match(intelligenceSource, /statusMessageIsAboutThisFile\(/);
  assert.match(
    intelligenceSource,
    /statusMessageIsAboutThisFile\(\s*event\.payload,\s*projectRoot,\s*activePreview\?\.language \?\? null\s*\)/
  );
});

test('the waiting inline-hint lookup replaces the original, not the other way round', () => {
  const spreadAt = panelSource.indexOf('{...sourceIntelligence.callbacks}');
  const overrideAt = panelSource.indexOf('onInlayHintLookup={');
  assert.ok(spreadAt >= 0, 'the panel still spreads the callback set onto the editor');
  assert.ok(overrideAt >= 0, 'the panel overrides the inline-hint lookup');
  assert.ok(
    overrideAt > spreadAt,
    'the override must come after the spread or the spread wins and hints are never held back'
  );
});

test('the second diagnostics read goes through the waiting room', () => {
  assert.match(panelSource, /languageServerGate/);
  assert.match(panelSource, /languageServerGate\.onReady\(\(\) => \{/);
  assert.match(panelSource, /loadDiagnosticsAfterServerReady\(path, generation\)/);
});

test('the chip and the controls are painted only from the shared colour names', () => {
  for (const [name, source] of [
    ['LanguageServerStatusChip.svelte', chipSource],
    ['LanguageIntelligenceControls.svelte', controlsSource]
  ] as const) {
    const style = source.slice(source.indexOf('<style>'));
    const writtenInColours = style.match(/#[0-9a-fA-F]{3,8}\b|\brgba?\(/g) ?? [];
    assert.deepEqual(
      writtenInColours,
      [],
      `colours belong in the shared token file, not in ${name}`
    );
  }
});

test('no text in the chip or the controls is smaller than 12 pixels', () => {
  for (const [name, source] of [
    ['LanguageServerStatusChip.svelte', chipSource],
    ['LanguageIntelligenceControls.svelte', controlsSource]
  ] as const) {
    const sizes = [...source.matchAll(/font-size:\s*(\d+)px/g)].map((match) => Number(match[1]));
    assert.ok(sizes.length > 0, `${name} should state its text size`);
    for (const size of sizes) {
      assert.ok(size >= 12, `text at ${size}px in ${name} is below the 12px floor`);
    }
  }
});
