/**
 * sourceEditorPanels.ts — pure helpers for the per-file editor Dockview panels.
 *
 * The source editor renders one nested Dockview tab per open file; each tab's
 * panel is keyed by a `file:${path}` id. These helpers convert between an open
 * tab / file path and that panel id, and derive the tab's display title.
 *
 * Pure by design: NO runes, NO store imports, NO Tauri. Shared by both
 * `+page.svelte` and `EditorPanel.svelte` so the panel-id encoding lives in one
 * place (and the page↔component import stays one-directional). The dirty-marker
 * lookup is passed in as a flag rather than imported, keeping this module pure —
 * callers pass `isSourcePathDirty(tab.path)`.
 */
import type { SourceOpenTab } from '$lib/sourceData';

export type SourceEditorFilePanelID = `file:${string}`;

export function sourceEditorFilePanelID(tab: Pick<SourceOpenTab, 'path'>): SourceEditorFilePanelID {
  return sourceEditorFilePanelIDFromPath(tab.path);
}

export function sourceEditorFilePanelIDFromPath(path: string): SourceEditorFilePanelID {
  return `file:${path}` as SourceEditorFilePanelID;
}

export function sourceEditorFilePathFromPanelID(panelID: SourceEditorFilePanelID): string {
  return panelID.slice('file:'.length);
}

export function sourceEditorFileDockviewTitle(tab: SourceOpenTab, isDirty: boolean): string {
  return `${isDirty ? '* ' : ''}${tab.fileName}`;
}
