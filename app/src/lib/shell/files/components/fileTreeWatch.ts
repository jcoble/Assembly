import { parseRemoteWorkspacePath } from '../../../workspacePaths.ts';
import { watch, type UnwatchFn, type WatchEvent } from "@tauri-apps/plugin-fs";

/**
 * Own one non-recursive native watcher for the directories currently loaded
 * by the visible tree. Aborting the panel owner releases it even when native
 * registration finishes after teardown.
 */
export async function watchFileTree(
	directories: readonly string[],
	signal: AbortSignal,
	onChange: (paths: readonly string[]) => void,
): Promise<void> {
	if (signal.aborted || directories.length === 0 || directories.some((path) => parseRemoteWorkspacePath(path))) return;
	let unwatch: UnwatchFn | null = null;
	let stopped = false;
	const stop = (): void => {
		if (stopped) return;
		stopped = true;
		signal.removeEventListener("abort", stop);
		unwatch?.();
		unwatch = null;
	};
	signal.addEventListener("abort", stop, { once: true });
	try {
		const registered = await watch(
			[...directories],
			(event: WatchEvent) => {
				if (!signal.aborted && !(typeof event.type === "object" && "access" in event.type)) onChange(event.paths);
			},
			{ recursive: false, delayMs: 350 },
		);
		if (stopped || signal.aborted) {
			registered();
			return;
		}
		unwatch = registered;
	} catch (error) {
		stop();
		if (!signal.aborted) throw error;
	}
}
