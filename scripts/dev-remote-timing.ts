import { performance } from 'node:perf_hooks';

// Durations, not scheduled work: every measurement ends with its owning operation.
export function stage(name: string) {
  const started = performance.now();
  return (details: Record<string, unknown> = {}) => {
    const ms = Math.round((performance.now() - started) * 100) / 100;
    console.log('[dev-sync timing] ' + JSON.stringify({ stage: name, ms, ...details }));
    return ms;
  };
}
