import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const client = await readFile(
  new URL('../src/lib/shell/files/editor/csharpLanguageClient.ts', import.meta.url),
  'utf8'
);
const editor = await readFile(
  new URL('../src/lib/shell/files/components/CodeMirrorSourceEditor.svelte', import.meta.url),
  'utf8'
);
const panel = await readFile(
  new URL('../src/lib/shell/files/components/EditorPanel.svelte', import.meta.url),
  'utf8'
);

assert.match(client, /from ['"]@codemirror\/lsp-client['"]/);
assert.match(client, /formatKeymap/);
assert.match(client, /renameKeymap/);
assert.match(client, /jumpToDefinitionKeymap/);
assert.match(client, /findReferencesKeymap/);
assert.match(client, /signatureHelp\(\)/);
assert.match(
  client,
  /new LSPClient\([\s\S]*serverCompletion\(\{ override: true \}\)[\s\S]*hoverTooltips\(\)[\s\S]*keymap\.of\(\[\.\.\.formatKeymap, \.\.\.renameKeymap, \.\.\.jumpToDefinitionKeymap, \.\.\.findReferencesKeymap\]\)[\s\S]*signatureHelp\(\)[\s\S]*serverDiagnostics\(\)/,
  'the official client should own completion, hover, formatting, rename, definition, references, signature help, and diagnostics'
);
assert.match(client, /new WebSocket\(endpoint\.wsUrl\)/);
assert.match(client, /workspaceFolders: \[\{/);
assert.match(client, /socket\.send\(withCsharpWorkspaceFolder\(message, requestedRoot\)\)/);
assert.match(
  client,
  /new WebSocket\(endpoint\.wsUrl\)[\s\S]*await new Promise<void>[\s\S]*addEventListener\('open'[\s\S]*new LSPClient/,
  'the official client should wait for the Roslyn WebSocket to open before sending initialization'
);
assert.match(client, /ensureNativeCsharpLanguageClientFromTauri\(requestedRoot\)/);
assert.match(client, /markNativeCsharpLanguageClientReadyFromTauri\(requestedRoot\)/);
assert.match(client, /'textDocument\/references'/);
assert.match(client, /export async function findCsharpReferenceLocations/);
assert.match(client, /activeSession\?\.dispose\(\)/);
assert.match(client, /client\?\.disconnect\(\)/);
assert.match(client, /socket\?\.close\(\)/);
assert.doesNotMatch(client, /monaco|LanguageClientWrapper|fallback/i);
assert.doesNotMatch(client, /(?:document|result)(?:Cache|Map|State)/i);

assert.match(editor, /languageServerRoot/);
assert.match(editor, /connectCodeMirrorCsharpClient\(root, onLanguageServerReady\)/);
assert.match(editor, /intelligence\.reconfigure\(extension\)/);
assert.match(client, /markNativeCsharpLanguageClientReadyFromTauri\(requestedRoot\)[\s\S]*onReady\?\.\(\)/);
assert.match(
  editor,
  /key: 'F12'[\s\S]*if \(officialLspExpected\(\)\) return false;[\s\S]*navigate\('definition'\)/,
  'official C# LSP should receive F12 before callback intelligence'
);
assert.match(
  editor,
  /key: 'Shift-F12'[\s\S]*if \(officialLspExpected\(\)\) return false;[\s\S]*peekReferences\(\)/,
  'official C# LSP should receive Shift-F12 before callback intelligence'
);
assert.match(editor, /function clearLspSupport\(disposeClient = true\)/);
assert.match(
  editor,
  /function showFile\(\)[\s\S]*clearLspSupport\(false\);[\s\S]*rememberCurrentView\(\)/,
  'file exit should remove the prior document plugin before retaining editor state'
);
assert.match(
  editor,
  /function clearLspSupport\(disposeClient = true\)[\s\S]*intelligence\.reconfigure\(callbackIntelligence\)/,
  'file switches should reconfigure the document overlay without disconnecting the root client'
);
assert.match(
  editor,
  /button\.addEventListener\('mousedown', preventMouseDown\)[\s\S]*button\.addEventListener\('click', runAction\)[\s\S]*destroy: \(\) => \{[\s\S]*button\.removeEventListener\('mousedown', preventMouseDown\)[\s\S]*button\.removeEventListener\('click', runAction\)/,
  'CodeMirror code-action tooltip listeners should be removed when the tooltip is destroyed'
);
assert.match(
  editor,
  /catch \{[\s\S]*generation !== lspGeneration \|\| requestedLspKey !== key[\s\S]*intelligence\.reconfigure\(callbackIntelligence\)[\s\S]*setDiagnostics\(view\.state, diagnosticsFor\(view\.state\)\)/,
  'a current failed official client load should restore callback intelligence and diagnostics'
);
assert.match(editor, /if \(visible\)[\s\S]*loadVisibleLspSupport\(\)[\s\S]*else \{[\s\S]*clearLspSupport\(\)/);
assert.match(panel, /languageServerRoot=\{activeLanguageServerRoot\}/);
assert.match(panel, /onLanguageServerReady=\{handleLanguageServerReady\}/);
assert.match(panel, /state: 'ready'/);

console.log('official CodeMirror C# language client contract tests passed');
