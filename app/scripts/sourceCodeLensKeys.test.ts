/**
 * Covers the two names behind the editor's "N references" margin number: the
 * lens id the editor identifies a lens by (and hands back on click), and the key
 * the editor remembers a counted number under. The rules live in
 * `src/lib/sourceCodeLensKeys.ts` so they can be checked without an editor.
 */
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import {
	formatSourceCodeLensTitle,
	settledSourceCodeLensCount,
	sourceCodeLensCountKey,
	sourceCodeLensId,
	sourceCodeLensSpotFromId
} from '../src/lib/sourceCodeLensKeys.ts';

// The source-intelligence module imports the shared Svelte counter. Node does
// not compile Svelte runes, so this identity function supplies the one rune the
// module creates while these pure decisions are imported.
globalThis.$state = (value) => value;
const {
	countingReadinessForStatus
} = await import('../src/lib/shell/files/editor/sourceIntelligence.ts');
const sourceIntelligenceSource = await readFile(
	new URL('../src/lib/shell/files/editor/sourceIntelligence.ts', import.meta.url),
	'utf8'
);
const spot = (symbolName, line, column) => ({ symbolName, line, column });

// an id carries the spot back intact when the user clicks the number
{
	const original = spot('RunAsync', 42, 17);
	assert.deepEqual(sourceCodeLensSpotFromId(sourceCodeLensId(original)), original);
}

// a symbol name with a colon in it does not split the id
{
	const original = spot('System::Widget', 3, 9);
	assert.deepEqual(sourceCodeLensSpotFromId(sourceCodeLensId(original)), original);
}

// the id is made of the spot and nothing else — no model version, which is
// what used to give every lens a new identity on every keystroke
{
	assert.equal(sourceCodeLensId(spot('RunAsync', 42, 17)), 'mcb-ref-count:42:17:RunAsync');
}

// a symbol that moved to another line is a different lens
{
	assert.notEqual(sourceCodeLensId(spot('RunAsync', 42, 17)), sourceCodeLensId(spot('RunAsync', 43, 17)));
}

// somebody else's lens, and damaged ids, are not ours to read
{
	assert.equal(sourceCodeLensSpotFromId(undefined), null);
	assert.equal(sourceCodeLensSpotFromId('some-other-provider:1:2:Alpha'), null);
	assert.equal(sourceCodeLensSpotFromId('mcb-ref-count:1:2:'), null, 'no symbol name');
	assert.equal(sourceCodeLensSpotFromId('mcb-ref-count:x:2:Alpha'), null, 'line is not a number');
	assert.equal(sourceCodeLensSpotFromId('mcb-ref-count:1:x:Alpha'), null, 'column is not a number');
}

// a remembered number is named by file, line and symbol
{
	const key = sourceCodeLensCountKey('file:///project/App.cs', spot('RunAsync', 42, 17));
	assert.equal(key, 'file:///project/App.cs::42:RunAsync');
}

// text typed earlier on the same line does not throw the number away
{
	assert.equal(
		sourceCodeLensCountKey('file:///project/App.cs', spot('RunAsync', 42, 17)),
		sourceCodeLensCountKey('file:///project/App.cs', spot('RunAsync', 42, 25))
	);
}

// the same symbol in another file is counted under its own name
{
	assert.notEqual(
		sourceCodeLensCountKey('file:///project/App.cs', spot('RunAsync', 42, 17)),
		sourceCodeLensCountKey('file:///project/Other.cs', spot('RunAsync', 42, 17))
	);
}

// A superseded request cannot erase a newer Roslyn count, and a transient
// no-answer from the newest request does not replace an already settled count.
{
	const roslynCount = { count: 7, atLeast: false };
	assert.deepEqual(settledSourceCodeLensCount(undefined, roslynCount, true), roslynCount);
	assert.deepEqual(settledSourceCodeLensCount(roslynCount, null, false), roslynCount);
	assert.deepEqual(settledSourceCodeLensCount(roslynCount, null, true), roslynCount);
	assert.equal(settledSourceCodeLensCount(undefined, null, true), null);
}

// a number that was counted in full is stated plainly, singular and plural
{
	assert.equal(formatSourceCodeLensTitle(0, false), '0 references');
	assert.equal(formatSourceCodeLensTitle(1, false), '1 reference');
	assert.equal(formatSourceCodeLensTitle(12, false), '12 references');
}

// a number from a pass that could not read everything says so
{
	assert.equal(formatSourceCodeLensTitle(1, true), 'at least 1 reference');
	assert.equal(formatSourceCodeLensTitle(50, true), 'at least 50 references');
}

// whether a reference-count question can wait for the language server
{
	assert.equal(countingReadinessForStatus('ready', false, false), 'ask-it');
	assert.equal(countingReadinessForStatus('starting', false, true), 'wait-for-it');
	assert.equal(countingReadinessForStatus('indexing', false, true), 'wait-for-it');
	assert.equal(countingReadinessForStatus('starting', false, false), 'no-server');
	assert.equal(countingReadinessForStatus('indexing', false, false), 'no-server');
	assert.equal(countingReadinessForStatus('not-running', true, true), 'wait-for-it');
	assert.equal(countingReadinessForStatus('not-running', true, false), 'no-server');
	assert.equal(countingReadinessForStatus('not-running', false, true), 'no-server');
	assert.equal(countingReadinessForStatus('not-running', undefined, true), 'no-server');
	assert.equal(countingReadinessForStatus('disabled', true, true), 'no-server');
	assert.equal(countingReadinessForStatus(undefined, true, true), 'no-server');
}

// a completed language-server question publishes a repaint instead of resolving
// parked margin-row promises.
{
	assert.doesNotMatch(sourceIntelligenceSource, /semanticCountRetry/);
	assert.doesNotMatch(sourceIntelligenceSource, new RegExp('set' + 'Timeout'));
	assert.doesNotMatch(sourceIntelligenceSource, /waitingSpots\.get\(key\) !== spot/);
	assert.doesNotMatch(sourceIntelligenceSource, /spot\.waiters/);
	assert.match(sourceIntelligenceSource, /waitingSpots\.delete\(key\)/);
	assert.match(sourceIntelligenceSource, /if \(count\) publishReferenceCountUpdate\(spot\.preview\.path\)/);
	assert.match(
		sourceIntelligenceSource,
		/countStore\.remember\(root, filePath, countKeyFor\(request\), count\)/,
		'a completed offscreen semantic lookup must retain the number paired with its Peek targets'
	);
	assert.match(
		sourceIntelligenceSource,
		/const referenceCountBatcher = isNativeTauriRuntime\(\)[\s\S]*?\? null[\s\S]*?: createReferenceCountBatcher/,
		'the legacy project text counter must not be wired in the native editor'
	);
	assert.match(
		sourceIntelligenceSource,
		/findReferences[\s\S]*?request\.languageServerEnabled === false[\s\S]*?findSourceReferencesInRootFromTauri\([\s\S]*?maxSourceReferenceResults/,
		'Peek without Supercharged must use the bounded native project scan'
	);
	assert.doesNotMatch(
		sourceIntelligenceSource,
		/\bfindSourceReferences\(/,
		'Peek must not use the removed in-memory legacy reference finder'
	);
	assert.match(
		sourceIntelligenceSource,
		/const uniqueUses = new Map<string, SourceReferenceTarget>\(\)/,
		'duplicate language-server locations must count once'
	);
	assert.match(
		sourceIntelligenceSource,
		/const semanticReferenceRequests = new Map<[\s\S]*?resolveSemanticReferences\(/,
		'the margin and Peek must share one in-flight semantic reference request'
	);
	assert.match(
		sourceIntelligenceSource,
		/findReferences[\s\S]*?resolveSemanticReferences\(preview, request, root\)/,
		'a Peek click must consume the shared semantic answer'
	);
	assert.match(
		sourceIntelligenceSource,
		/askLanguageServerToCount[\s\S]*?resolveSemanticReferences\([\s\S]*?spot\.projectRoot/,
		'the CodeLens count must consume the same answer using its captured project'
	);
	assert.match(
		sourceIntelligenceSource,
		/root: root \?\? ''/,
		'a request queued before a project switch must not use the new active root'
	);
}

console.log('sourceCodeLensKeys tests passed');
