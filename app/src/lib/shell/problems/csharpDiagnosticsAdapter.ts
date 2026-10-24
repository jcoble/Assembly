import type { RawProblemDiagnostic } from './problemsStore.svelte';

const diagnosticsByRoot = new Map<string, RawProblemDiagnostic[]>();
/** How many projects keep their diagnostics; the oldest is dropped past this. */
const rootLimit = 3;
const listeners = new Set<(root: string) => void>();

function normalizedRoot(root: string): string {
  return root.replaceAll('\\', '/').replace(/\/+$/, '');
}

export function recordNativeCsharpDiagnostics(
  root: string,
  diagnostics: RawProblemDiagnostic[]
): void {
  const key = normalizedRoot(root);
  // Delete first: `Map.set` on a key that is already there does not move it to
  // the end, and the oldest key is the one evicted.
  diagnosticsByRoot.delete(key);
  diagnosticsByRoot.set(key, diagnostics.map((diagnostic) => ({ ...diagnostic })));
  while (diagnosticsByRoot.size > rootLimit) {
    const oldest = diagnosticsByRoot.keys().next().value;
    if (oldest === undefined) break;
    diagnosticsByRoot.delete(oldest);
  }
  for (const listener of listeners) listener(key);
}

export function nativeCsharpDiagnosticsForRoot(root: string): RawProblemDiagnostic[] {
  return diagnosticsByRoot.get(normalizedRoot(root))?.map((diagnostic) => ({ ...diagnostic })) ?? [];
}

export function onNativeCsharpDiagnosticsChanged(listener: (root: string) => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}
