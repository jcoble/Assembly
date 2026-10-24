<script lang="ts">
	import WorkingSpinner from "$lib/shell/components/feedback/WorkingSpinner.svelte";
		import { dev } from "$app/environment";
	import { Button } from "$lib/components/ui/button/index.js";
	import * as ContextMenu from "$lib/components/ui/context-menu/index.js";
	import { EmptyState } from "$lib/components/ui/empty-state/index.js";
	import { IconButton } from "$lib/components/ui/icon-button/index.js";
	import { Input } from "$lib/components/ui/input/index.js";
	import * as Select from "$lib/components/ui/select/index.js";
	import FileIcon from "$lib/shell/components/explorer/FileIcon.svelte";
	import FileHistoryPane from "./FileHistoryPane.svelte";
	import {
		activate as activateExplorer,
		loadDirectory,
		refresh,
		refreshChangedPath,
		refreshChangedPaths,
		revealExplorerPath,
		scanRoot,
		stopScan,
		unloadDirectory,
	} from "$lib/shell/files/explorer/explorerService";
	import type { ExplorerTreeNode } from "$lib/shell/files/explorer/explorerStore.svelte";
	import {
		canonicalPath,
		explorer,
		explorerNodes,
		loadedExplorerDirectories,
		loadedExplorerDirectoryDepth,
		selectPath,
		setIncludeExcluded,
	} from "$lib/shell/files/explorer/explorerStore.svelte";
		import {
			FILE_TREE_OVERSCAN_ROWS,
			FILE_TREE_ROW_HEIGHT,
			fileHistoryTarget,
			visibleFileTreeNodes,
			windowFileTreeNodes,
		} from "./fileTreeModel.ts";
	import { watchFileTree } from "./fileTreeWatch.ts";
	import { onWorkspaceFileChange, publishWorkspaceFileChange } from "$lib/shell/workspaceFileChangeBus.ts";
	import { nativeWorkspacePath, revealDisabledReason, workspaceChangePath } from "$lib/workspacePaths.ts";
	import { editorState } from "$lib/shell/files/editor/editorStore.svelte";
	import { gitService } from "$lib/shell/git/gitService";
	import { openFileInEditor, openFileTimeline } from "$lib/shell/workbenchNavigation";
	import type { SourceTreeSearchMatch } from "$lib/sourceData";
	import type { RepositoryCheckout } from "$lib/tauriSource";
	import {
		cancelSourceScanFromTauri,
		createSourceScanId,
		isNativeTauriRuntime,
		listRepositoryCheckoutsFromTauri,
		moveToTrashFromTauri,
		searchSourceTreeFromTauri,
		validateProjectRootFromTauri,
	} from "$lib/tauriSource";
	import ChevronDown from "@lucide/svelte/icons/chevron-down";
	import ChevronRight from "@lucide/svelte/icons/chevron-right";
	import ArrowDownAZ from "@lucide/svelte/icons/arrow-down-a-z";
	import ArrowUpZA from "@lucide/svelte/icons/arrow-up-z-a";
	import Eye from "@lucide/svelte/icons/eye";
	import EyeOff from "@lucide/svelte/icons/eye-off";
	import FilePlus from "@lucide/svelte/icons/file-plus";
	import Folder from "@lucide/svelte/icons/folder";
	import FolderOpen from "@lucide/svelte/icons/folder-open";
	import FolderPlus from "@lucide/svelte/icons/folder-plus";
	import FolderTree from "@lucide/svelte/icons/folder-tree";
	import RefreshCw from "@lucide/svelte/icons/refresh-cw";
	import Rows3 from "@lucide/svelte/icons/rows-3";
	import Search from "@lucide/svelte/icons/search";
	import Square from "@lucide/svelte/icons/square";
	import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
	import { onDestroy, tick, untrack } from "svelte";

	interface Props {
		visible: boolean;
		root: string;
		ownedId: string | null;
		onRootUnavailable?(root: string): void | Promise<void>;
		expandedPathsByRoot?: Readonly<Record<string, readonly string[]>>;
		onExpandedPathsChange?(ownedId: string, root: string, paths: readonly string[]): void;
		inspectionRoot?: string | null;
		onInspectionRootChange?(root: string | null): void;
		checkoutDiscoveryRoots?: readonly string[];
		onUseSessionCheckout?(root: string): void | Promise<void>;
		/** Set while another session works in the folder shown: nothing here may change it. */
		readOnlyReason?: string | null;
		/** Raised by the page (the `+` menu's "Open file…") to focus the filter. */
		filterFocusRequest?: number;
		viewing?: boolean; // `root` is a project the session only views, so it is not called "(session)".
	}

	type PendingEntry = {
		kind: "rename" | "new-file" | "new-folder";
		path: string;
	};

	type FileClipboard = {
		operation: "cut" | "copy";
		path: string;
		name: string;
		isDirectory: boolean;
	};

	type TreeItem = ExplorerTreeNode & {
		treePath: string;
		treeParentPath: string;
		relativePath: string;
		hasChildren: boolean;
		expanded: boolean;
		selected: boolean;
		searchResult?: boolean;
	};

	let {
		visible,
		root,
		ownedId,
		onRootUnavailable,
		expandedPathsByRoot,
		onExpandedPathsChange,
		inspectionRoot,
		onInspectionRootChange,
		checkoutDiscoveryRoots = [],
		onUseSessionCheckout,
		readOnlyReason = null,
		filterFocusRequest = 0,
		viewing = false,
	}: Props = $props();

	// A request counts once, and only while the panel is on screen; the filter
	// field may appear later than the request (first activation), so the effect
	// waits for it.
	let filterInput = $state<HTMLInputElement | null>(null);
	let handledFilterFocus = untrack(() => filterFocusRequest);
	$effect(() => {
		const request = filterFocusRequest;
		const input = filterInput;
		if (!visible || !input || request <= handledFilterFocus) return;
		handledFilterFocus = request;
		input.focus();
	});

	let inspectedRoot = $state("");
	let checkouts = $state<RepositoryCheckout[]>([]);
	let expanded = $state.raw<Set<string>>(new Set());
	let treeData = $state.raw<TreeItem[]>([]);
	let sortDirection = $state<"ascending" | "descending">("ascending");
	let pending = $state<PendingEntry | null>(null);
	let pendingName = $state("");
	let entryField = $state<HTMLInputElement | null>(null);
	let actionError = $state<string | null>(null);
	let fileClipboard = $state.raw<FileClipboard | null>(null);
	let contextMenuNode = $state.raw<TreeItem | null>(null);
		let treeHost = $state<HTMLDivElement | null>(null);
		let treeScroll = $state<HTMLDivElement | null>(null);
		let treeScrollTop = $state(0);
		let treeHeight = $state(400);
		let virtualized = $state(true);
		let searchText = $state("");
	let searchMatches = $state.raw<SourceTreeSearchMatch[]>([]);
	let searchNextCursor = $state<number | null>(null);
	let searchLoading = $state(false);
	let searchError = $state<string | null>(null);
	let searchGeneration = 0;
	let revealGeneration = 0;
	let activeSearchScanId: string | null = null;
	let notifiedUnavailableRoot = "";
	let hydratedRoot = "";
	let hydratedExpansionKey = "";
	let expansionRestoreGeneration = 0;
	let scopedRoot = "";
	let scopedOwnedId: string | null = null;
	let inspectionGeneration = 0;
	let checkoutGeneration = 0;
	let checkoutBusy = $state(false);
	let scopePickerOpen = $state(false);
	let fileHistoryOpen = $state(false);
	let filesOwnerSignal: AbortSignal | undefined;

	const READ_ONLY_SCOPE_MESSAGE =
		"This folder is open for reading only — switch back to the session folder to change it.";
	const READ_ONLY_RUNTIME_MESSAGE = "File changes are available in the desktop app only.";
	const MUTATING_ACTIONS = new Set(["new-file", "new-folder", "cut", "paste", "rename", "delete"]);

	function canMutateFileTree(): boolean {
		const sessionRoot = canonicalPath(root);
		return isNativeTauriRuntime() && Boolean(ownedId) && !readOnlyReason && sessionRoot !== "" && canonicalPath(explorer.root ?? root) === sessionRoot;
	}
	const fileTreeMutationReason = (): string => readOnlyReason || (isNativeTauriRuntime() ? READ_ONLY_SCOPE_MESSAGE : READ_ONLY_RUNTIME_MESSAGE);

	const loadedNodes = $derived(explorerNodes());
	const sessionRoot = $derived(root.trim());
	const checkoutRoots = $derived([
		...new Set([sessionRoot, ...checkoutDiscoveryRoots].map(canonicalPath).filter(Boolean)),
	]);
	const projectRoot = $derived((inspectedRoot || explorer.root || sessionRoot).trim());
	const stopWorkspaceFileChanges = onWorkspaceFileChange((change) => {
		if (!visible || !ownedId || change.ownedId !== ownedId) return;
		const path = workspaceChangePath(projectRoot, change.path);
		refreshChangedPath(path);
	});
	const readOnlyInspection = $derived(
		Boolean(readOnlyReason) || (Boolean(inspectedRoot) && canonicalPath(inspectedRoot) !== canonicalPath(sessionRoot)),
	);
	const scopeOptions = $derived(
		sessionRoot === ""
			? []
			: [
					{ path: sessionRoot, label: viewing ? folderName(sessionRoot) : `${folderName(sessionRoot)} (session)` },
					...checkouts
						.filter((checkout) => canonicalPath(checkout.path) !== canonicalPath(sessionRoot))
						.map((checkout) => ({
							path: checkout.path,
							label: checkout.branch.trim() ? `${folderName(checkout.path)} — ${checkout.branch.trim()}` : folderName(checkout.path),
						})),
				],
	);
	const scopeValue = $derived(projectRoot || sessionRoot);
	/** The picker shows the session's own folder by its bare name; the list marks it "(session)" unless it is only being viewed. */
	const scopeLabel = $derived.by(() => {
		const option = scopeOptions.find((candidate) => canonicalPath(candidate.path) === canonicalPath(scopeValue));
		if (!option) return "Session folder";
		return canonicalPath(option.path) === canonicalPath(sessionRoot) ? folderName(option.path) : option.label;
	});
	const listed = $derived(explorer.lastScanFinishedAt !== null);
	const watchedDirectoryKey = $derived.by(() => {
		loadedNodes.length;
		if (!listed) return "";
		return loadedExplorerDirectories()
			.map((directory) => canonicalPath(directory.path))
			.sort()
			.join("\u0000");
	});
	const searching = $derived(searchText.trim().length > 0);
	const treeVisible = $derived(explorer.activated && explorer.unavailable === null && loadedNodes.length > 0);
	/** The root's own listing has returned; the loaded-directory map changes only alongside the tree rows. */
	const rootListed = $derived.by(() => {
		loadedNodes.length;
		return explorer.root !== null && loadedExplorerDirectoryDepth(explorer.root) !== null;
	});
	const searchTreeData = $derived(
		searchMatches.map<TreeItem>((match) => ({
			path: match.path,
			name: match.name,
			isDirectory: match.isDirectory,
			excluded: match.excluded,
			parentPath: projectRoot,
			depth: 0,
			childCount: 0,
			ignored: match.excluded,
			treePath: match.relativePath,
			treeParentPath: "",
			relativePath: match.relativePath,
			hasChildren: false,
			expanded: false,
			selected: match.path === explorer.selectedPath,
			searchResult: true,
		})),
	);
		const displayedTreeData = $derived(searching ? searchTreeData : treeData);
		/** The rows an open tree actually shows, parents before their open children. */
		const visibleRows = $derived(visibleFileTreeNodes(displayedTreeData, expanded, compareTreeNodes));
		/** Only the slice inside the viewport, plus a spacer for everything above and below. */
		const treeWindow = $derived(
			windowFileTreeNodes(
				visibleRows,
				treeScrollTop,
				treeHeight,
				FILE_TREE_ROW_HEIGHT,
				FILE_TREE_OVERSCAN_ROWS,
			),
		);
	const renderedRows = $derived(virtualized ? treeWindow.nodes : visibleRows);
	// File History follows whichever changed last: the tree selection or the file
	// on screen in the editor, however that file was opened. Each falls back to
	// the other when it clears.
	let historyPath = $state<string | null>(null);
	$effect(() => {
		historyPath = explorer.selectedPath ?? untrack(() => editorState.activePath);
	});
	$effect(() => {
		historyPath = editorState.activePath ?? untrack(() => explorer.selectedPath);
	});
	const selectedFile = $derived(fileHistoryTarget(projectRootForView(), historyPath, loadedNodes));

	$effect(() => {
		const treeRoot = projectRoot.replace(/\/+$/, "");
		sortDirection;
		treeData = loadedNodes.map((node) => ({
			...node,
			treePath: relativePath(treeRoot, node.path),
			treeParentPath: node.parentPath === treeRoot ? "" : relativePath(treeRoot, node.parentPath),
			relativePath: relativePath(treeRoot, node.path),
			hasChildren: node.isDirectory,
			expanded: expanded.has(node.path),
			selected: node.path === explorer.selectedPath,
		}));
	});

	$effect(() => {
		const nextRoot = canonicalPath(sessionRoot);
		const nextOwnedId = ownedId;
		const discoveryRoots = checkoutRoots;
		const shouldActivate = visible && nextRoot !== "";
		const controller = new AbortController();
		filesOwnerSignal = controller.signal;
		untrack(() => {
			const rootChanged = nextRoot !== scopedRoot || nextOwnedId !== scopedOwnedId;
			if (rootChanged) {
				scopedRoot = nextRoot;
				scopedOwnedId = nextOwnedId;
				inspectionGeneration += 1;
				inspectedRoot = "";
				checkouts = [];
				cancelActiveSearch();
				revealGeneration += 1;
				hydratedRoot = "";
				hydratedExpansionKey = "";
				expansionRestoreGeneration += 1;
				expanded = new Set();
				pending = null;
				fileClipboard = null;
				contextMenuNode = null;
				searchText = "";
				if (nextRoot && canonicalPath(explorer.root ?? "") === nextRoot) {
					for (const directory of loadedExplorerDirectories()) {
						if (directory.path !== nextRoot) unloadDirectory(directory.path);
					}
				}
			}
			if (shouldActivate && !controller.signal.aborted) {
				activateExplorer(nextRoot, false, controller.signal);
			} else if (rootChanged) {
				// Only a real change clears the tree. Showing or hiding the panel must
				// not wipe the "checkout deleted" state the shell sets for a session
				// whose folder is gone, which left it saying "No session selected".
				activateExplorer(null);
			}
			if (visible && discoveryRoots.length > 0) {
				void loadCheckouts(discoveryRoots, ++checkoutGeneration, controller.signal);
			}
		});
		return () => {
			controller.abort();
			if (filesOwnerSignal === controller.signal) filesOwnerSignal = undefined;
			stopScan(controller.signal);
		};
	});

	$effect(() => {
		if (!visible) return;
		const controller = new AbortController();
		void refreshFilesAfterRender(controller.signal);
		return () => controller.abort();
	});

	/** Hiding the panel or unmounting it aborts the signal before the refresh. */
	async function refreshFilesAfterRender(signal: AbortSignal): Promise<void> {
		await tick();
		if (!signal.aborted) refreshFiles();
	}

	async function loadCheckouts(
		roots: readonly string[],
		generation: number,
		signal: AbortSignal,
	): Promise<void> {
		try {
			const grouped = await listRepositoryCheckoutsFromTauri(roots);
			if (signal.aborted || generation !== checkoutGeneration) return;
			if (!grouped) {
				checkouts = [];
				return;
			}
			const unique = new Map<string, RepositoryCheckout>();
			for (const checkout of Object.values(grouped).flat()) {
				const path = canonicalPath(checkout.path);
				if (path) unique.set(path, checkout);
			}
			checkouts = [...unique.values()];
		} catch (error) {
			if (dev && !signal.aborted && generation === checkoutGeneration) {
				console.warn("Could not list repository checkouts", error);
			}
		}
	}

	async function useSessionCheckout(): Promise<void> {
		if (!readOnlyInspection || checkoutBusy || !onUseSessionCheckout) return;
		checkoutBusy = true;
		actionError = null;
		try {
			await onUseSessionCheckout(projectRoot);
		} catch (error) {
			actionError = describeError(error);
		} finally {
			checkoutBusy = false;
		}
	}

	async function validateInspectionRootForEffect(
		generation: number,
		target: string,
		sessionRootPath: string,
		signal: AbortSignal,
	): Promise<void> {
		const validation = await validateProjectRootFromTauri(target, signal);
		if (
			signal.aborted ||
			generation !== inspectionGeneration ||
			canonicalPath(root) !== sessionRootPath ||
			canonicalPath(inspectionRoot ?? "") !== target
		)
			return;
		if (validation === null || (validation.exists && validation.isDirectory)) {
			selectInspectionRoot(target);
		} else {
			selectInspectionRoot("");
		}
	}

	$effect(() => {
		if (inspectionRoot === undefined) return;
		const signal = filesOwnerSignal;
		const requestedRoot = canonicalPath(inspectionRoot ?? "");
		const sessionRootPath = canonicalPath(sessionRoot);
		const target = requestedRoot && requestedRoot !== sessionRootPath ? requestedRoot : "";
		const generation = ++inspectionGeneration;
		if (!visible || !sessionRootPath || !signal || signal.aborted) return;
		if (requestedRoot === canonicalPath(inspectedRoot) && (!inspectionRoot || target !== "")) return;
		if (!target) {
			selectInspectionRoot("");
			return;
		}
		if (!scopeOptions.some((option) => canonicalPath(option.path) === target)) {
			selectInspectionRoot("");
			return;
		}
		void validateInspectionRootForEffect(generation, target, sessionRootPath, signal);
	});

	$effect(() => {
		const sessionOwner = ownedId;
		const requestedRoot = canonicalPath(projectRoot);
		const activeRoot = canonicalPath(explorer.root ?? "");
		const savedPaths = activeRoot ? canonicalExpandedPaths(activeRoot, expandedPathsByRoot?.[activeRoot] ?? []) : [];
		const savedKey = expansionKey(savedPaths);
		if (!sessionOwner || sessionOwner !== scopedOwnedId || !listed || !explorer.activated || !requestedRoot || requestedRoot !== activeRoot) {
			if (activeRoot !== hydratedRoot) {
				hydratedRoot = activeRoot;
				hydratedExpansionKey = "";
				expansionRestoreGeneration += 1;
				expanded = new Set();
			}
			return;
		}
		if (activeRoot === hydratedRoot && savedKey === hydratedExpansionKey) return;

		hydratedRoot = activeRoot;
		hydratedExpansionKey = savedKey;
		expansionRestoreGeneration += 1;
		expanded = new Set(savedPaths);
		const generation = expansionRestoreGeneration;
		const signal = filesOwnerSignal;
		if (signal && !signal.aborted) {
			void restoreExpandedDirectories(activeRoot, savedPaths, generation, signal);
		}
	});

	async function ownFileTreeWatch(directories: readonly string[], signal: AbortSignal): Promise<void> {
		try {
			await watchFileTree(directories, signal, (paths) => {
				if (!ownedId) return;
				for (const path of paths) publishWorkspaceFileChange({ ownedId, path });
			});
		} catch (error) {
			if (dev && !signal.aborted) console.warn("Could not watch the file tree", error);
		}
	}

	$effect(() => {
		const target = canonicalPath(projectRoot);
		const directories = watchedDirectoryKey
			? watchedDirectoryKey
					.split("\u0000")
					.filter(
						(directory) =>
							directory === target ||
							!directory
								.slice(target.length)
								.split("/")
								.some((part) => part.startsWith(".")),
					)
			: [];
		if (!visible || !target || directories.length === 0 || !isNativeTauriRuntime()) return;
		const controller = new AbortController();
		void ownFileTreeWatch(directories, controller.signal);
		return () => controller.abort();
	});

	$effect(() => {
		const target = projectRoot;
		if (explorer.unavailable !== "checkout-deleted" || !target || readOnlyInspection) {
			if (explorer.unavailable === null) notifiedUnavailableRoot = "";
			return;
		}
		if (canonicalPath(target) === canonicalPath(notifiedUnavailableRoot)) return;
		notifiedUnavailableRoot = target;
		void onRootUnavailable?.(target);
	});
	$effect(() => {
		const query = searchText.trim();
		explorer.includeExcluded;
		const generation = ++searchGeneration;
		cancelActiveSearch();
		searchMatches = [];
		searchNextCursor = null;
		searchError = null;
		searchLoading = false;
		if (!query || !visible || !listed || !projectRootForView()) {
			return;
		}

		const signal = filesOwnerSignal;
		if (signal && !signal.aborted) void loadSearchPage(generation, null, true, signal);
	});

	$effect(() => {
		if (visible) return;
		contextMenuNode = null;
	});

	$effect(() => {
		const field = entryField;
		if (!field) return;
		field.focus();
		field.select();
	});

		$effect(() => {
			displayedTreeData.length;
		const viewport = treeScroll;
		if (!viewport) return;
		const maxScrollTop = Math.max(0, viewport.scrollHeight - viewport.clientHeight);
			if (viewport.scrollTop > maxScrollTop) viewport.scrollTop = maxScrollTop;
		});

		function observeTreeHost(node: HTMLElement) {
			const update = () => {
				const nextHeight = Math.max(1, Math.floor(node.clientHeight));
				if (treeHeight !== nextHeight) treeHeight = nextHeight;
			};
			update();
			const observer = new ResizeObserver(update);
			observer.observe(node);
			return {
				destroy() {
					observer.disconnect();
				},
			};
		}

	function relativePath(projectRoot: string, path: string): string {
		const prefix = `${projectRoot}/`;
		return path.startsWith(prefix) ? path.slice(prefix.length) : path.replace(/^\/+/, "");
	}

	function folderName(path: string): string {
		const parts = path.split("/").filter(Boolean);
		return parts.at(-1) ?? path;
	}

	function projectRootForView(): string {
		return canonicalPath(projectRoot);
	}

	function selectInspectionRoot(value: string): void {
		scopePickerOpen = false;
		const target = canonicalPath(value);
		if (!target || target === canonicalPath(sessionRoot)) {
			if (inspectedRoot === "" && !inspectionRoot) return;
			inspectedRoot = "";
			inspectionGeneration += 1;
			revealGeneration += 1;
			expanded = new Set();
			pending = null;
			fileClipboard = null;
			actionError = null;
			searchText = "";
			cancelActiveSearch();
			activateExplorer(sessionRoot || null, false, filesOwnerSignal);
			onInspectionRootChange?.(null);
			return;
		}
		if (!scopeOptions.some((option) => canonicalPath(option.path) === target)) return;
		if (canonicalPath(inspectedRoot) === target) return;
		inspectedRoot = target;
		inspectionGeneration += 1;
		revealGeneration += 1;
		expanded = new Set();
		pending = null;
		fileClipboard = null;
		actionError = null;
		searchText = "";
		cancelActiveSearch();
		activateExplorer(target, false, filesOwnerSignal);
		onInspectionRootChange?.(target);
	}

	function parentOf(path: string): string {
		const cut = path.lastIndexOf("/");
		return cut < 0 ? "" : path.slice(0, cut);
	}

	function describeError(error: unknown): string {
		return error instanceof Error ? error.message : String(error);
	}

	function canonicalExpandedPaths(projectRoot: string, paths: readonly string[]): string[] {
		return [
			...new Set(
				paths
					.map((path) => canonicalPath(path))
					.filter((path) => path && path !== projectRoot && isPathAtOrBelow(path, projectRoot)),
			),
		].sort((left, right) => {
			const depth = pathDepth(left) - pathDepth(right);
			return depth !== 0 ? depth : left.localeCompare(right);
		});
	}

	function pathDepth(path: string): number {
		return path.split("/").filter(Boolean).length;
	}

	function isPathAtOrBelow(path: string, projectRoot: string): boolean {
		if (path === projectRoot) return true;
		if (projectRoot === "/") return path.startsWith("/");
		return path.startsWith(`${projectRoot}/`);
	}

	function expansionKey(paths: readonly string[]): string {
		return paths.join("\u0000");
	}

	function persistExpandedPaths(): void {
		const projectRoot = canonicalPath(explorer.root ?? "");
		if (!scopedOwnedId || scopedOwnedId !== ownedId || !projectRoot || projectRoot !== hydratedRoot || !listed) return;
		onExpandedPathsChange?.(scopedOwnedId, projectRoot, canonicalExpandedPaths(projectRoot, [...expanded]));
	}

	async function restoreExpandedDirectories(
		projectRoot: string,
		paths: readonly string[],
		generation: number,
		signal: AbortSignal,
	): Promise<void> {
		const directories = new Set<string>();
		for (const path of paths) {
			const relative = path.slice(projectRoot.length).replace(/^\/+/, "");
			let directory = projectRoot;
			for (const component of relative.split("/").filter(Boolean)) {
				directory = `${directory}/${component}`;
				directories.add(directory);
			}
		}
		const ordered = [...directories].sort((left, right) => {
			const depth = pathDepth(left) - pathDepth(right);
			return depth !== 0 ? depth : left.localeCompare(right);
		});
		for (const directory of ordered) {
			if (signal.aborted || generation !== expansionRestoreGeneration || canonicalPath(explorer.root ?? "") !== projectRoot) return;
			if (loadedExplorerDirectoryDepth(directory) !== null) continue;
			await loadDirectory(directory, pathDepth(directory) - pathDepth(projectRoot), signal);
		}
	}

	function cancelActiveSearch(): void {
		const scanId = activeSearchScanId;
		activeSearchScanId = null;
		if (scanId) void cancelSourceScanFromTauri(scanId);
	}

	function onFilterInput(event: Event & { currentTarget: HTMLInputElement }): void {
		searchText = event.currentTarget.value;
	}

	async function loadSearchPage(
		generation: number,
		cursor: number | null,
		replace: boolean,
		signal: AbortSignal,
	): Promise<void> {
		const projectRoot = projectRootForView();
		const query = searchText.trim();
		if (signal.aborted || !projectRoot || !query || generation !== searchGeneration) return;

		cancelActiveSearch();
		const scanId = createSourceScanId();
		activeSearchScanId = scanId;
		searchLoading = true;
		searchError = null;
		try {
			const page = await searchSourceTreeFromTauri(
				projectRoot,
				query,
				50,
				cursor,
				explorer.includeExcluded,
				scanId,
				signal,
			);
			if (signal.aborted || generation !== searchGeneration || searchText.trim() !== query || projectRootForView() !== projectRoot)
				return;
			if (!page) {
				searchError = "The file search is not available here.";
				return;
			}
			searchMatches = replace ? page.matches : [...searchMatches, ...page.matches];
			searchNextCursor = page.nextCursor;
		} catch (error) {
			if (generation === searchGeneration) searchError = describeError(error);
		} finally {
			if (generation === searchGeneration) searchLoading = false;
			if (activeSearchScanId === scanId) activeSearchScanId = null;
		}
	}

	function loadMoreSearchResults(): void {
		if (searchNextCursor === null || searchLoading) return;
		const signal = filesOwnerSignal;
		if (signal && !signal.aborted) void loadSearchPage(searchGeneration, searchNextCursor, false, signal);
	}

	async function onSearchResultClicked(node: TreeItem): Promise<void> {
		const projectRoot = projectRootForView();
		const signal = filesOwnerSignal;
		if (!projectRoot || !signal || signal.aborted) return;
		const generation = ++revealGeneration;
		searchText = "";
		const ancestors = await revealExplorerPath(node.path, signal);
		if (signal.aborted || generation !== revealGeneration || projectRootForView() !== projectRoot) return;
		expanded = new Set([...expanded, ...ancestors]);
		persistExpandedPaths();
		selectPath(node.path);
		if (!node.isDirectory) {
			openFileInEditor({
				path: node.path,
				projectRoot,
				readOnly: readOnlyInspection,
				preview: true,
			});
		}
	}

	/** Siblings only: folders first, then name, in whichever direction the header asks for. */
	function compareTreeNodes(left: TreeItem, right: TreeItem): number {
		if (left.isDirectory !== right.isDirectory) return left.isDirectory ? -1 : 1;
		const nameOrder = left.name.localeCompare(right.name, undefined, {
			numeric: true,
			sensitivity: "base",
		});
		return sortDirection === "ascending" ? nameOrder : -nameOrder;
	}

	function collapsePath(path: string): void {
		unloadDirectory(path);
		expanded = new Set([...expanded].filter((candidate) => candidate !== path && !candidate.startsWith(`${path}/`)));
		persistExpandedPaths();
	}

	function onTreeNodeClicked(node: TreeItem): void {
		if (node.searchResult) {
			void onSearchResultClicked(node);
			return;
		}
		if (node.isDirectory) {
			if (expanded.has(node.path)) collapsePath(node.path);
			else {
				expanded = new Set([...expanded, node.path]);
				persistExpandedPaths();
				void loadDirectory(node.path, node.depth + 1);
			}
			return;
		}

		selectPath(node.path);
		const projectRoot = projectRootForView();
		openFileInEditor({
			path: node.path,
			projectRoot: projectRoot || undefined,
			readOnly: readOnlyInspection,
			preview: true,
		});
	}

	function pinTreeNodeOpen(node: TreeItem, event: MouseEvent): void {
		event.stopPropagation();
		if (node.isDirectory) return;
		selectPath(node.path);
		const projectRoot = projectRootForView();
		openFileInEditor({
			path: node.path,
			projectRoot: projectRoot || undefined,
			readOnly: readOnlyInspection,
			pin: true,
		});
	}

	function beginEntry(node: TreeItem, kind: PendingEntry["kind"]): void {
		if (readOnlyInspection) return;
		actionError = null;
		if (kind !== "rename" && !expanded.has(node.path)) {
			expanded = new Set([...expanded, node.path]);
			persistExpandedPaths();
			void loadDirectory(node.path, node.depth + 1);
		}
		pending = { kind, path: node.path };
		pendingName = kind === "rename" ? node.name : "";
	}

	function beginRootEntry(kind: "new-file" | "new-folder"): void {
		if (readOnlyInspection) return;
		if (!canMutateFileTree()) {
			actionError = fileTreeMutationReason();
			return;
		}
		if (!explorer.root || !rootListed) return;
		actionError = null;
		pending = { kind, path: explorer.root };
		pendingName = "";
	}

	async function isFree(target: string): Promise<boolean> {
		const { exists } = await import("../../../workspaceFs");
		if (!(await exists(target))) return true;
		actionError = `Something is already called ${target.slice(target.lastIndexOf("/") + 1)} here.`;
		return false;
	}

	async function commitEntry(): Promise<void> {
		if (readOnlyInspection) return;
		const generation = inspectionGeneration;
		const entry = pending;
		const name = pendingName.trim();
		pending = null;
		if (!entry || !name || name.includes("/")) return;
		if (!canMutateFileTree()) {
			actionError = fileTreeMutationReason();
			return;
		}

		try {
			const fs = await import("../../../workspaceFs");
			if (generation !== inspectionGeneration || readOnlyInspection) return;
			if (entry.kind === "rename") {
				const target = `${parentOf(entry.path)}/${name}`;
				if (target !== entry.path && (await isFree(target))) {
					if (generation !== inspectionGeneration || readOnlyInspection) return;
					await fs.rename(entry.path, target);
					collapsePath(entry.path);
					refreshChangedPath(target);
				}
			} else {
				const target = `${entry.path}/${name}`;
				if (!(await isFree(target))) return;
				if (generation !== inspectionGeneration || readOnlyInspection) return;
				if (entry.kind === "new-folder") await fs.mkdir(target);
				else await fs.writeTextFile(target, "");
				refreshChangedPath(target);
			}
		} catch (error) {
			actionError = describeError(error);
		}
	}

	function onEntryKeydown(event: KeyboardEvent): void {
		if (event.key === "Enter") {
			event.preventDefault();
			void commitEntry();
		} else if (event.key === "Escape") {
			event.preventDefault();
			pending = null;
		}
	}

	async function copyDirectory(source: string, target: string): Promise<void> {
		const fs = await import("../../../workspaceFs");
		await fs.mkdir(target);
		for (const entry of await fs.readDir(source)) {
			if (entry.isSymlink) continue;
			const childSource = `${source}/${entry.name}`;
			const childTarget = `${target}/${entry.name}`;
			if (entry.isDirectory) await copyDirectory(childSource, childTarget);
			else if (entry.isFile) await fs.copyFile(childSource, childTarget);
		}
	}

	async function pasteInto(node: TreeItem): Promise<void> {
		if (readOnlyInspection) return;
		const generation = inspectionGeneration;
		const entry = fileClipboard;
		if (!entry || !node.isDirectory) return;
		const target = `${node.path}/${entry.name}`;
		if (!(await isFree(target))) return;
		if (generation !== inspectionGeneration || readOnlyInspection) return;

		const fs = await import("../../../workspaceFs");
		if (generation !== inspectionGeneration || readOnlyInspection) return;
		if (entry.operation === "cut") {
			await fs.rename(entry.path, target);
			collapsePath(entry.path);
			fileClipboard = null;
		} else if (entry.isDirectory) {
			await copyDirectory(entry.path, target);
		} else {
			await fs.copyFile(entry.path, target);
		}
		refreshChangedPath(target);
	}

	async function toggleExcludedFiles(): Promise<void> {
		const include = !explorer.includeExcluded;
		if (!include) {
			for (const node of loadedNodes.filter((candidate) => candidate.ignored)) collapsePath(node.path);
		}
		setIncludeExcluded(include);
		refresh();
	}

	async function runAction(node: TreeItem, id: string): Promise<void> {
		actionError = null;
		if (MUTATING_ACTIONS.has(id) && !canMutateFileTree()) return;
		try {
			if (id === "new-file" || id === "new-folder" || id === "rename") {
				beginEntry(node, id);
			} else if (id === "copy-path") {
				const { writeText } = await import("@tauri-apps/plugin-clipboard-manager");
				await writeText(nativeWorkspacePath(node.path));
			} else if (id === "reveal-in-finder") {
				const { revealItemInDir } = await import("@tauri-apps/plugin-opener");
				await revealItemInDir(node.path);
			} else if (id === "delete") {
				await moveToTrashFromTauri(node.path);
				collapsePath(node.path);
				refreshChangedPath(node.path);
			} else if (id === "cut" || id === "copy") {
				fileClipboard = {
					operation: id,
					path: node.path,
					name: node.name,
					isDirectory: node.isDirectory,
				};
			} else if (id === "paste") {
				await pasteInto(node);
			} else if (id === "open-timeline") {
				const projectRoot = projectRootForView();
				if (!projectRoot || node.isDirectory) return;
				selectPath(node.path);
				fileHistoryOpen = true;
				await gitService.showFileHistory(projectRoot, node.relativePath);
			} else if (id === "git-file-history") {
				const projectRoot = projectRootForView();
				if (projectRoot && !node.isDirectory) {
					selectPath(node.path);
					await openFileTimeline({ projectRoot, relativePath: node.relativePath });
				}
			} else if (id === "toggle-excluded") {
				await toggleExcludedFiles();
			}
		} catch (error) {
			actionError = describeError(error);
		}
	}

	function contextMenuItems(node: TreeItem) {
		const mutationDisabled = !canMutateFileTree();
		const mutationReason = mutationDisabled ? fileTreeMutationReason() : undefined;
		return [
			...(node.isDirectory
				? [
						{
							id: "new-file",
							label: "New File",
							disabled: mutationDisabled,
							title: mutationReason,
							onselect: () => void runAction(node, "new-file"),
						},
						{
							id: "new-folder",
							label: "New Folder",
							disabled: mutationDisabled,
							title: mutationReason,
							onselect: () => void runAction(node, "new-folder"),
						},
						{
							id: "paste",
							label: "Paste",
							disabled: mutationDisabled || !fileClipboard,
							title: mutationReason ?? (fileClipboard ? undefined : "Copy or cut a file first."),
							onselect: () => void runAction(node, "paste"),
						},
					]
				: []),
			{
				id: "open-timeline",
				label: "Open Timeline",
				separatorBefore: node.isDirectory,
				onselect: () => void runAction(node, "open-timeline"),
			},
			{
				id: "git-file-history",
				label: "Git: View File History",
				onselect: () => void runAction(node, "git-file-history"),
			},
			{
				id: "cut",
				label: "Cut",
				separatorBefore: true,
				disabled: mutationDisabled,
				title: mutationReason,
				onselect: () => void runAction(node, "cut"),
			},
			{
				id: "copy",
				label: "Copy",
				onselect: () => void runAction(node, "copy"),
			},
			{ id: "copy-path", label: "Copy Path", onselect: () => void runAction(node, "copy-path") },
			{
				id: "rename",
				label: "Rename",
				separatorBefore: true,
				disabled: mutationDisabled,
				title: mutationReason,
				onselect: () => void runAction(node, "rename"),
			},
			{
				id: "delete",
				label: "Delete",
				danger: true,
				disabled: mutationDisabled,
				title: mutationReason,
				onselect: () => void runAction(node, "delete"),
			},
			{ id: "reveal-in-finder", label: "Reveal in Finder", separatorBefore: true, disabled: revealDisabledReason(node.path) !== null,
				title: revealDisabledReason(node.path) ?? undefined, onselect: () => void runAction(node, "reveal-in-finder") },
			{
				id: "toggle-excluded",
				label: explorer.includeExcluded ? "Hide Excluded Files" : "Show Excluded Files",
				onselect: () => void runAction(node, "toggle-excluded"),
			},
		];
	}

	function selectContextMenuNode(node: TreeItem): void {
		selectPath(node.path);
		contextMenuNode = node;
	}

	function refreshFiles(): void {
		refresh();
		if (ownedId && projectRoot) {
			publishWorkspaceFileChange({ ownedId, path: projectRoot, recursive: true });
		}
	}

	onDestroy(() => {
		stopWorkspaceFileChanges();
		inspectionGeneration += 1;
		searchGeneration += 1;
		revealGeneration += 1;
		cancelActiveSearch();
		contextMenuNode = null;
		activateExplorer(null);
	});
</script>

<div class="files-panel flex h-full min-h-0 w-full flex-col text-foreground">
	<!-- The header follows the Codex file pane: a row of pill controls, then the
	     filter field, then the tree. -->
	<header class="flex flex-none flex-col gap-2 px-3 pt-1 pb-2" aria-label="Files">
		<div class="-mr-1 flex min-w-0 items-center gap-1">
			{#if scopeOptions.length > 0}
				<Select.Root
					type="single"
					bind:open={scopePickerOpen}
					value={scopeValue}
					onValueChange={selectInspectionRoot}
				>
					<!-- Up to 190px for the folder name; it gives way only down to 120px
					     when the icon buttons beside it need the room. -->
					<Select.Trigger size="sm" class="min-w-[120px] max-w-[190px] border-transparent bg-secondary pr-2 pl-3 text-[13px] hover:bg-secondary/80 data-[size=sm]:h-7 data-[size=sm]:rounded-full dark:bg-secondary dark:hover:bg-secondary/80 [&>svg:last-child]:size-3" aria-label="Folder this panel reads">
						<Folder class="size-[14px] text-muted-foreground" strokeWidth={2} aria-hidden="true" />
						<span class="min-w-0 truncate">{scopeLabel}{#if readOnlyInspection} · read-only{/if}</span>
					</Select.Trigger>
					<Select.Content class="min-w-[240px]">
						{#each scopeOptions as option (option.path)}
							<Select.Item value={option.path} label={option.label} />
						{/each}
					</Select.Content>
				</Select.Root>
			{/if}
			<span class="flex-1"></span>
			{#if explorer.activated && explorer.unavailable === null}
				<!-- Developer diagnostic, dev builds only: compares the windowed tree with
				     every row rendered. Icon-sized so the folder picker keeps its room. -->
				{#if dev}
					<IconButton
						label={virtualized ? "Showing visible rows only (dev only). Click to show every row" : "Showing every row (dev only). Click to show visible rows only"}
						onclick={() => (virtualized = !virtualized)}
					>
						<Rows3 class="size-[14px]" strokeWidth={2} aria-hidden="true" />
					</IconButton>
				{/if}
				<IconButton label="New file" disabled={!rootListed || readOnlyInspection} onclick={() => beginRootEntry("new-file")}>
					<FilePlus class="size-[14px]" />
				</IconButton>
				<IconButton label="New folder" disabled={!rootListed || readOnlyInspection} onclick={() => beginRootEntry("new-folder")}>
					<FolderPlus class="size-[14px]" />
				</IconButton>
				<IconButton
					label={sortDirection === "ascending" ? "Sorted A to Z. Click to sort Z to A" : "Sorted Z to A. Click to sort A to Z"}
					onclick={() => (sortDirection = sortDirection === "ascending" ? "descending" : "ascending")}
				>
					{#if sortDirection === "ascending"}<ArrowDownAZ class="size-[14px]" />{:else}<ArrowUpZA class="size-[14px]" />{/if}
				</IconButton>
				<IconButton
					label={explorer.includeExcluded ? "Hide excluded files" : "Show excluded files"}
					onclick={() => void toggleExcludedFiles()}
				>
					{#if explorer.includeExcluded}<EyeOff class="size-[14px]" />{:else}<Eye class="size-[14px]" />{/if}
				</IconButton>
				{#if explorer.scanning}
					<IconButton label="Stop listing files" onclick={() => stopScan()}><Square class="size-[14px]" /></IconButton>
				{:else}
					<IconButton label="List this project's files again" disabled={!explorer.root} onclick={refreshFiles}>
						<RefreshCw class="size-[14px]" />
					</IconButton>
				{/if}
			{/if}
		</div>
		{#if readOnlyInspection && onUseSessionCheckout}
			<Button
				size="sm"
				variant="outline"
				class="h-7 self-start rounded-full px-3 text-[13px]"
				disabled={checkoutBusy}
				data-testid="files-use-session-checkout"
				onclick={useSessionCheckout}
			>
				{checkoutBusy ? "Changing…" : "Use as session folder"}
			</Button>
		{/if}
		{#if explorer.activated && explorer.unavailable === null}
			<div class="relative">
				<Search class="pointer-events-none absolute top-1/2 left-3 size-[14px] -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
				<Input
					bind:ref={filterInput}
					type="search"
					class="h-8 w-full rounded-[var(--radius-md)] border-transparent bg-muted dark:bg-muted pr-3 pl-[34px] text-[13px]"
					placeholder="Filter files"
					aria-label="Search project files"
					autocomplete="off"
					autocapitalize="none"
					spellcheck={false}
					value={searchText}
					oninput={onFilterInput}
				/>
			</div>
		{/if}
	</header>

	{#if !explorer.activated}
		{#if sessionRoot}
			<EmptyState
				title="Loading files…"
				body="Reading the selected session's project folder."
			>
				{#snippet icon()}<FolderTree />{/snippet}
			</EmptyState>
		{:else}
			<EmptyState title="No session selected" body="Pick a session to view its files.">
				{#snippet icon()}<FolderTree />{/snippet}
			</EmptyState>
		{/if}
	{:else if explorer.unavailable === "checkout-deleted" && !readOnlyInspection}
		<EmptyState
			title="Checkout/Worktree deleted."
			body="The conversation is still available, but this session’s project files no longer exist."
		>
			{#snippet icon()}<TriangleAlert />{/snippet}
		</EmptyState>
	{:else if explorer.error && loadedNodes.length === 0}
		<EmptyState title="Could not list the files" body={explorer.error}>
			{#snippet icon()}<TriangleAlert />{/snippet}
			{#snippet actions()}
				<Button size="sm" variant="secondary" onclick={() => explorer.root && void scanRoot(explorer.root)}>
					Try again
				</Button>
			{/snippet}
		</EmptyState>
	{:else if explorer.scanning && loadedNodes.length === 0}
		<EmptyState title="Listing files…" body="Reading this project's top-level entries.">
			{#snippet icon()}<FolderTree />{/snippet}
		</EmptyState>
	{:else if loadedNodes.length === 0}
		<EmptyState title="No source files here" body="This folder has no files the app can open.">
			{#snippet icon()}<FolderTree />{/snippet}
			{#snippet actions()}
				<Button size="sm" variant="secondary" disabled={!rootListed || readOnlyInspection} onclick={() => beginRootEntry("new-file")}>
					New file
				</Button>
				<Button size="sm" variant="secondary" disabled={!rootListed || readOnlyInspection} onclick={() => beginRootEntry("new-folder")}>
					New folder
				</Button>
			{/snippet}
		</EmptyState>
	{:else}
		{#if explorer.error}
			<p class="border-b px-3 py-2 text-sm leading-snug text-[var(--color-bad)]">{explorer.error}</p>
		{/if}
		{#if searchError}
			<p class="border-b px-3 py-2 text-sm leading-snug text-[var(--color-bad)]">{searchError}</p>
		{/if}
	{/if}
	{#if explorer.root && actionError}
		<p class="border-b px-3 py-2 text-sm leading-snug text-[var(--color-bad)]">{actionError}</p>
	{/if}
	{#if explorer.root && pending}
		<div class="entry-bar">
			<Input
				class="h-6 w-full"
				aria-label={pending.kind === "rename" ? "Rename path" : "New path name"}
				placeholder={pending.kind === "new-folder" ? "New folder name" : "New file name"}
				bind:ref={entryField}
				bind:value={pendingName}
				onkeydown={onEntryKeydown}
				onblur={() => void commitEntry()}
			/>
		</div>
	{/if}

	<div
			class="tree-host"
			class:hidden={!treeVisible}
			aria-hidden={!treeVisible}
			bind:this={treeHost}
			use:observeTreeHost
	>
		<ContextMenu.Root
			onOpenChange={(open) => {
				if (!open) contextMenuNode = null;
			}}
		>
			<ContextMenu.Trigger>
				{#snippet child({ props })}
		<div
			{...props}
			class="tree-scroll"
			role="tree"
			tabindex={treeVisible ? 0 : -1}
			aria-label="Project files"
			bind:this={treeScroll}
			onscroll={(event) => (treeScrollTop = event.currentTarget.scrollTop)}
		>
				{#if renderedRows.length === 0}
					<p class="tree-empty">{#if searchLoading}<WorkingSpinner size={12} /> Searching files…{:else}Nothing matches that search.{/if}</p>
				{:else}
					<!-- One canvas whose height never changes as you scroll, with each row
					     transformed into place. Resizing spacer divs on every scroll instead
					     rewrites the scroller's layout continuously, and WebKit reallocates
					     the layer's tile backing each time without releasing the old grid. -->
					<div class="tree-canvas" style={`height: ${virtualized ? treeWindow.totalHeight : renderedRows.length * FILE_TREE_ROW_HEIGHT}px`}>
					<!-- Keyed by slot, not by path. Keying by path destroys and rebuilds every row
					     on each session switch; each destroyed row's layer backing is never
					     reclaimed, so the tile count ratchets. By slot, the same elements are
					     reused and only their contents change. -->
					{#each renderedRows as node, rowIndex (rowIndex)}
					<!-- svelte-ignore a11y_no_static_element_interactions -->
					<!-- svelte-ignore a11y_click_events_have_key_events -->
					<div
							class="tree-node"
							class:selected={node.path === explorer.selectedPath}
							style={`padding-left: ${node.depth * 16}px; transform: translateY(${(virtualized ? treeWindow.topSpacerHeight : 0) + rowIndex * FILE_TREE_ROW_HEIGHT}px)`}
						onclick={() => onTreeNodeClicked(node)}
						ondblclick={(event) => pinTreeNodeOpen(node, event)}
						oncontextmenu={() => selectContextMenuNode(node)}
					>
						<span class="tree-toggle" aria-hidden="true">
							{#if node.isDirectory}
								{#if expanded.has(node.path)}
									<ChevronDown size={12} strokeWidth={2} />
								{:else}
									<ChevronRight size={12} strokeWidth={2} />
								{/if}
							{/if}
						</span>
						<span class="tree-row" class:excluded={node.ignored} title={node.path}>
							<span class="tree-file-icon" aria-hidden="true">
								{#if node.isDirectory}
									{#if expanded.has(node.path)}
										<FolderOpen size={15} strokeWidth={1.75} />
									{:else}
										<Folder size={15} strokeWidth={1.75} />
									{/if}
								{:else}
									<FileIcon fileName={node.name} size={15} />
								{/if}
							</span>
							<span class="tree-name">{node.name}</span>
							{#if fileClipboard?.path === node.path}
								<span class="clipboard-mark">{fileClipboard.operation}</span>
							{/if}
						</span>
					</div>
				{/each}
				</div>
			{/if}
		</div>
				{/snippet}
			</ContextMenu.Trigger>
			{#if contextMenuNode}
				<ContextMenu.Content class="w-[220px]" aria-label={`Actions for ${contextMenuNode.name}`}>
					{#each contextMenuItems(contextMenuNode) as item (item.id)}
						{#if item.separatorBefore}<ContextMenu.Separator />{/if}
						<ContextMenu.Item
							variant={item.danger ? "destructive" : "default"}
							disabled={item.disabled}
							title={item.title}
							onSelect={item.onselect}
						>
							{item.label}
						</ContextMenu.Item>
					{/each}
				</ContextMenu.Content>
			{/if}
		</ContextMenu.Root>
	</div>
	{#if treeVisible && searching && searchNextCursor !== null}
		<div class="search-more">
			<Button size="sm" variant="ghost" disabled={searchLoading} onclick={loadMoreSearchResults}>
				{#if searchLoading}<WorkingSpinner size={12} />Loading…{:else}Load more{/if}
			</Button>
		</div>
	{/if}
	<FileHistoryPane
		visible={visible && treeVisible}
		root={projectRootForView()}
		relativePath={selectedFile?.relativePath ?? ""}
		fileName={selectedFile?.name ?? ""}
		bind:open={fileHistoryOpen}
		onOpenFile={() => {
			const projectRoot = projectRootForView();
			if (projectRoot && selectedFile) {
				openFileInEditor({
					path: selectedFile.path,
					projectRoot,
					readOnly: readOnlyInspection,
					preview: true,
				});
			}
		}}
		onOpenLarge={() => {
			const projectRoot = projectRootForView();
			if (projectRoot && selectedFile) {
				void openFileTimeline({ projectRoot, relativePath: selectedFile.relativePath });
			}
		}}
	/>

</div>

<style>
	.tree-host {
		--tree-node-indent-per-level: 16px;
		display: flex;
		height: 0;
		min-height: 0;
		flex: 1;
		overflow: hidden;
		padding: 0 8px 4px;
	}
	.tree-host.hidden {
		display: none;
	}
	.search-more {
		display: flex;
		justify-content: center;
		padding: 2px 8px 6px;
		border-top: 1px solid var(--color-border);
	}
	.entry-bar {
		padding: 4px 8px;
		border-bottom: 1px solid var(--color-border);
	}
	.tree-row {
		display: flex;
		align-items: center;
		min-width: 0;
		width: 100%;
		gap: 6px;
		color: var(--color-text);
	}
	.tree-row.excluded {
		opacity: 0.55;
	}
	.tree-file-icon {
		display: flex;
		flex: 0 0 14px;
		align-items: center;
		justify-content: center;
		color: var(--muted-foreground);
	}
	.tree-name {
		min-width: 0;
		flex: 1;
		overflow: hidden;
		text-overflow: ellipsis;
		white-space: nowrap;
	}
	.clipboard-mark {
		flex: none;
		color: var(--color-text-3);
		font-size: 12px;
		text-transform: uppercase;
	}
	.tree-empty {
		padding: 16px 10px;
		color: var(--color-text-3);
		text-align: center;
	}
	.tree-scroll {
		width: 100%;
		height: 100%;
		min-width: 0;
		min-height: 0;
		overflow-x: hidden;
		overflow-y: auto;
		scrollbar-width: thin;
		scrollbar-color: var(--scrollbar-thumb) transparent;
	}
		.tree-canvas {
			position: relative;
			width: 100%;
		}
		.tree-node {
			position: absolute;
			top: 0;
			left: 0;
			width: 100%;
		display: flex;
		height: 28px;
		min-width: 0;
		align-items: center;
		gap: 6px;
		padding-right: 8px;
		border-radius: 4px;
		font-size: 13px;
		user-select: none;
		cursor: pointer;
	}
	.tree-node:hover {
		background: var(--accent);
	}
	/* Neutral, like the rail's selected session: the brand colour stays off the tree. */
	.tree-node.selected {
		background: var(--secondary);
	}
	.tree-toggle {
		display: flex;
		width: 14px;
		height: 28px;
		flex: 0 0 14px;
		align-items: center;
		justify-content: center;
		color: var(--color-text-3);
	}
</style>
