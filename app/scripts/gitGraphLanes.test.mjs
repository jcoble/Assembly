/**
 * gitGraphLanes.test.mjs — the commit graph's column layout.
 *
 * `gitGraphLanes.ts` is the piece the commit history was missing: given the
 * commits in the order `git log` returns them (newest first) plus each commit's
 * parents, it works out which vertical column ("lane") every commit sits in and
 * which line segments have to be drawn in that commit's row. It is pure — no
 * state, no backend, no DOM — so everything about the picture can be checked
 * here in plain Node.
 *
 * Run: node --experimental-strip-types scripts/gitGraphLanes.test.mjs
 */

import assert from 'node:assert/strict';

import {
  assignGitGraphLanes,
  gitGraphLaneX,
  gitGraphLaneWidth,
  gitGraphRowLanesBelow
} from '../src/lib/shell/git/gitGraphLanes.ts';

/** A commit, in the shape the history command returns. */
function commit(sha, ...parentShas) {
  return { sha, parentShas };
}

/** Every edge of one row, as short tuples, so a failure is readable. */
function edges(row) {
  return row.edges.map((edge) => [edge.kind, edge.fromLane, edge.toLane]);
}

function rowFor(layout, sha) {
  const row = layout.rows.find((entry) => entry.sha === sha);
  assert.ok(row, `expected a row for ${sha}`);
  return row;
}

// ── nothing in, nothing out ─────────────────────────────────────────────────
{
  const layout = assignGitGraphLanes([]);
  assert.deepEqual(layout.rows, []);
  assert.equal(layout.laneCount, 0);
}

// ── a plain line of commits sits in one column ──────────────────────────────
{
  const layout = assignGitGraphLanes([
    commit('c', 'b'),
    commit('b', 'a'),
    commit('a') // the first commit of the repository: no parents
  ]);

  assert.equal(layout.laneCount, 1, 'a straight history needs one column');
  assert.deepEqual(
    layout.rows.map((row) => row.lane),
    [0, 0, 0]
  );

  // The newest commit has nothing above it, so its line starts at its own dot
  // and carries on downwards to its parent.
  assert.deepEqual(edges(rowFor(layout, 'c')), [['parent', 0, 0]]);
  // A commit in the middle has a line coming in from above and one going out.
  assert.deepEqual(edges(rowFor(layout, 'b')), [
    ['child', 0, 0],
    ['parent', 0, 0]
  ]);
  // The first commit ever made ends the line: nothing continues below it.
  assert.deepEqual(edges(rowFor(layout, 'a')), [['child', 0, 0]]);
  assert.equal(rowFor(layout, 'a').isRoot, true);
  assert.equal(rowFor(layout, 'c').isRoot, false);
}

// ── a merge opens a second column and closes it again ───────────────────────
{
  //   m   merge of b and c
  //   b   on the main line
  //   c   the branch that was merged in
  //   a   where both started
  const layout = assignGitGraphLanes([
    commit('m', 'b', 'c'),
    commit('b', 'a'),
    commit('c', 'a'),
    commit('a')
  ]);

  assert.equal(layout.laneCount, 2, 'the merged-in branch needs its own column');
  assert.equal(rowFor(layout, 'm').lane, 0);
  assert.equal(rowFor(layout, 'b').lane, 0);
  assert.equal(rowFor(layout, 'c').lane, 1);
  assert.equal(rowFor(layout, 'a').lane, 0);

  assert.equal(rowFor(layout, 'm').isMerge, true);
  assert.equal(rowFor(layout, 'b').isMerge, false);

  // The merge sends one line straight down to its first parent and one across
  // to the column the merged-in branch gets.
  assert.deepEqual(edges(rowFor(layout, 'm')), [
    ['parent', 0, 0],
    ['parent', 0, 1]
  ]);

  // While `b` is drawn, the merged-in branch is still waiting in column 1, so
  // its line passes straight through this row.
  assert.deepEqual(edges(rowFor(layout, 'b')), [
    ['child', 0, 0],
    ['parent', 0, 0],
    ['passing', 1, 1]
  ]);

  // `c` joins back onto the column its parent already occupies, and column 1
  // is free again afterwards.
  assert.deepEqual(edges(rowFor(layout, 'c')), [
    ['passing', 0, 0],
    ['child', 1, 1],
    ['parent', 1, 0]
  ]);

  assert.deepEqual(edges(rowFor(layout, 'a')), [['child', 0, 0]]);
  assert.equal(rowFor(layout, 'a').isRoot, true);
}

// ── a freed column is used again by the next branch ─────────────────────────
{
  const layout = assignGitGraphLanes([
    commit('m', 'b', 'c'),
    commit('b', 'a'),
    commit('c', 'a'),
    commit('a', 'z'),
    // A second tip that nothing in this window points at. It takes the first
    // free column rather than opening a new one.
    commit('t', 'z'),
    commit('z')
  ]);

  assert.equal(rowFor(layout, 't').lane, 1, 'the freed column is used again');
  assert.equal(layout.laneCount, 2);
}

// ── a parent that is already waiting gets an across-and-down line ───────────
{
  const layout = assignGitGraphLanes([
    commit('t1', 'a'),
    commit('t2', 'a'),
    commit('a')
  ]);

  assert.equal(rowFor(layout, 't1').lane, 0);
  assert.equal(rowFor(layout, 't2').lane, 1, 'a second tip gets its own column');
  // Nothing above points at `t2`, so it has no incoming line — and its parent
  // is already waiting in column 0, so its line goes across into that column
  // instead of reserving a third one.
  assert.deepEqual(edges(rowFor(layout, 't2')), [
    ['passing', 0, 0],
    ['parent', 1, 0]
  ]);
  assert.equal(rowFor(layout, 'a').lane, 0);
  assert.equal(layout.laneCount, 2);
}

// ── the same parent listed twice is drawn once ──────────────────────────────
{
  const layout = assignGitGraphLanes([commit('m', 'a', 'a'), commit('a')]);
  assert.deepEqual(edges(rowFor(layout, 'm')), [['parent', 0, 0]]);
  assert.equal(layout.laneCount, 1);
}

// ── history cut off at the window edge still draws its line ─────────────────
{
  // `b`'s parent is older than the commits we loaded. The line has to carry on
  // off the bottom of the list rather than stopping dead, because the commit is
  // there — we just did not ask for it.
  const layout = assignGitGraphLanes([commit('c', 'b'), commit('b', 'older')]);
  const last = rowFor(layout, 'b');
  assert.deepEqual(edges(last), [
    ['child', 0, 0],
    ['parent', 0, 0]
  ]);
  assert.equal(last.isRoot, false, 'a truncated history is not a root commit');
  assert.equal(last.continuesBelow, true);
}

// ── blank and repeated commit ids are dropped rather than drawn wrong ───────
{
  const layout = assignGitGraphLanes([
    commit('a', 'b'),
    commit('   '),
    commit('a', 'b'), // the same commit twice: keep the first
    commit('b')
  ]);
  assert.deepEqual(
    layout.rows.map((row) => row.sha),
    ['a', 'b']
  );
}

// ── each row says how wide it is, and where to draw each column ─────────────
{
  const layout = assignGitGraphLanes([
    commit('m', 'b', 'c'),
    commit('b', 'a'),
    commit('c', 'a'),
    commit('a')
  ]);
  assert.equal(rowFor(layout, 'm').laneCount, 2);
  assert.equal(rowFor(layout, 'a').laneCount, 1);

  assert.equal(gitGraphLaneX(0, 12, 8), 8);
  assert.equal(gitGraphLaneX(2, 12, 8), 32);
  assert.equal(gitGraphLaneWidth(0, 12, 8), 0, 'no commits means no room reserved');
  assert.equal(
    gitGraphLaneWidth(2, 12, 8),
    28,
    'two columns: the same 8px gap either side of a 12px step'
  );
}

// ── which lines a row opened in place has to keep drawing ──────────────────
{
  const layout = assignGitGraphLanes([
    commit('m', 'b', 'c'),
    commit('b', 'a'),
    commit('c', 'a'),
    commit('a')
  ]);
  // Opening the merge shows its files; both its lines carry on below the list.
  assert.deepEqual(gitGraphRowLanesBelow(rowFor(layout, 'm')), [0, 1]);
  // `b` has its own line down plus the merged-in branch passing by.
  assert.deepEqual(gitGraphRowLanesBelow(rowFor(layout, 'b')), [0, 1]);
  // `c` rejoins column 0, so column 1 stops here.
  assert.deepEqual(gitGraphRowLanesBelow(rowFor(layout, 'c')), [0]);
  // The first commit of the repository ends every line.
  assert.deepEqual(gitGraphRowLanesBelow(rowFor(layout, 'a')), []);
}

// ── the lines never leave the width the layout asked for ───────────────────
{
  const layout = assignGitGraphLanes([
    commit('m', 'b', 'c'),
    commit('b', 'd'),
    commit('c', 'd'),
    commit('d', 'e', 'f'),
    commit('e', 'g'),
    commit('f', 'g'),
    commit('g')
  ]);
  for (const row of layout.rows) {
    assert.ok(row.lane < layout.laneCount, `${row.sha} sits inside the graph`);
    for (const edge of row.edges) {
      assert.ok(
        edge.fromLane < layout.laneCount && edge.toLane < layout.laneCount,
        `${row.sha}: every line stays inside the graph`
      );
      assert.ok(
        edge.fromLane < row.laneCount && edge.toLane < row.laneCount,
        `${row.sha}: the row's own width covers its lines`
      );
    }
  }
}

console.log('gitGraphLanes.test.mjs: all checks passed');
