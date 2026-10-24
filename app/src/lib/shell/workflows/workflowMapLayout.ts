/**
 * Where each step's box goes on a run's Map: its column is how deep it sits in
 * `needs`, its row is its place among that column's steps in file order.
 * Computed once per workflow file (keyed by its steps and needs) and kept.
 */
export interface MapBox {
  id: string;
  column: number;
  row: number;
}

export interface MapLayout {
  boxes: MapBox[];
  columns: number;
  rows: number;
}

const layouts = new Map<string, MapLayout>();

export function layoutWorkflowMap(nodes: readonly { id: string; dependsOn: readonly string[] }[]): MapLayout {
  const key = nodes.map((node) => `${node.id}<${node.dependsOn.join(',')}`).join(';');
  const kept = layouts.get(key);
  if (kept) return kept;
  const needs = new Map(nodes.map((node) => [node.id, node.dependsOn]));
  const depths = new Map<string, number>();
  function depth(id: string, seen: Set<string>): number {
    const known = depths.get(id);
    if (known !== undefined) return known;
    if (seen.has(id) || !needs.has(id)) return -1;
    seen.add(id);
    const value = 1 + Math.max(-1, ...(needs.get(id) ?? []).map((need) => depth(need, seen)));
    depths.set(id, value);
    return value;
  }
  const rowsPerColumn: number[] = [];
  const boxes = nodes.map((node) => {
    const column = depth(node.id, new Set());
    const row = rowsPerColumn[column] ?? 0;
    rowsPerColumn[column] = row + 1;
    return { id: node.id, column, row };
  });
  const layout = { boxes, columns: rowsPerColumn.length, rows: Math.max(0, ...rowsPerColumn) };
  layouts.set(key, layout);
  return layout;
}
