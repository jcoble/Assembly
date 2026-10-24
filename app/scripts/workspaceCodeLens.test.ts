import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import {
  dotnetWorkspaceCommandIds,
  dotnetWorkspaceLensTitles,
  dotnetWorkspaceSessionRequest
} from '../src/lib/workspaceCodeLens.ts';

assert.deepEqual(dotnetWorkspaceCommandIds, {
  build: 'mcb.source.dotnetBuild',
  test: 'mcb.source.dotnetTest'
});
assert.deepEqual(dotnetWorkspaceLensTitles, {
  build: 'Build workspace',
  test: 'Test workspace'
});

assert.deepEqual(dotnetWorkspaceSessionRequest('build', '/work/My App'), {
  id: 'dotnet-build:/work/My App',
  cwd: '/work/My App',
  script: 'dotnet build --nologo',
  title: 'Build My App'
});

assert.deepEqual(dotnetWorkspaceSessionRequest('test', '/work/My App/'), {
  id: 'dotnet-test:/work/My App/',
  cwd: '/work/My App/',
  script: 'dotnet test --nologo',
  title: 'Test My App'
});

// The path is data, not shell text. A hostile-looking folder cannot alter the
// fixed command line the Rust terminal session executes.
assert.equal(
  dotnetWorkspaceSessionRequest('build', '/work/Project; touch nope').script,
  'dotnet build --nologo'
);

assert.throws(
  () => dotnetWorkspaceSessionRequest('test', '   '),
  /needs a project folder/
);

const codeMirrorLensSource = await readFile(
  new URL('../src/lib/shell/files/editor/codeMirrorCodeLens.ts', import.meta.url),
  'utf8'
);
const editorPanelSource = await readFile(
  new URL('../src/lib/shell/files/components/EditorPanel.svelte', import.meta.url),
  'utf8'
);
const codeMirrorEditorSource = await readFile(
  new URL('../src/lib/shell/files/components/CodeMirrorSourceEditor.svelte', import.meta.url),
  'utf8'
);

assert.match(
  codeMirrorLensSource,
  /preview\.language\.toLowerCase\(\) === 'csharp'[\s\S]*?dotnetWorkspaceLensTitles\.build/,
  'Build and Test lenses must appear only on C# documents'
);
assert.match(
  editorPanelSource,
  /dotnetWorkspaceSessionRequest\(action, root\)/,
  'the active editor workspace must provide the command working directory'
);
assert.match(
  editorPanelSource,
  /onDotnetBuildRequest=\{onStartWorkspaceCommand \? \(\) => runDotnetWorkspace\('build'\) : undefined\}/,
  'the editor must not expose an inert Build workspace action without a command owner'
);
assert.match(
  codeMirrorEditorSource,
  /onDotnetAction: onDotnetBuildRequest && onDotnetTestRequest[\s\S]*?: undefined/,
  'CodeMirror must omit workspace lenses when their command callbacks are unavailable'
);
assert.match(
  codeMirrorLensSource,
  /showCodeMirrorReferences = StateEffect\.define<SourceLookupRequest>\(\)/,
  'the editor context menu must be able to open the existing inline reference viewer'
);
assert.match(
  codeMirrorEditorSource,
  /editorMenuReferenceRequest = lookupRequestAt\(editor\.state\)[\s\S]*?onSelect=\{\(\) => peekReferences\(editorMenuReferenceRequest\)\}/,
  'Peek References must use the symbol captured at the right-click location after the menu takes focus'
);
assert.match(
  codeMirrorEditorSource,
  /function peekReferences\(capturedRequest\?: SourceLookupRequest \| null\): void \{[\s\S]*?capturedRequest \?\? lookupRequestAt\(editor\.state\)[\s\S]*?showCodeMirrorReferences\.of\(request\)/,
  'Peek References must reuse the installed CodeLens reference viewer with the captured request'
);
assert.match(
  codeMirrorEditorSource,
  /key: 'Shift-F12',[\s\S]*?peekReferences\(\)/,
  'the non-LSP Find References shortcut must open the same inline reference viewer'
);
assert.match(
  codeMirrorLensSource,
  /setCodeMirrorLensRows\.of\(this\.rows\)/,
  'asynchronous reference results must dispatch into editor state so block CodeLens rows repaint safely'
);
assert.match(
  codeMirrorLensSource,
  /create: \(state\) => lensDecorations\(state, initialRows\)/,
  'initial CodeLens rows must be created with editor state instead of dispatching during view construction'
);
assert.doesNotMatch(
  codeMirrorLensSource,
  /requestMeasure\([\s\S]{0,300}?this\.paint\(\)/,
  'CodeLens must not dispatch an editor update from a CodeMirror measurement callback'
);
assert.match(
  codeMirrorLensSource,
  /effect\.is\(showCodeMirrorReferences\)[\s\S]*?if \(this\.alive\) void this\.openReferences\(request\)/,
  'Peek References must hand the captured request to its owned view plugin'
);
assert.match(
  codeMirrorLensSource,
  /private async openReferences[\s\S]*?await options\.onReferences\?\.\(request\)[\s\S]*?this\.view\.dispatch/,
  'Peek References must await the language-server answer before dispatching its panel refresh'
);
assert.doesNotMatch(
  codeMirrorLensSource,
  /effect\.is\(showCodeMirrorReferences\)[\s\S]{0,500}?requestMeasure/,
  'Peek References must not dispatch from CodeMirror DOM measurement callbacks'
);
assert.match(
  codeMirrorLensSource,
  /function peekDecorations\([\s\S]*?block: true[\s\S]*?const peekState = StateField\.define<[\s\S]*?provide: \(field\) => EditorView\.decorations\.from\(field/,
  'Peek References must own its block decoration in editor state so CodeMirror can measure vertical layout'
);
assert.match(
  codeMirrorLensSource,
  /destroy\(\): void \{[\s\S]*?this\.previewView\?\.destroy\(\)/,
  'closing or replacing Peek must destroy its nested source editor'
);

console.log('workspace CodeLens tests passed');
