/**
 * The rail's reading of an age, coarsening as it grows: seconds under a minute
 * ("38s"), whole minutes under an hour ("11m"), whole hours under a day
 * ("2h"), then whole days ("3d").
 */
export function formatRailElapsed(elapsedMs: number): string {
  const totalSeconds = Math.max(0, Math.floor(elapsedMs / 1_000));
  if (totalSeconds < 60) return `${totalSeconds}s`;

  const totalMinutes = Math.floor(totalSeconds / 60);
  if (totalMinutes < 60) return `${totalMinutes}m`;

  const totalHours = Math.floor(totalMinutes / 60);
  if (totalHours < 24) return `${totalHours}h`;

  return `${Math.floor(totalHours / 24)}d`;
}
