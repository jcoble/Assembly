// Lag meter, in counters mode only (?counters=1): the gaps between animation
// frames, and composer key latency, from a keydown to the frame after the one
// that handled it. Kept in plain numbers and copied to `lag` twice a second, so
// the meter doesn't re-render the status bar every frame.
export const lag = $state({ jank50: 0, jank100: 0, worst5s: 0, worstRun: 0, keyWorst: 0, keyMedian: 0 });

const WINDOW_MS = 5000;
const KEYS = 20;
let on = false;
let jank50 = 0;
let jank100 = 0;
let worstRun = 0;
const frames: { at: number; gap: number }[] = [];
const keys: number[] = [];

export function startLagMeter() {
  if (on) return;
  on = true;
  let last = performance.now();
  // A hidden window gets no frames; its return isn't a slow frame.
  document.addEventListener('visibilitychange', () => (last = performance.now()));
  const frame = (now: number) => {
    const gap = now - last;
    last = now;
    if (gap > 50) jank50++;
    if (gap > 100) jank100++;
    worstRun = Math.max(worstRun, gap);
    frames.push({ at: now, gap });
    while (frames[0]!.at < now - WINDOW_MS) frames.shift();
    requestAnimationFrame(frame);
  };
  requestAnimationFrame(frame);
  const publish = () => {
    const sorted = [...keys].sort((a, b) => a - b);
    Object.assign(lag, {
      jank50,
      jank100,
      worst5s: Math.round(Math.max(0, ...frames.map((f) => f.gap))),
      worstRun: Math.round(worstRun),
      keyWorst: Math.round(Math.max(0, ...keys)),
      keyMedian: Math.round(sorted[Math.floor(sorted.length / 2)] ?? 0),
    });
    setTimeout(publish, 500);
  };
  publish();
}

export function keyPressed(event: KeyboardEvent) {
  if (!on) return;
  const at = event.timeStamp;
  requestAnimationFrame(() =>
    requestAnimationFrame(() => {
      keys.push(performance.now() - at);
      if (keys.length > KEYS) keys.shift();
    }),
  );
}
