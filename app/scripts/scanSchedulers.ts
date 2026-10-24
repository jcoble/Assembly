#!/usr/bin/env node
import { execFileSync } from 'node:child_process';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

const root = new URL('..', import.meta.url).pathname;
const repoRoot = join(root, '..');
const srcRoot = join(root, 'src');
const srcPrefix = relative(repoRoot, srcRoot).split(sep).join('/');
const includedExtensions = ['.ts', '.svelte', '.css'];
const excludedFileSuffixes = ['.test.ts', '.test.svelte', '.server.ts'];
const excludedPathSegments = new Set([
  '__generated__',
  'generated',
  'server'
]);

const schedulerPattern =
  /\b(setTimeout|setInterval|requestAnimationFrame|requestIdleCallback|cancelAnimationFrame|cancelIdleCallback|queueMicrotask|tick)\b/g;
const promiseChainPattern = /\.(then|catch|finally)\s*\(/g;
const promiseWrapperPattern = /\b(new\s+Promise|Promise\.(?:race|withResolvers))\s*(?:<[^>]+>)?\s*\(/g;
const anonymousAsyncIifePattern =
  /\b(void|[A-Za-z_$][\w$]*(?:\s*\?\?=|\s*=))\s*\(\s*async\s*\(\s*\)\s*=>/g;
const cssHasPattern = /:has\s*\(/gi;
const tailwindHasPattern = /\b(?:(?:group|peer)-)?has-(?:\[[^\]\r\n]+\]|[A-Za-z][\w-]*)(?:\/[\w-]+)?:/g;
// Animations end (AGENTS.md): an endless loop repaints every frame, so CSS, inline styles and the
// Tailwind loop classes may not repeat forever. A value ends at a quote, except a quoted keyframe name, and
// may hold Svelte {expressions}.
const endlessAnimationPattern = /\banimation(?:-iteration-count)?(?:\|important)?\s*(?::|=\s*\{?\s*["'`]?)(?:[^;{}"'`<>]|\{[^{}"'`<>]*\}|(["'])[\w-]*\1)*?(?<![\w-])infinite(?![\w-])|\banimate-(?:spin|pulse|ping|bounce)\b/gi;

const allowedHits = new Set([
  'src/lib/shell/components/UtilityStrip.svelte:setInterval:const refreshInterval = window.setInterval(() => {',
  'src/lib/shell/components/UtilityStrip.svelte:clearInterval:window.clearInterval(refreshInterval);',
  // One fifteen-second interval for every rail row's age label; it skips hidden windows and unmounting clears it.
  'src/lib/shell/components/SessionRail.svelte:setInterval:const tick = window.setInterval(() => {',
  // The remote-restart warning's one-second countdown; cancel, finishing and unmounting clear it.
  "src/lib/shell/components/RestartProvidersButton.svelte:setInterval:const timer = window.setInterval(() => stepCountdown('tick'), 1000);",
  // Svelte tick is awaited only after mounting a user-requested lazy surface.
  "src/lib/shell/components/DockPanel.svelte:tick:import { tick } from 'svelte';",
  'src/lib/shell/components/DockPanel.svelte:tick:await tick();',
  // The one-shot WebSocket readiness bridge removes every listener when it
  // settles, and disposing the editor closes the socket so the wait rejects.
  'src/lib/shell/files/editor/csharpLanguageClient.ts:new Promise:await new Promise<void>((resolve, reject) => {',
  "src/lib/shell/panels/tasks/TasksPanel.svelte:tick:import { onDestroy, onMount, tick } from 'svelte';",
  'src/lib/shell/panels/tasks/TasksPanel.svelte:tick:await tick();',
  // These one-shot timers are both stopped by the AbortSignal-owning surface.
  // TSK-1553: this callback wait owns one five-minute timer; claim, abort and browser-open failure clear it and remove its listener.
  'src/lib/shell/notionOAuth.ts:new Promise:const code = new Promise<string>((resolve, reject) => {',
  'src/lib/shell/notionOAuth.ts:setTimeout:const timeout = setTimeout(',
  'src/lib/shell/workflows/components/WorkflowRuns.svelte:setTimeout:const timer = window.setTimeout(() => {',
  // The Board badge's one timer: when the earliest snoozed card wakes it recounts and reloads an open board; each recount and unmounting clear it.
  'src/lib/shell/board/BoardButton.svelte:setTimeout:if (result.snoozed_until_ms) timer = window.setTimeout(() => { version += 1; void recount(); }, Math.max(0, result.snoozed_until_ms - Date.now()));',
  // One five-second shot per active turn; the effect clears it when the turn ends or the composer unmounts.
  'src/lib/shell/conversation/components/ConversationComposer.svelte:setTimeout:const timer = setTimeout(() => (turnSettled = true), 5000);',
  // One ten-second shot while a filter group is open; closing the group or unmounting clears it.
  'src/lib/components/ui/filter-pills/filter-pills.svelte:setTimeout:timer = window.setTimeout(close, AUTO_CLOSE_MS);',
  // A jump re-aligns its file two frames later, after its neighbours are drawn; the next
  // jump or unmounting cancels the pending frame.
  'src/lib/shell/git/components/MultiFileDiff.svelte:cancelAnimationFrame:cancelAnimationFrame(jumpFrame);',
  'src/lib/shell/git/components/MultiFileDiff.svelte:requestAnimationFrame:jumpFrame = requestAnimationFrame(() => {',
  "src/lib/shell/git/components/MultiFileDiff.svelte:requestAnimationFrame:jumpFrame = requestAnimationFrame(() => section?.scrollIntoView({ block: 'start' }));",
  'src/lib/shell/git/components/MultiFileDiff.svelte:cancelAnimationFrame:onDestroy(() => cancelAnimationFrame(jumpFrame));',
  // The 350 ms pull request search debounce; each keystroke, Enter and unmounting clear it.
  'src/lib/shell/git/github/PullRequestWorkspace.svelte:setTimeout:let searchTimer: ReturnType<typeof setTimeout> | undefined;',
  'src/lib/shell/git/github/PullRequestWorkspace.svelte:setTimeout:if (searchInput.trim() !== searchedText) searchTimer = setTimeout(() => void load(true), 350);',
  // A diff read races the abort of its combined owner-and-timeout signal, so a read that ignores
  // the signal (every local call) still ends; settled by that abort, listener removed in finally.
  'src/lib/shell/git/gitBackendExtra.ts:new Promise:const aborted = new Promise<never>((_resolve, reject) => {',
  'src/lib/shell/git/gitBackendExtra.ts:Promise.race:return await Promise.race([read(signal), aborted]);',
  // TSK-1424 E/H: one one-second clock for every running workflow stage's elapsed time. It runs only
  // while the panel is visible and a stage is running; the effect cleanup clears it on hide, finish or unmount.
  'src/lib/shell/workflows/components/WorkflowRuns.svelte:setInterval:const timer = window.setInterval(() => (now = Date.now()), 1000);',
  // The Workflows screen's one elapsed-time clock; it runs only while a running step is on screen.
  'src/lib/shell/workflows/WorkflowsScreen.svelte:setInterval:const timer = window.setInterval(() => (now = Date.now()), 1000);',
  // TSK-1424 G: the tools column's one-frame WebKit invalidation (hide, change the grid next frame, reveal
  // a frame later). The pending frame id is kept; a newer transition and dispose() cancel it.
  'src/lib/shell/layout/frame.ts:requestAnimationFrame:toolsFrame = requestAnimationFrame(() => {',
  'src/lib/shell/layout/frame.ts:cancelAnimationFrame:cancelAnimationFrame(toolsFrame);',
  // TSK-1299: the same one-frame invalidation when the drawer changes the frame's width. The pending
  // frame id is kept; the next layout and unmounting cancel it.
  'src/lib/shell/components/ShellFrame.svelte:requestAnimationFrame:layoutRequest = requestAnimationFrame(() => {',
  "src/lib/shell/components/ShellFrame.svelte:requestAnimationFrame:layoutRequest = requestAnimationFrame(() => (centerRegionSlot.style.visibility = ''));",
  'src/lib/shell/components/ShellFrame.svelte:cancelAnimationFrame:cancelAnimationFrame(layoutRequest);',
  // TSK-1424 G: refresh after the panel renders visible; flushSync throws inside an effect. The effect's
  // controller aborts on hide or unmount and the refresh checks it after the tick.
  'src/lib/shell/files/components/FilesPanel.svelte:tick:import { onDestroy, tick, untrack } from "svelte";',
  'src/lib/shell/files/components/FilesPanel.svelte:tick:await tick();',
  // TSK-1424 G: these return the shared cached promise so the preview's {#await} keeps one identity and an
  // image or formula never blinks back to pending; the reads behind them are named async functions.
  'src/lib/shell/files/components/SourceMarkdownPreview.svelte:loadKatex:function loadKatex(): Promise<KatexComponent> {',
  'src/lib/shell/files/components/SourceMarkdownPreview.svelte:localImageUrl:function localImageUrl(file: string): Promise<string> {',
  // TSK-1424 C: the turn's one-second elapsed clock. It runs only while the turn runs and the label and
  // window are visible; the effect cleanup clears it when the turn ends, it scrolls out of view or unmounts.
  'src/lib/shell/conversation/components/ConversationTurnElapsed.svelte:setInterval:const timer = setInterval(() => { now = Date.now(); }, 1000);',
  // TSK-1424 C: waitFor, the 300 ms wait before a local image send reads its bytes and the wait between
  // lookups of a session the rail lacks. Its timer is cleared and its abort listener removed when it ends;
  // Stop aborts the send's signal and stopConversationEvents the lookups' signal, which ends it at once.
  'src/lib/shell/conversation/conversationService.ts:new Promise:await new Promise<void>((resolve) => {',
  'src/lib/shell/conversation/conversationService.ts:setTimeout:const timer = setTimeout(finish, ms);',
  // TSK-1424 B: the chat stream's wait for the next native event. Leaving the session aborts the
  // stream's signal, whose listener wakes the wait; the stream's finally removes that listener.
  'src/lib/shell/conversation/conversationConnection.ts:new Promise:if (!items.length && !signal.aborted) await new Promise<void>((resolve) => { wake = resolve; });',
  // TSK-1561: streamed text waits one frame so a frame's deltas reach the chat as one update. The
  // frame, the 50 ms fallback for a window that draws no frames, and leaving the session (abort)
  // each settle the wait, and settling cancels the other two.
  'src/lib/shell/conversation/conversationMessages.ts:new Promise:await new Promise<void>((resolve) => {',
  'src/lib/shell/conversation/conversationMessages.ts:cancelAnimationFrame:cancelAnimationFrame(frame);',
  'src/lib/shell/conversation/conversationMessages.ts:requestAnimationFrame:const frame = requestAnimationFrame(done);',
  'src/lib/shell/conversation/conversationMessages.ts:setTimeout:const timer = setTimeout(done, 50);',
  // TSK-1424 B: the chat's readiness gate, resolved when its snapshot is consumed. Leaving the
  // session (disposeConversationChat) and a failed stream reject it.
  'src/lib/shell/conversation/conversationConnection.ts:new Promise:selection.ready = new Promise<void>((resolve, reject) => {',
  // TSK-1424 B: a send's admission, resolved by the backend receipt mid-turn. The send's failure, or
  // its end without a receipt, rejects it; leaving the session does not cancel a send already made.
  'src/lib/shell/conversation/conversationConnection.ts:new Promise:const promise = new Promise<AgentConversationSendReceipt>((accept, decline) => {',
  // TSK-1424 A: scroll work that must wait for the next render inside an effect, where flushSync throws.
  // Each waiting function takes the timeline's signal, which unmounting aborts, and checks it and the
  // render window after the tick. Click handlers use flushSync instead.
  "src/lib/shell/conversation/components/ConversationTimeline.svelte:tick:import { flushSync, onDestroy, tick, untrack, setContext } from 'svelte';",
  'src/lib/shell/conversation/components/ConversationTimeline.svelte:tick:await tick();',
  // TSK-1424 A: the background line's one-second clock. It runs only while the line is on screen and the
  // window is visible; going off screen, hiding the window or the action's destroy on unmount clears it.
  'src/lib/shell/conversation/components/ConversationTimeline.svelte:setInterval:let timer: ReturnType<typeof setInterval> | null = null;',
  'src/lib/shell/conversation/components/ConversationTimeline.svelte:setInterval:timer = setInterval(() => { backgroundNow = Date.now(); }, 1000);',
  // TSK-1563: scrolling saves the reader's place and checks for paging once a frame instead of per event.
  // One pending frame at a time; unmounting cancels it, and the work it runs checks the render window.
  'src/lib/shell/conversation/components/ConversationTimeline.svelte:requestAnimationFrame:viewFrame ||= requestAnimationFrame(() => { viewFrame = 0; saveView(); requestPage(frameOlder); });',
  'src/lib/shell/conversation/components/ConversationTimeline.svelte:cancelAnimationFrame:onDestroy(() => { stop.abort(); cancelAnimationFrame(viewFrame); });',
  // TSK-1424 A: a running tool group's first scroll to its newest row waits for the first render inside an
  // effect; unmounting aborts the group's signal, which the wait checks after the tick.
  "src/lib/shell/conversation/components/ConversationToolGroup.svelte:tick:import { getContext, onDestroy, setContext, tick, untrack } from 'svelte';",
  'src/lib/shell/conversation/components/ConversationToolGroup.svelte:tick:await tick();'
]);

interface Hit {
  file: string;
  line: number;
  category:
    | 'scheduler'
    | 'promise-chain'
    | 'promise-wrapper'
    | 'anonymous-async-iife'
    | 'non-async-promise-implementation'
    | 'css-has-selector'
    | 'tailwind-has-variant'
    | 'endless-animation';
  term: string;
  text: string;
}

function isSourceFile(path: string): boolean {
  return includedExtensions.some((extension) => path.endsWith(extension));
}

function isProductionPath(path: string): boolean {
  if (excludedFileSuffixes.some((suffix) => path.endsWith(suffix))) return false;
  const parts = path.split(sep);
  return !parts.some((part) => excludedPathSegments.has(part));
}

function files(dir: string): string[] {
  const out: string[] = [];
  for (const entry of readdirSync(dir)) {
    const path = join(dir, entry);
    const stats = statSync(path);
    if (stats.isDirectory()) out.push(...files(path));
    else if (isSourceFile(path) && isProductionPath(relative(srcRoot, path))) out.push(path);
  }
  return out;
}

function scanFile(path: string): Hit[] {
  const rel = relative(root, path);
  const source = readFileSync(path, 'utf8');
  return scanSource(rel, source);
}

function scanStagedFiles(): { count: number; hits: Hit[] } {
  const paths = execFileSync(
    'git',
    ['diff', '--cached', '--name-only', '--diff-filter=ACMR', '-z', '--', srcPrefix],
    { cwd: repoRoot, encoding: 'utf8' }
  ).split('\0').filter(Boolean).filter((path) => {
    const rel = path.slice(`${srcPrefix}/`.length);
    return isSourceFile(path) && isProductionPath(rel);
  }).sort();
  return {
    count: paths.length,
    hits: paths.flatMap((path) => {
      const rel = relative(root, join(repoRoot, path));
      const source = execFileSync('git', ['show', `:${path}`], { cwd: repoRoot, encoding: 'utf8' });
      return scanSource(rel, source);
    })
  };
}

export function scanSource(rel: string, source: string): Hit[] {
  const cssHits = scanCssText(rel, source);
  if (rel.endsWith('.css')) return cssHits;
  return [
    ...scanText(rel, source, 0),
    ...scanConcretePromiseImplementations(rel, source),
    ...cssHits
  ];
}

function scanText(rel: string, source: string, lineOffset: number): Hit[] {
  const lines = source.split(/\r?\n/);
  const hits: Hit[] = [];
  for (let index = 0; index < lines.length; index += 1) {
    const text = lines[index];
    schedulerPattern.lastIndex = 0;
    let match: RegExpExecArray | null;
    while ((match = schedulerPattern.exec(text)) !== null) {
      if (match[1] === 'tick' && !/\btick\s*\(/.test(text) && !/\bimport\s+\{[^}]*\btick\b/.test(text)) continue;
      hits.push({ file: rel, line: lineOffset + index + 1, category: 'scheduler', term: match[1], text: text.trim() });
    }
    promiseChainPattern.lastIndex = 0;
    while ((match = promiseChainPattern.exec(text)) !== null) {
      hits.push({ file: rel, line: lineOffset + index + 1, category: 'promise-chain', term: `promise.${match[1]}`, text: text.trim() });
    }
    promiseWrapperPattern.lastIndex = 0;
    while ((match = promiseWrapperPattern.exec(text)) !== null) {
      hits.push({ file: rel, line: lineOffset + index + 1, category: 'promise-wrapper', term: match[1].replace(/\s+/, ' '), text: text.trim() });
    }
    anonymousAsyncIifePattern.lastIndex = 0;
    while ((match = anonymousAsyncIifePattern.exec(text)) !== null) {
      hits.push({ file: rel, line: lineOffset + index + 1, category: 'anonymous-async-iife', term: match[1].replace(/\s+/, ' '), text: text.trim() });
    }
  }
  return hits;
}

function scanCssText(rel: string, source: string): Hit[] {
  const withoutComments = source.replace(/<!--[\s\S]*?-->|\/\*[\s\S]*?\*\//g, (comment) => comment.replace(/[^\r\n]/g, ' '));
  const lines = withoutComments.split(/\r?\n/);
  const hits: Hit[] = [];
  for (let index = 0; index < lines.length; index += 1) {
    const text = lines[index];
    for (const [pattern, category, term] of [
      [cssHasPattern, 'css-has-selector', ':has()'],
      [tailwindHasPattern, 'tailwind-has-variant', 'has-*']
    ] as const) {
      pattern.lastIndex = 0;
      if (pattern.test(text)) hits.push({ file: rel, line: index + 1, category, term, text: text.trim() });
    }
  }
  // A declaration can wrap across lines, so the loop check reads the whole text and reports the line it starts on.
  for (const match of withoutComments.matchAll(endlessAnimationPattern)) {
    const line = withoutComments.slice(0, match.index).split(/\r?\n/).length;
    hits.push({ file: rel, line, category: 'endless-animation', term: 'infinite', text: lines[line - 1].trim() });
  }
  return hits;
}

function scriptBlocks(source: string): { text: string; lineOffset: number }[] {
  const blocks: { text: string; lineOffset: number }[] = [];
  const pattern = /<script\b[^>]*>([\s\S]*?)<\/script>/gi;
  let match: RegExpExecArray | null;
  while ((match = pattern.exec(source)) !== null) {
    blocks.push({
      text: match[1],
      lineOffset: source.slice(0, match.index).split(/\r?\n/).length - 1
    });
  }
  return blocks;
}

function parseBlocks(rel: string, source: string): { parsed: ts.SourceFile; lineOffset: number; text: string }[] {
  const blocks = rel.endsWith('.svelte') ? scriptBlocks(source) : [{ text: source, lineOffset: 0 }];
  return blocks.map((block) => ({
    ...block,
    parsed: ts.createSourceFile(`${rel}.ts`, block.text, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS)
  }));
}

function hasAsyncModifier(node: ts.Node): boolean {
  return Boolean(ts.canHaveModifiers(node) && ts.getModifiers(node)?.some((modifier) => modifier.kind === ts.SyntaxKind.AsyncKeyword));
}

function promiseReturn(type: ts.TypeNode | undefined): boolean {
  if (!type || ts.isUnionTypeNode(type)) return false;
  if (ts.isParenthesizedTypeNode(type)) return promiseReturn(type.type);
  return ts.isTypeReferenceNode(type) && type.typeName.getText() === 'Promise';
}

function implementationName(node: ts.Node): string {
  if ((ts.isFunctionDeclaration(node) || ts.isMethodDeclaration(node)) && node.name) return node.name.getText();
  if (ts.isArrowFunction(node) && ts.isVariableDeclaration(node.parent) && ts.isIdentifier(node.parent.name)) return node.parent.name.text;
  return 'anonymous';
}

function scanConcretePromiseImplementations(rel: string, source: string): Hit[] {
  const hits: Hit[] = [];
  for (const block of parseBlocks(rel, source)) {
    const visit = (node: ts.Node): void => {
      const concrete =
        (ts.isFunctionDeclaration(node) || ts.isMethodDeclaration(node) || ts.isArrowFunction(node))
        && Boolean(node.body)
        && !hasAsyncModifier(node)
        && promiseReturn(node.type);
      if (concrete) {
        const { line } = block.parsed.getLineAndCharacterOfPosition(node.getStart(block.parsed));
        const text = block.text.split(/\r?\n/)[line]?.trim() ?? '';
        hits.push({
          file: rel,
          line: block.lineOffset + line + 1,
          category: 'non-async-promise-implementation',
          term: implementationName(node),
          text
        });
      }
      ts.forEachChild(node, visit);
    };
    visit(block.parsed);
  }
  return hits;
}

function run(): void {
  const staged = process.argv.includes('--staged');
  const sourceFiles = staged ? [] : files(srcRoot).sort();
  const scan = staged ? scanStagedFiles() : { count: sourceFiles.length, hits: sourceFiles.flatMap(scanFile) };
  const hits = scan.hits.filter((hit) =>
    hit.category === 'endless-animation'
    || hit.category.startsWith('css-')
    || hit.category === 'tailwind-has-variant'
    || !allowedHits.has(`${hit.file}:${hit.term}:${hit.text}`)
  );
  const count = (category: Hit['category']): number => hits.filter((hit) => hit.category === category).length;

  console.log(`production source files scanned: ${scan.count}`);
  console.log(`mode: ${staged ? 'staged index' : 'working tree'}`);
  console.log(`scope: src/**/*.ts, src/**/*.svelte, src/**/*.css`);
  console.log(`excluded: ${excludedFileSuffixes.join(', ')}; path segments ${[...excludedPathSegments].join(', ')}`);
  console.log(`scheduler hits: ${count('scheduler')}`);
  console.log(`promise chain hits: ${count('promise-chain')}`);
  console.log(`promise wrapper hits: ${count('promise-wrapper')}`);
  console.log(`anonymous async IIFE hits: ${count('anonymous-async-iife')}`);
  console.log(`non-async Promise implementation hits: ${count('non-async-promise-implementation')}`);
  console.log(`CSS :has() hits: ${count('css-has-selector')}`);
  console.log(`Tailwind has variant hits: ${count('tailwind-has-variant')}`);
  console.log(`endless animation hits: ${count('endless-animation')}`);
  console.log(`total prohibited hits: ${hits.length}`);
  for (const hit of hits) {
    console.log(`${hit.file}:${hit.line}: ${hit.category}:${hit.term}: ${hit.text}`);
  }

  if (hits.length > 0) process.exitCode = 1;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) run();
