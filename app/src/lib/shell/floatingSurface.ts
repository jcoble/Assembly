/** Run optional floating-surface background IO under the caller's lifetime. */
export function afterFloatingSurfacePaint(run: () => void): void {
  run();
}
