/**
 * gitGraphLanes.ts — which column each commit is drawn in, and which lines run
 * through its row.
 *
 * The commit list the backend returns is flat: one commit per line, newest
 * first, each one naming its parents. That is enough to draw the branching
 * picture next to it, but somebody has to work out the picture. This file is
 * that somebody, and it is deliberately the only place that arithmetic lives:
 *
 *  - **Pure.** No state, no backend, no DOM, no dates. Same commits in, same
 *    layout out, every time — which is why `scripts/gitGraphLanes.test.mjs` can
 *    check the whole picture in plain Node.
 *  - **It knows nothing about pixels.** It returns column numbers. The two
 *    helpers at the bottom turn a column number into an x position, and they
 *    take the spacing as arguments so the component owns the look.
 *
 * HOW THE COLUMNS ARE WORKED OUT
 * Reading top to bottom (newest commit first), we keep a list of columns, each
 * either empty or holding the commit id we expect to meet further down. For
 * each commit:
 *
 *   1. If a column is already waiting for this commit, that is its column.
 *      Otherwise it is a branch tip as far as this list is concerned, and it
 *      takes the leftmost empty column.
 *   2. Its first parent carries on in the same column — that keeps the main
 *      line of history straight down the left.
 *   3. Every other parent (a merge has two or more) either joins a column that
 *      is already waiting for it, or takes the leftmost empty column.
 *   4. A commit with no parents at all is the first commit of the repository:
 *      its column ends there and is free for whatever comes next.
 *
 * WHAT AN EDGE MEANS
 * Each row hands back the line segments to draw inside that one row's band:
 *
 *   'child'   a line entering the row from above and ending at this commit's
 *             dot (drawn from the top of the row to the middle).
 *   'parent'  a line leaving this commit's dot and carrying on below (drawn
 *             from the middle of the row to the bottom). `toLane` differs from
 *             `fromLane` when the parent lives in another column, which is the
 *             sideways stroke a merge or a branch join draws.
 *   'passing' a line that has nothing to do with this commit and simply goes
 *             straight through the row.
 *
 * A NOTE ON TRUNCATED HISTORY
 * The panel asks for a limited number of commits, so the oldest rows often name
 * parents that were never loaded. Their line is still drawn heading off the
 * bottom of the list (`continuesBelow`), because the history really does carry
 * on — we simply did not ask for it. Only a commit with no parents at all is
 * treated as the end of the line (`isRoot`).
 */

/** The two fields this layout needs from a commit. */
export interface GitGraphLaneCommit {
  sha: string;
  parentShas: readonly string[];
}

export type GitGraphLaneEdgeKind = 'child' | 'parent' | 'passing';

/** One line segment to draw inside a single row's band. */
export interface GitGraphLaneEdge {
  kind: GitGraphLaneEdgeKind;
  /** The column the segment starts in, at the top of the row. */
  fromLane: number;
  /** The column the segment ends in, at the bottom of the row. */
  toLane: number;
}

export interface GitGraphLaneRow {
  sha: string;
  /** The column this commit's dot sits in, counting from 0 on the left. */
  lane: number;
  edges: GitGraphLaneEdge[];
  /** How many columns this row alone needs — for a per-row width. */
  laneCount: number;
  /** More than one parent: this commit brought another line of work in. */
  isMerge: boolean;
  /** No parents at all: the first commit of the repository. */
  isRoot: boolean;
  /** Its line carries on past the bottom of the loaded list. */
  continuesBelow: boolean;
}

export interface GitGraphLaneLayout {
  rows: GitGraphLaneRow[];
  /** The widest the graph gets anywhere in the list. */
  laneCount: number;
}

/** Leftmost column that is free right now, adding one on the end if need be. */
function claimLane(lanes: (string | null)[], sha: string | null): number {
  const free = lanes.indexOf(null);
  if (free >= 0) {
    lanes[free] = sha;
    return free;
  }
  lanes.push(sha);
  return lanes.length - 1;
}

/**
 * Work out the whole picture for a list of commits in the order the history
 * command returns them (newest first).
 *
 * Commits with a blank id, and a commit id that has already been seen, are
 * dropped: drawing them would put two dots on one line of history and every
 * column below would be wrong.
 */
export function assignGitGraphLanes(
  commits: readonly GitGraphLaneCommit[]
): GitGraphLaneLayout {
  const rows: GitGraphLaneRow[] = [];
  /** Column -> the commit id we expect further down, or null when free. */
  const lanes: (string | null)[] = [];
  const seen = new Set<string>();
  let laneCount = 0;

  for (const commit of commits) {
    const sha = (commit?.sha ?? '').trim();
    if (sha === '' || seen.has(sha)) continue;
    seen.add(sha);

    // Every parent named once, blanks dropped, order kept.
    const parents: string[] = [];
    for (const raw of commit.parentShas ?? []) {
      const parent = (raw ?? '').trim();
      if (parent !== '' && !parents.includes(parent)) parents.push(parent);
    }

    const waiting = lanes.indexOf(sha);
    const lane = waiting >= 0 ? waiting : claimLane(lanes, sha);
    const edges: GitGraphLaneEdge[] = [];

    // Lines that were already on their way down and have nothing to do with
    // this commit keep going straight through the row.
    for (let index = 0; index < lanes.length; index += 1) {
      if (index === lane || lanes[index] === null) continue;
      edges.push({ kind: 'passing', fromLane: index, toLane: index });
    }

    // Something above pointed at this commit, so its line arrives at the dot.
    if (waiting >= 0) edges.push({ kind: 'child', fromLane: lane, toLane: lane });

    if (parents.length === 0) {
      // The first commit of the repository: nothing continues below it.
      lanes[lane] = null;
    } else {
      const [firstParent, ...otherParents] = parents;
      // Another column may already be on its way down to this same parent —
      // that is what a branch coming back together looks like. Join it instead
      // of expecting the same commit in two places, which would draw its line
      // twice and leave a column waiting for a commit that never comes.
      const alreadyWaiting = lanes.findIndex(
        (value, index) => index !== lane && value === firstParent
      );
      if (alreadyWaiting >= 0) {
        lanes[lane] = null;
        edges.push({ kind: 'parent', fromLane: lane, toLane: alreadyWaiting });
      } else {
        lanes[lane] = firstParent;
        edges.push({ kind: 'parent', fromLane: lane, toLane: lane });
      }

      for (const parent of otherParents) {
        const existing = lanes.indexOf(parent);
        const parentLane = existing >= 0 ? existing : claimLane(lanes, parent);
        edges.push({ kind: 'parent', fromLane: lane, toLane: parentLane });
      }
    }

    // Left to right, and within one column: what passes through, then what
    // arrives, then what leaves. A stable sort keeps that order.
    edges.sort((left, right) => left.fromLane - right.fromLane);

    const rowLaneCount = edges.reduce(
      (widest, edge) => Math.max(widest, edge.fromLane + 1, edge.toLane + 1),
      lane + 1
    );
    laneCount = Math.max(laneCount, rowLaneCount);

    rows.push({
      sha,
      lane,
      edges,
      laneCount: rowLaneCount,
      isMerge: parents.length > 1,
      isRoot: parents.length === 0,
      continuesBelow: parents.length > 0
    });
  }

  // Trailing columns nothing ever used (they can only appear if a lane was
  // claimed and freed in the same row) do not count towards the width.
  return { rows, laneCount };
}

/**
 * The columns whose lines carry on below this row — what a row opened in place
 * has to keep drawing behind its file list so the graph is not cut in half.
 */
export function gitGraphRowLanesBelow(row: GitGraphLaneRow): number[] {
  const lanes = new Set<number>();
  for (const edge of row.edges) {
    if (edge.kind === 'passing' || edge.kind === 'parent') lanes.add(edge.toLane);
  }
  return [...lanes].sort((left, right) => left - right);
}

/** Where the middle of a column sits, in pixels from the left of the graph. */
export function gitGraphLaneX(lane: number, spacing: number, offset: number): number {
  return offset + lane * spacing;
}

/** How much room the graph needs: the same gap either side of the columns. */
export function gitGraphLaneWidth(
  laneCount: number,
  spacing: number,
  offset: number
): number {
  if (laneCount <= 0) return 0;
  return offset * 2 + (laneCount - 1) * spacing;
}
