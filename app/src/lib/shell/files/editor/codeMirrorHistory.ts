import { historyField, redoDepth, undoDepth } from '@codemirror/commands';
import { EditorState, type Extension } from '@codemirror/state';

export const savedHistoryDepthLimit = 5;

type SerializedHistory = {
  done: unknown[];
  undone: unknown[];
};

/** Keep a retained tab state from carrying an unbounded CodeMirror history. */
export function limitSavedEditorHistory(
  state: EditorState,
  extensions: Extension,
  limit = savedHistoryDepthLimit
): EditorState {
  if (undoDepth(state) <= limit && redoDepth(state) <= limit) return state;

  const serialized = state.toJSON({ history: historyField }) as {
    doc: string;
    selection: unknown;
    history: SerializedHistory;
  };
  serialized.history = {
    done: serialized.history.done.slice(-limit),
    undone: serialized.history.undone.slice(-limit)
  };

  return EditorState.fromJSON(serialized, { extensions }, { history: historyField });
}
