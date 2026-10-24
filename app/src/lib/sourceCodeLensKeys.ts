/**
 * sourceCodeLensKeys.ts — the two names behind the "N references" number that
 * CodeMirror draws above a symbol.
 *
 * There are two of them and they are easy to confuse:
 *
 *  - the **lens id**, which is how the editor decides whether the lens it is
 *    looking at is the same one it saw a moment ago, and which also carries the
 *    spot back to us when the user clicks the number; and
 *  - the **count key**, which is how the editor remembers a number it has
 *    already been given.
 *
 * Neither may contain the model's version number. It used to, and the effect
 * was that a single keystroke gave every lens on screen a new identity and a
 * new question to ask, all at once. The number counts the symbol's name across
 * the project's saved files, which typing in the open buffer does not change,
 * so where the symbol sits is identity enough and an old number is simply let
 * go after a while.
 *
 * Kept out of `CodeMirrorSourceEditor.svelte` so these rules can be tested
 * without a browser or an editor instance.
 */

/** Where a code lens sits, and what it is about. */
export type SourceCodeLensSpot = {
	symbolName: string;
	/** 1-based. */
	line: number;
	/** 1-based. */
	column: number;
};

/** Marks an id as ours; anything else on the lens is somebody else's. */
export const sourceCodeLensIdPrefix = 'mcb-ref-count';

/**
 * How long one counted number is reused before it is asked for again. Same
 * span as the count memory in `shell/files/editor/sourceIntelligence.ts`, which is
 * what the re-ask lands in.
 */
export const sourceCodeLensCountMemoryMs = 30_000;

/**
 * The lens id. `encodeURIComponent` escapes `:`, so however odd the symbol
 * name is it can never split the id into more pieces than this builds.
 */
export function sourceCodeLensId(spot: SourceCodeLensSpot): string {
	return [
		sourceCodeLensIdPrefix,
		spot.line,
		spot.column,
		encodeURIComponent(spot.symbolName)
	].join(':');
}

/** Read a lens id back, or `null` when it is not one of ours or is damaged. */
export function sourceCodeLensSpotFromId(id: string | undefined): SourceCodeLensSpot | null {
	if (!id?.startsWith(`${sourceCodeLensIdPrefix}:`)) return null;
	const [, line, column, encodedSymbolName] = id.split(':');
	const parsedLine = Number(line);
	const parsedColumn = Number(column);
	const symbolName = encodedSymbolName ? decodeURIComponent(encodedSymbolName) : '';
	if (!symbolName || !Number.isFinite(parsedLine) || !Number.isFinite(parsedColumn)) {
		return null;
	}

	return { symbolName, line: parsedLine, column: parsedColumn };
}

/**
 * Which file, which line, which name. The column is left out on purpose: text
 * typed earlier on the same line moves the symbol sideways without changing
 * the number above it.
 */
export function sourceCodeLensCountKey(modelUri: string, spot: SourceCodeLensSpot): string {
	return `${modelUri}::${spot.line}:${spot.symbolName}`;
}

export type SettledSourceCodeLensCount = {
	count: number;
	atLeast: boolean;
};

/**
 * Choose what remains painted when one asynchronous count request settles.
 *
 * Roslyn is the native editor's one count authority, but two consumers can be
 * waiting on the same answer: the background CodeLens request and a later Peek
 * click. An older completion must not repaint over the later one, and a
 * transient no-answer must not erase a real number that already arrived.
 */
export function settledSourceCodeLensCount(
	current: SettledSourceCodeLensCount | null | undefined,
	incoming: SettledSourceCodeLensCount | null,
	isLatestRequest: boolean
): SettledSourceCodeLensCount | null | undefined {
	if (!isLatestRequest) return current;
	if (incoming === null && current != null) return current;
	return incoming;
}

/**
 * The finished sentence for a number we were actually given.
 *
 * "at least N" is for a number that came out of the plain-text search over the
 * project — that search stops when it runs out of time or hits its ceiling, so
 * the tally covers only the files it reached and the real number can only be
 * higher. A number from the language server is exact and is stated as it is.
 */
export function formatSourceCodeLensTitle(count: number, atLeast: boolean): string {
	const word = count === 1 ? 'reference' : 'references';
	return atLeast ? `at least ${count} ${word}` : `${count} ${word}`;
}
