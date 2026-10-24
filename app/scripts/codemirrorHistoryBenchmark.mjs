import { TextEncoder } from 'node:util';

import { history, historyField, isolateHistory, undoDepth } from '@codemirror/commands';
import { EditorState } from '@codemirror/state';

import {
  limitSavedEditorHistory,
  savedHistoryDepthLimit
} from '../src/lib/shell/files/editor/codeMirrorHistory.ts';

const encoder = new TextEncoder();
const DOC_BYTES = 1024 * 1024;
const EDIT_COUNT = 100;
const THRESHOLD_BYTES = Math.max(16 * 1024 * 1024, DOC_BYTES * 2);

function bytes(value) {
  return encoder.encode(typeof value === 'string' ? value : JSON.stringify(value)).length;
}

function makeDocument() {
  const line = 'const retainedStateProbe = "CodeMirror history benchmark";\n';
  return line.repeat(Math.ceil(DOC_BYTES / bytes(line))).slice(0, DOC_BYTES);
}

function representativeTabs(editedDocBytes) {
  return [
    { path: '/workspace/src/large.ts', active: true, language: 'typescript', byteCount: editedDocBytes, line: 1, column: 1 },
    { path: '/workspace/src/routes/+page.svelte', active: false, language: 'svelte', byteCount: 184_000, line: 412, column: 9 },
    { path: '/workspace/src/lib/shell/files/editor/editorStore.svelte.ts', active: false, language: 'typescript', byteCount: 41_000, line: 73, column: 3 },
    { path: '/workspace/src/lib/shell/files/components/CodeMirrorSourceEditor.svelte', active: false, language: 'svelte', byteCount: 32_000, line: 419, column: 1 },
    { path: '/workspace/src-tauri/src/lib.rs', active: false, language: 'rust', byteCount: 24_000, line: 28, column: 1 }
  ];
}

function applyRepresentativeEdits(state) {
  const stride = Math.floor(state.doc.length / EDIT_COUNT);
  for (let index = 0; index < EDIT_COUNT; index += 1) {
    const from = Math.min(index * stride, state.doc.length - 24);
    const to = from + 24;
    const insert = `/* edit-${String(index).padStart(3, '0')} */`.padEnd(24, ' ');
    state = state.update({
      changes: { from, to, insert },
      annotations: [isolateHistory.of('full')]
    }).state;
  }
  return state;
}

function measureState(label, extensions, limitSavedHistory = false) {
  const initialDoc = makeDocument();
  let state = applyRepresentativeEdits(EditorState.create({ doc: initialDoc, extensions }));
  if (limitSavedHistory) state = limitSavedEditorHistory(state, extensions);
  const documentBytes = bytes(state.doc.toString());
  const tabStateBytes = bytes(representativeTabs(documentBytes));
  let historyJsonBytes = 0;
  let undoGroups = 0;
  let historyMeasure = 'none; history extension disabled';

  if (extensions.length > 0) {
    const serialized = state.toJSON({ history: historyField }).history;
    historyJsonBytes = bytes(serialized);
    undoGroups = undoDepth(state);
    historyMeasure = 'CodeMirror historyField JSON bytes; proxy, not JS heap bytes';
  }

  return {
    label,
    documentBytes,
    tabStateBytes,
    historyJsonBytes,
    retainedStateProxyBytes: documentBytes + tabStateBytes + historyJsonBytes,
    undoGroups,
    historyMeasure
  };
}

const rows = [
  measureState('history disabled baseline', []),
  measureState(
    'saved CodeMirror state capped at 5 groups',
    [history({ minDepth: savedHistoryDepthLimit })],
    true
  )
];
const baselineRow = rows[0];
const historyRow = rows[1];
const retainedHistoryDeltaBytes = historyRow.retainedStateProxyBytes - baselineRow.retainedStateProxyBytes;
const withinThreshold = retainedHistoryDeltaBytes <= THRESHOLD_BYTES;
const withinDepthLimit = historyRow.undoGroups <= savedHistoryDepthLimit;

console.table(rows.map((row) => ({
  scenario: row.label,
  docBytes: row.documentBytes,
  tabJsonBytes: row.tabStateBytes,
  historyJsonBytes: row.historyJsonBytes,
  retainedProxyBytes: row.retainedStateProxyBytes,
  undoGroups: row.undoGroups
})));

console.log(JSON.stringify({
  editedDocumentBytes: historyRow.documentBytes,
  editGroupsApplied: EDIT_COUNT,
  retainedHistoryDeltaBytes,
  thresholdBytes: THRESHOLD_BYTES,
  withinThreshold,
  savedHistoryDepthLimit,
  withinDepthLimit,
  measurement: rows.map(({ label, historyMeasure }) => ({ label, historyMeasure })),
  conclusion: withinThreshold && withinDepthLimit
    ? 'Saved editor state stays within the five-group undo cap and retained-history byte threshold.'
    : 'Investigate retained editor history before release.'
}, null, 2));
