/**
 * themeRegistry.test.ts — proves a theme is complete, and proves the built-in
 * Houston theme still says exactly what the app already renders.
 *
 * The app shell is painted from three places that do NOT share a palette:
 * the CSS custom properties in `nextTokens.css`, Monaco's own theme object in
 * `sourcePreviewAppearance.ts`, and Ghostty's palette in the theme registry.
 * The theme registry gathers all three into one named set so switching themes
 * can change all three at once.
 *
 * That gathering is a COPY, which is the danger: change a color in one of the
 * original files and the registry keeps handing out the old one, so the app
 * looks one way until you pick a theme and another way afterwards. The checks
 * below read the original files and fail the moment the copy drifts.
 *
 * Run: node --experimental-strip-types scripts/themeRegistry.test.ts
 */
import assert from 'node:assert/strict';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { sourcePreviewAppearance } from '../src/lib/sourcePreviewAppearance.ts';
import {
  CHROME_TOKEN_NAMES,
  DEFAULT_THEME_ID,
  PALETTE_TOKEN_NAMES,
  THEMES,
  TOKEN_NAMES,
  getTheme,
  isThemeId,
  listThemes,
  resolveThemeId
} from '../src/lib/shell/settings/themes/themeRegistry.ts';

const projectRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (relativePath: string): string => readFileSync(resolve(projectRoot, relativePath), 'utf8');

const NEXT_TOKENS_PATH = 'src/lib/shell/styles/nextTokens.css';
const THEME_CHROME_PATH = 'src/lib/shell/styles/themeChrome.css';
const GHOSTTY_FACTORY_PATH = 'src/lib/shell/terminal/ghosttyFactory.ts';
const CONTRACT_PATH = 'src/lib/shell/settings/themes/_(themes)-INTEGRATION.md';

const stripComments = (css: string): string => css.replace(/\/\*[\s\S]*?\*\//g, '');
const tidy = (value: string): string => value.trim().replace(/\s+/g, ' ');

/** Every `--name: value;` a stylesheet declares, flattened into one map. */
function readDeclarations(css: string): Map<string, string> {
  const declarations = new Map<string, string>();
  const source = stripComments(css);
  for (const match of source.matchAll(/(--[a-zA-Z0-9-]+)\s*:\s*([^;}]+)/g)) {
    declarations.set(match[1], tidy(match[2]));
  }
  return declarations;
}

// ── The registry offers real themes, and Houston is the one you get ────────
{
  assert.equal(DEFAULT_THEME_ID, 'assembly', 'Assembly is the look the app ships with');
  assert.ok(THEMES.length >= 2, 'a chooser with one theme proves nothing');
  assert.deepEqual(
    listThemes().map((theme) => theme.id),
    THEMES.map((theme) => theme.id),
    'listThemes() must hand back the whole roster'
  );
  assert.equal(getTheme(DEFAULT_THEME_ID).id, 'assembly');
  assert.ok(
    THEMES.some((theme) => theme.id === 'dracula'),
    'Dracula ships as the second theme'
  );

  for (const theme of THEMES) {
    assert.ok(theme.label.length > 0, `${theme.id} needs a name a person can read`);
    assert.ok(
      theme.description.length > 0,
      `${theme.id} needs a one-line description for the chooser`
    );
    assert.ok(isThemeId(theme.id), `isThemeId must recognise ${theme.id}`);
  }

  // A settings file written before themes existed says "dark", and there has
  // never been a light theme. Both must land on the theme the app ships with
  // rather than on nothing at all.
  assert.equal(resolveThemeId('dark'), 'assembly');
  assert.equal(resolveThemeId('light'), 'assembly');
  assert.equal(resolveThemeId('nonsense'), 'assembly');
  assert.equal(resolveThemeId(undefined), 'assembly');
  assert.equal(resolveThemeId('dracula'), 'dracula');
  assert.equal(getTheme('nonsense').id, 'assembly', 'an unknown id falls back, never throws');
}

// ── Every theme sets every color the shell reads ───────────────────────────
{
  const semanticNames = [
    '--color-selected',
    '--color-selected-border',
    '--color-hover',
    '--color-focus-solid',
    '--color-disabled-text',
    '--color-status-idle'
  ];
  const expected = [...TOKEN_NAMES].sort();
  assert.deepEqual(
    [...PALETTE_TOKEN_NAMES, ...CHROME_TOKEN_NAMES].sort(),
    expected,
    'TOKEN_NAMES must be the palette names plus the extra chrome names, nothing else'
  );
  for (const name of semanticNames) {
    assert.ok(PALETTE_TOKEN_NAMES.includes(name), `${name} must be part of the theme palette`);
  }

  for (const theme of THEMES) {
    assert.deepEqual(
      Object.keys(theme.tokens).sort(),
      expected,
      `${theme.id} does not set exactly the same colors as the other themes — ` +
        'a name it leaves out keeps whatever the previous theme painted'
    );
    for (const [name, value] of Object.entries(theme.tokens)) {
      assert.ok(
        /^(#[0-9a-f]{3,8}|rgba?\(.+\))$/.test(value),
        `${theme.id} ${name} is "${value}", which is not a color`
      );
    }
  }
}

// ── The default theme still matches the stylesheet the app actually loads ────────────
//
// This is the check that matters most. Houston is a copy of the values in
// `nextTokens.css`; if someone edits that file the copy has to move with it,
// or the shell repaints itself the first time a theme is applied.
{
  const houston = getTheme(DEFAULT_THEME_ID);
  const stylesheet = readDeclarations(read(NEXT_TOKENS_PATH));

  const colorsInStylesheet = [...stylesheet.keys()].filter((name) => name.startsWith('--color-'));
  assert.ok(colorsInStylesheet.length >= 20, 'expected the shell stylesheet to set a real palette');

  assert.deepEqual(
    [...PALETTE_TOKEN_NAMES].sort(),
    colorsInStylesheet.sort(),
    `the theme palette and ${NEXT_TOKENS_PATH} must name exactly the same colors`
  );

  for (const name of colorsInStylesheet) {
    assert.equal(
      houston.tokens[name],
      stylesheet.get(name),
      `Houston says ${name} is ${houston.tokens[name]} but ${NEXT_TOKENS_PATH} ` +
        `says ${stylesheet.get(name)} — switching to Houston would change the screen`
    );
  }

  // The handful of extra chrome colors have no home in the shared token file,
  // so they get their own stylesheet — and the same drift check.
  const chrome = readDeclarations(read(THEME_CHROME_PATH));
  assert.deepEqual(
    [...chrome.keys()].sort(),
    [...CHROME_TOKEN_NAMES].sort(),
    `${THEME_CHROME_PATH} must define exactly the extra chrome colors`
  );
  for (const name of CHROME_TOKEN_NAMES) {
    assert.equal(
      houston.tokens[name],
      chrome.get(name),
      `Houston and ${THEME_CHROME_PATH} disagree about ${name}`
    );
  }
}

// ── Every theme carries a complete editor theme ────────────────────────────
{
  const houstonMonaco = getTheme('houston').monaco;
  const ruleTokens = houstonMonaco.rules.map((rule) => rule.token).sort();
  const colorKeys = Object.keys(houstonMonaco.colors).sort();

  for (const theme of THEMES) {
    assert.equal(theme.monaco.id, theme.id, `${theme.id}'s editor theme must use the theme's id`);
    assert.deepEqual(
      theme.monaco.rules.map((rule) => rule.token).sort(),
      ruleTokens,
      `${theme.id}'s editor theme colors a different set of code tokens — the ` +
        'ones it leaves out keep the previous theme’s color'
    );
    assert.deepEqual(
      Object.keys(theme.monaco.colors).sort(),
      colorKeys,
      `${theme.id}'s editor theme leaves out some of the editor surfaces`
    );
    for (const rule of theme.monaco.rules) {
      assert.ok(
        rule.foreground === undefined || /^[0-9a-f]{6}$/i.test(rule.foreground),
        `${theme.id} ${rule.token}: Monaco wants a bare six-digit color, got "${rule.foreground}"`
      );
    }
    for (const [key, value] of Object.entries(theme.monaco.colors)) {
      assert.ok(/^#[0-9a-f]{6,8}$/i.test(value), `${theme.id} ${key} is "${value}", not a color`);
    }
    assert.equal(
      theme.monaco.encodedTokensColors?.length,
      houstonMonaco.encodedTokensColors?.length,
      `${theme.id} must list the same number of highlighting colors as Houston`
    );
  }
}

// ── Houston's editor theme is still the one the editor is built with ───────
{
  const houston = getTheme('houston').monaco;
  const live = sourcePreviewAppearance.theme;

  assert.equal(houston.id, live.id);
  assert.equal(houston.base, live.base);
  assert.equal(houston.inherit, live.inherit);
  assert.deepEqual(
    houston.rules,
    live.rules,
    'Houston’s editor colors have drifted from src/lib/sourcePreviewAppearance.ts — ' +
      'the code in the editor would change color the first time a theme is applied'
  );
  assert.deepEqual(houston.colors, live.colors, 'Houston’s editor surfaces have drifted');
  assert.deepEqual(
    houston.encodedTokensColors,
    live.encodedTokensColors,
    'Houston’s highlighting colors have drifted'
  );
}

// ── Ghostty's terminal reads only #rrggbb ─────────────────────────────────
//
// Foreground, background, cursor and the 16 ANSI colours go into Ghostty's
// WASM terminal through parseColorToHex, which reads #hex and rgb() only; any
// other form becomes black. selectionBackground is only a canvas fill, so it
// may be the theme's rgba() highlighter.
{
  const source = read(GHOSTTY_FACTORY_PATH);
  assert.match(source, /theme:\s*\{ \.\.\.getTheme\(settings\.appearance\.themeId\)\.terminal \}/);
  for (const theme of THEMES) {
    for (const [slot, value] of Object.entries(theme.terminal)) {
      if (slot === 'selectionBackground') continue;
      assert.match(value, /^#[0-9a-f]{6}$/i, `${theme.id} terminal.${slot} is ${value}; Ghostty's WASM terminal reads only #rrggbb and draws any other form as black`);
    }
  }
}

// ── Dockview must follow the theme, not a color typed into a component ─────
//
// dockview reads its own `--dv-…` names. While those are set to a literal
// colour in a component's stylesheet, the dock keeps that colour no matter
// which theme is picked. Each one has to become a `var(--color-…)` reference.
// Until the integrator makes those edits, every remaining literal must be
// written down in the integration contract, file and value, so none is
// forgotten; once the contract file is gone, none may remain at all.
{
  const scanRoots = ['src/lib/shell', 'src/routes'];
  const files = [];
  const walk = (directory) => {
    for (const entry of readdirSync(resolve(projectRoot, directory), { withFileTypes: true })) {
      const relative = `${directory}/${entry.name}`;
      if (entry.isDirectory()) walk(relative);
      else if (entry.name.endsWith('.svelte') || entry.name.endsWith('.css')) files.push(relative);
    }
  };
  for (const directory of scanRoots) walk(directory);
  assert.ok(files.length > 10, 'expected to find the shell component stylesheets');

  const literals = [];
  const references = [];
  for (const path of files) {
    for (const match of stripComments(read(path)).matchAll(/(--dv-[a-z0-9-]+)\s*:\s*([^;}]+)/g)) {
      const name = match[1];
      const value = tidy(match[2]);
      if (/#[0-9a-fA-F]{3,8}/.test(value)) literals.push({ path, name, value });
      for (const reference of value.matchAll(/var\(\s*(--color-[a-zA-Z0-9-]+)/g)) {
        references.push({ path, name, token: reference[1] });
      }
    }
  }

  for (const reference of references) {
    assert.ok(
      TOKEN_NAMES.includes(reference.token),
      `${reference.path}: ${reference.name} reads ${reference.token}, which no theme sets — ` +
        'the dock would draw that part with no colour at all'
    );
  }

  const contractExists = existsSync(resolve(projectRoot, CONTRACT_PATH));
  if (!contractExists) {
    assert.deepEqual(
      literals.map((entry) => `${entry.path}: ${entry.name}: ${entry.value}`),
      [],
      'these dock colours are typed straight into a component, so the dock keeps ' +
        'them whichever theme is picked. Point each one at a var(--color-…) name'
    );
  } else {
    const contract = read(CONTRACT_PATH);
    const missing = literals
      .filter((entry) => !(contract.includes(entry.name) && contract.includes(entry.value)))
      .map((entry) => `${entry.path}: ${entry.name}: ${entry.value}`);
    assert.deepEqual(
      missing,
      [],
      'these dock colours are still typed into a component AND are not written down ' +
        `in ${CONTRACT_PATH}, so the integrator has no way to know about them`
    );
    assert.ok(
      literals.length > 0 || contract.length > 0,
      'the contract exists, so it should still describe the work'
    );
  }
}

console.log('themeRegistry.test.ts: all checks passed');
