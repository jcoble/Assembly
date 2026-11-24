/**
 * Shared workbench layout state matching the old SvelteKit app 1:1:
 * - Left Sessions Rail (collapsible)
 * - Live native GPUIKit Chat (`#native-chat-slot`) always visible on the left of center
 * - Optional split Secondary Chrome Tab pane beside Chat (empty until `+` or file/diff/PR opens one)
 * - Right Panel with 4 visible core icons (`files`, `git`, `agents`, `tasks`) + `...` overflow
 *   menu (`worktrees`, `terminal`, `run`, `context`, `history`) + close button
 */

import {
  RIGHT_CORE_TABS,
  RIGHT_OVERFLOW_TABS,
  type RightCoreTabId,
  type RightOverflowTabId,
  type RightPanelTabId,
  type SecondaryTab,
  type SecondaryTabKind
} from '$lib/domain/types';

export {
  RIGHT_CORE_TABS,
  RIGHT_OVERFLOW_TABS,
  type RightCoreTabId,
  type RightOverflowTabId,
  type RightPanelTabId,
  type SecondaryTab,
  type SecondaryTabKind
};

export type UtilityPopoverId = 'resources' | 'usage';

class WorkspaceStore {
  railOpen = $state<boolean>(true);
  railWidth = $state<number>(268);

  /** Secondary chrome tabs opened beside live GPUIKit Chat via `+` or clicking a file/diff/PR. */
  secondaryTabs = $state<SecondaryTab[]>([]);
  activeSecondaryTabId = $state<string | null>(null);
  secondaryExpanded = $state<boolean>(false);
  /** Fractional width of live GPUIKit Chat when a secondary split tab is open beside it. */
  chatSplitRatio = $state<number>(0.46);

  /** Full-center modal/page overlays like Home/Projects/Settings/Board when triggered from Home. */
  homeOverlay = $state<'none' | 'projects' | 'settings' | 'board'>('none');

  /** Active bottom-bar utility popover (`resources` or `usage`). */
  openUtility = $state<UtilityPopoverId | null>(null);

  rightPanelOpen = $state<boolean>(true);
  rightPanelWidth = $state<number>(318);
  activeRightTab = $state<RightPanelTabId>('files');

  get hasSecondaryPane(): boolean {
    return this.secondaryTabs.length > 0 && this.activeSecondaryTabId !== null;
  }

  get activeSecondaryTab(): SecondaryTab | null {
    if (!this.activeSecondaryTabId) return null;
    return this.secondaryTabs.find((t) => t.id === this.activeSecondaryTabId) ?? null;
  }

  setRailWidth(width: number): void {
    this.railWidth = Math.round(Math.min(540, Math.max(210, width)));
  }

  setRightPanelWidth(width: number): void {
    this.rightPanelWidth = Math.round(Math.min(720, Math.max(240, width)));
  }

  setChatSplitRatio(ratio: number): void {
    this.chatSplitRatio = Math.min(0.75, Math.max(0.25, ratio));
  }

  toggleUtility(id: UtilityPopoverId): void {
    this.openUtility = this.openUtility === id ? null : id;
  }

  closeUtility(): void {
    this.openUtility = null;
  }

  openSecondaryTab(kind: SecondaryTabKind, title: string, filePath?: string): void {
    this.homeOverlay = 'none';
    const existing = this.secondaryTabs.find(
      (t) => t.kind === kind && (filePath ? t.filePath === filePath : true)
    );
    if (existing) {
      this.activeSecondaryTabId = existing.id;
      return;
    }
    const id = `${kind}:${filePath ?? Date.now()}`;
    this.secondaryTabs = [...this.secondaryTabs, { id, kind, title, filePath }];
    this.activeSecondaryTabId = id;
  }

  selectSecondaryTab(id: string): void {
    this.homeOverlay = 'none';
    this.activeSecondaryTabId = id;
  }

  closeSecondaryTab(id: string): void {
    const idx = this.secondaryTabs.findIndex((t) => t.id === id);
    if (idx < 0) return;
    const next = this.secondaryTabs.filter((t) => t.id !== id);
    this.secondaryTabs = next;
    if (this.activeSecondaryTabId === id) {
      this.activeSecondaryTabId = next[Math.min(idx, next.length - 1)]?.id ?? null;
    }
    if (next.length === 0) {
      this.secondaryExpanded = false;
    }
  }

  toggleSecondaryExpanded(): void {
    this.secondaryExpanded = !this.secondaryExpanded;
  }

  openHomeOverlay(view: 'none' | 'projects' | 'settings' | 'board'): void {
    this.homeOverlay = view;
  }

  selectRightTab(tab: RightPanelTabId): void {
    this.activeRightTab = tab;
    this.rightPanelOpen = true;
  }

  toggleRightPanel(): void {
    this.rightPanelOpen = !this.rightPanelOpen;
  }

  closeRightPanel(): void {
    this.rightPanelOpen = false;
  }
}

export const workspaceStore = new WorkspaceStore();
