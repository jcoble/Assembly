/**
 * topTabs.svelte.ts — the one store for the top tab row.
 *
 * It holds only the order and the chosen tab. Editor files and browser pages
 * are read live from their own stores, so the row can never list a file that
 * is closed or miss one that was opened elsewhere. The rules live in
 * `topTabsOps.ts`; this file only makes them reactive.
 */
import { captureBrowserState } from '../browser/browserStore.svelte.ts';
import { editorState } from '../files/editor/editorStore.svelte.ts';
import {
  closeTopTab,
  openTopTab,
  parseTopTabKey,
  resolveActiveKey,
  topTabRow,
  type LiveTabSources,
  type SessionTopTabsWorkspace,
  type TopTabKey,
  type TopTabKind
} from './topTabsOps.ts';

function liveSources(): LiveTabSources {
  return {
    editorPaths: editorState.openFiles.map((file) => file.path),
    browserTabIds: captureBrowserState().tabs.map((tab) => tab.id)
  };
}

class TopTabs {
  order = $state<TopTabKey[]>([]);
  chosen = $state<TopTabKey | null>(null);
  readonly row = $derived(topTabRow(this.order, liveSources()));
  readonly activeKey = $derived(resolveActiveKey(this.row, this.chosen));
  readonly activeKind = $derived<TopTabKind | null>(
    this.activeKey ? parseTopTabKey(this.activeKey)?.kind ?? null : null
  );

  open(key: TopTabKey): void {
    this.order = openTopTab(this.row, key);
    this.chosen = key;
  }

  select(key: TopTabKey): void {
    this.chosen = key;
  }

  forget(key: TopTabKey): void {
    const next = closeTopTab(this.row, this.activeKey, key);
    this.order = next.order;
    this.chosen = next.activeKey;
  }

  capture(): SessionTopTabsWorkspace {
    return { order: [...this.row], activeKey: this.activeKey };
  }

  restore(value: SessionTopTabsWorkspace | undefined): void {
    this.order = value ? [...value.order] : [];
    this.chosen = value?.activeKey ?? null;
  }

  reset(): void {
    this.order = [];
    this.chosen = null;
  }
}

export const topTabs = new TopTabs();
