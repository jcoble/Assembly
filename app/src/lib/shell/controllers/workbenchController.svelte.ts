/**
 * Controller for the drawer tab, the tab pane's place in the frame, and frame
 * layout. Which top tab is showing lives in `topTabs`.
 */
import { DEFAULT_RIGHT_TAB } from '../layout/workbenchTabs';
import { topTabs } from '../layout/topTabs.svelte';
import { parseTopTabKey, type TopTabKey } from '../layout/topTabsOps';
import type { RightTabId } from '../workbenchNavigation';
import {
	DEFAULT_DIFF_MODE,
	type DiffMode,
	type SessionWorkspaceSnapshot,
} from '../sessionWorkspaces';
import { gitService } from '../git/gitService';
import { gitCommitFilesService } from '../git/gitCommitFilesService';
import { gitPanel } from '../git/gitPanelStore.svelte';
import { shellPanels } from '../shellPanels';
import {
	CENTER_MIN_WIDTH,
	DOCK_HEIGHT,
	SESSIONS_MAX_WIDTH,
	SESSIONS_MIN_WIDTH,
	SESSIONS_STRIP_WIDTH,
	SESSIONS_WIDTH,
	TOOLS_MAX_WIDTH,
	TOOLS_MIN_WIDTH,
	type RegionHeightLimits,
	type RegionWidthLimits,
	type ShellRegionId,
} from '../layout/frame';
import { settings, type ProblemsLocation } from '../settings/settingsStore.svelte';
import { writeAssemblySettingFromTauri } from '../../tauriSource';
import {
	captureBrowserState,
	releaseBrowserWorkspace,
	restoreBrowserState,
} from '../browser/browserStore.svelte.ts';

const SESSIONS_COLLAPSED_KEY = 'shell.sessions.collapsed';

/** Drawer tabs that live behind its ⋯ menu; one at a time gets a tab of its own. */
export type RightExtraTabId = 'worktrees' | 'terminal' | 'run' | 'context' | 'history';

function isRightExtraTab(id: RightTabId): id is RightExtraTabId {
	return id === 'worktrees' || id === 'terminal' || id === 'run' || id === 'context' || id === 'history';
}

export interface FrameControls {
	resetLayout(): void;
	setRegionWidth(id: ShellRegionId, width: number, limits?: RegionWidthLimits): void;
	setRegionHeight(id: ShellRegionId, height: number, limits?: RegionHeightLimits): void;
	setDockPresent(present: boolean): void;
	setToolsPresent(present: boolean): void;
	setToolsExpanded(expanded: boolean): void;
	setRegionLimits(id: ShellRegionId, limits: RegionWidthLimits): void;
	regionWidth(id: ShellRegionId): number | null;
}

export class WorkbenchController {
	rightTab = $state<RightTabId>(DEFAULT_RIGHT_TAB);
	/** The ⋯-menu tab shown in the drawer's tab row, replaced by the next pick.
	 * Memory only: on restore it follows `rightTab`. */
	rightExtraTab = $state<RightExtraTabId | null>(null);
	diffMode = $state<DiffMode>(DEFAULT_DIFF_MODE);
	sessionsCollapsed = $state(false);
	/** The right drawer. Closed at launch; it pushes the frame narrower
	 * beside it and never changes the grid's arrangement. */
	rightPanelOpen = $state(false);
	/** The tab pane widened over the chat; never saved, cleared when the pane goes. */
	expanded = $state(false);
	/** True between `beginSessionSwitch` and `restoreSessionState`, so the pane
	 * is not removed and re-added while the next session's tabs arrive. */
	switching = $state(false);
	/** The `+`-menu tab the drawer closed for; leaving that tab opens the drawer again. */
	private drawerYieldedTo: TopTabKey | null = null;

	private frameControls: FrameControls | null = null;
	private restoringTabs = false;

	onFrameReady(controls: FrameControls): void {
		this.frameControls = controls;
		controls.setRegionLimits('tools', {
			minimumWidth: TOOLS_MIN_WIDTH,
			maximumWidth: TOOLS_MAX_WIDTH,
		});
		controls.setRegionLimits('center', { minimumWidth: CENTER_MIN_WIDTH });
		controls.setToolsPresent(topTabs.row.length > 0);
		this.applyProblemsLocation(settings.panels.problemsLocation);
		if (this.sessionsCollapsed) {
			this.applySessionsWidth(true);
		} else {
			controls.setRegionLimits('sessions', {
				minimumWidth: SESSIONS_MIN_WIDTH,
				maximumWidth: SESSIONS_MAX_WIDTH,
			});
			const restored = controls.regionWidth('sessions');
			if (restored !== null && restored < SESSIONS_MIN_WIDTH) {
				controls.setRegionWidth('sessions', SESSIONS_WIDTH);
			}
		}
	}

	/** Put the tab pane in the grid or take it out. */
	setPanePresent(present: boolean): void {
		if (!present) this.expanded = false;
		this.frameControls?.setToolsPresent(present);
	}

	setExpanded(expanded: boolean): void {
		this.expanded = expanded;
		this.frameControls?.setToolsExpanded(expanded);
	}

	/** What is in front: a pane tab, or the chat when no tab is open (null). */
	paneShown(key: TopTabKey | null): void {
		if (key !== this.drawerYieldedTo) this.giveDrawerBack();
		shellPanels.panelShown(key ? parseTopTabKey(key)?.kind ?? 'session' : 'session');
		this.syncGitSurfaceVisibility();
	}

	selectRightTab(id: RightTabId): void {
		this.rightTab = id;
		if (isRightExtraTab(id)) this.rightExtraTab = id;
		this.setRightPanelOpen(true);
	}

	toggleRightPanel(): void {
		this.setRightPanelOpen(!this.rightPanelOpen);
	}

	/** A wide tab opened from the `+` menu takes the drawer's room until the user leaves it. */
	yieldDrawerTo(key: TopTabKey | null): void {
		if (key === null || key.startsWith('editor:')) return;
		if (!this.rightPanelOpen && this.drawerYieldedTo === null) return;
		this.setRightPanelOpen(false);
		this.drawerYieldedTo = key;
	}

	/** The chat is chosen: the pane stops covering it, and a yielded drawer comes back. */
	showChat(): void {
		if (this.expanded) this.setExpanded(false);
		this.giveDrawerBack();
	}

	private giveDrawerBack(): void {
		if (this.drawerYieldedTo !== null) this.setRightPanelOpen(true);
	}

	setRightPanelOpen(open: boolean): void {
		this.drawerYieldedTo = null;
		this.rightPanelOpen = open;
		this.syncRightPanelVisibility();
		this.syncGitSurfaceVisibility();
	}

	private adoptRightTab(id: RightTabId): void {
		this.rightTab = id;
		this.rightExtraTab = isRightExtraTab(id) ? id : null;
		this.syncRightPanelVisibility();
		this.syncGitSurfaceVisibility();
	}

	setDiffMode(mode: DiffMode): void {
		this.diffMode = mode;
	}

	captureSessionState(): Partial<SessionWorkspaceSnapshot> {
		return {
			rightTab: this.rightTab,
			diffMode: this.diffMode,
			diffPath: gitPanel.selectedPath || null,
			diffRoot: gitPanel.selectedPath ? gitPanel.root : null,
			browser: captureBrowserState(),
			topTabs: topTabs.capture(),
		};
	}

	/** Remove outgoing lazy surfaces while the next session is being restored. */
	beginSessionSwitch(): void {
		this.giveDrawerBack();
		this.switching = true;
		releaseBrowserWorkspace();
		// The outgoing tabs were already captured; keeping them as remembered
		// tabs would let the incoming session's pane recreate them.
		restoreBrowserState(null);
		// A center-tab round trip keeps the diff ready to return to. A session
		// switch is the ownership boundary where that selection must be released.
		gitService.clearSelection();
		// Let go of the outgoing repository too; whichever git surface is on
		// screen points it at the incoming session's repository as it remounts.
		gitService.activate(null);
		gitCommitFilesService.release();
		this.adoptRightTab(DEFAULT_RIGHT_TAB);
		topTabs.reset();
	}

	restoreSessionState(snapshot: SessionWorkspaceSnapshot | null): void {
		this.restoringTabs = true;
		try {
			this.diffMode = snapshot?.diffMode ?? DEFAULT_DIFF_MODE;
			restoreBrowserState(snapshot?.browser);
			topTabs.restore(snapshot?.topTabs);
			this.adoptRightTab(snapshot?.rightTab ?? DEFAULT_RIGHT_TAB);
		} finally {
			this.restoringTabs = false;
			this.switching = false;
		}
		this.syncGitSurfaceVisibility();
	}

	resetLayout(): void {
		this.expanded = false;
		this.frameControls?.resetLayout();
		// The default arrangement has no pane; put it back if tabs are open.
		this.frameControls?.setToolsPresent(topTabs.row.length > 0);
		this.applyProblemsLocation(settings.panels.problemsLocation);
		this.syncRightPanelVisibility();
		this.syncGitSurfaceVisibility();
	}

	applyProblemsLocation(location: ProblemsLocation): void {
		const atBottom = location === 'bottom';
		this.frameControls?.setDockPresent(atBottom);
		if (atBottom) {
			this.frameControls?.setRegionHeight('dock', DOCK_HEIGHT, {
				minimumHeight: 96,
				maximumHeight: Number.MAX_SAFE_INTEGER,
			});
		}
	}

	showBottomDock(): void {
		settings.panels.problemsLocation = 'bottom';
		this.applyProblemsLocation('bottom');
	}

	collapseSessions(collapsed: boolean): void {
		this.sessionsCollapsed = collapsed;
		this.applySessionsWidth(collapsed);
		void writeAssemblySettingFromTauri(SESSIONS_COLLAPSED_KEY, collapsed ? 'true' : 'false');
	}

	private applySessionsWidth(collapsed: boolean): void {
		if (!this.frameControls) return;
		if (collapsed) {
			this.frameControls.setRegionLimits('sessions', {
				minimumWidth: SESSIONS_STRIP_WIDTH,
				maximumWidth: SESSIONS_STRIP_WIDTH,
			});
			this.frameControls.setRegionWidth('sessions', SESSIONS_STRIP_WIDTH);
		} else {
			this.frameControls.setRegionLimits('sessions', {
				minimumWidth: SESSIONS_MIN_WIDTH,
				maximumWidth: SESSIONS_MAX_WIDTH,
			});
			this.frameControls.setRegionWidth('sessions', SESSIONS_WIDTH);
		}
	}

	private syncGitSurfaceVisibility(): void {
		if (this.restoringTabs) return;
		const graphVisible = topTabs.activeKind === 'git-history' || (this.rightPanelOpen && this.rightTab === 'source-control');
		shellPanels.sourceControlVisible(graphVisible);
		if (!graphVisible) gitService.releaseHistorySurface();
		// Commit expansion and selection belong to the current session, not to the
		// panel that happens to be visible. beginSessionSwitch releases them.
	}

	private syncRightPanelVisibility(): void {
		const visible = this.rightPanelOpen;
		shellPanels.filesVisible(visible && this.rightTab === 'files');
		shellPanels.worktreesVisible(visible && this.rightTab === 'worktrees');
		shellPanels.stacksVisible(visible && this.rightTab === 'run');
	}
}
