// Renders every working spinner design (workingSpinners.ts) to an animated WebP and a still frame
// (TSK-1564). Run by hand after changing a design; needs ffmpeg and a Playwright install:
//   PLAYWRIGHT=/path/to/node_modules/playwright-core/index.mjs node --experimental-strip-types scripts/renderSpinnerFrames.ts
// Each frame is the old CSS spinner, paused at its moment with a negative animation-delay.
import { execFileSync } from 'node:child_process';
import { mkdirSync, rmSync } from 'node:fs';
import { DESIGNS, SPINNER_IDS, type Part } from '../src/lib/shell/components/feedback/workingSpinners.ts';

const OUT = new URL('../src/lib/shell/components/feedback/spinners/', import.meta.url).pathname;
const FPS = 12;
const CYCLE = 4; // seconds, 48 frames. No design's loops fit 2 s, so each loop is rounded to a whole number of repeats in 4 s.
const SIZE = 14; // CSS px of the spinner, the size most uses show, so its fixed-px glow and strokes match
const PAD = 3; // CSS px of glow kept around it
const PIXELS = 48; // the image's side; uses up to 16 px downscale on a 2x screen

// A turn looks the same half a cycle on (every shape is mirror-symmetric), and these designs look the same
// turned 180 degrees (every part centred and point-symmetric), so they need only that much spin per repeat.
const HALF_SPIN = new Set(['gyroscope', 'gyro-armillary', 'gyro-cross', 'gyro-precess', 'gyro-cascade', 'cube']);
const fit = (seconds: number) => {
  const repeats = Math.round(CYCLE / seconds);
  return repeats ? CYCLE / repeats : 0;
};

const CSS = `body{margin:0;background:transparent}
.spinner{display:block;position:relative;margin:${PAD}px;width:${SIZE}px;height:${SIZE}px}
.axis,.part{position:absolute}.part{inset:0}
.part svg{display:block;width:100%;height:100%;overflow:visible}
.ring{border-radius:50%;border:1px solid #fff;box-shadow:0 0 1px 1px var(--c),0 0 3px var(--c),inset 0 0 1px 1px var(--c)}
.dot{border-radius:50%;background:#fff;box-shadow:0 0 3px 1px var(--c)}
.turn{animation:turn 1s cubic-bezier(.37,0,.63,1) infinite alternate paused}
.spin{animation:spin 1s linear infinite paused}
@keyframes turn{from{transform:scaleX(1)}to{transform:scaleX(-1)}}
@keyframes spin{to{transform:rotate(360deg)}}`;

// The class and style that freeze one loop at second `at`; a period of 0 holds it still.
function motion(kind: 'turn' | 'spin', period: number, delay: number, at: number) {
  if (!period) return { cls: '', style: '' };
  const direction = period < 0 ? ';animation-direction:reverse' : '';
  return { cls: kind, style: `animation-duration:${Math.abs(period)}s;animation-delay:${delay - at}s${direction}` };
}

function frame(id: keyof typeof DESIGNS, at: number) {
  const design = DESIGNS[id];
  const half = HALF_SPIN.has(id) ? 2 : 1;
  const spin = design.spin ?? 0;
  const group = motion('spin', Math.sign(spin) * fit(Math.abs(spin) / half) * half, 0, at);
  const part = (p: Part) => {
    const unit = (24 * 100) / (SIZE * p.size);
    const [x, y] = p.at ?? [50, 50];
    const turn = p.turn ? fit(p.turn) : 0;
    const loop = turn ? motion('turn', turn, (p.delay ?? 0) * (turn / p.turn!), at) : motion('spin', Math.sign(p.spin ?? 0) * fit(Math.abs(p.spin ?? 0)), 0, at);
    const shape = p.shape === 'ring' || p.shape === 'dot' ? p.shape : '';
    const path = (stroke: string, width: number, opacity = '') => `<path d="${p.shape}" stroke="${stroke}" stroke-width="${width}"${opacity}/>`;
    const svg = shape ? '' : `<svg viewBox="0 0 24 24" fill="none" stroke-linecap="round" stroke-linejoin="round">${path(p.color, 3 * unit, ' stroke-opacity="0.25"')}${path(p.color, 1.5 * unit)}${path('#fff', 0.6 * unit)}</svg>`;
    return `<span class="axis" style="left:${x - p.size / 2}%;top:${y - p.size / 2}%;width:${p.size}%;height:${p.size}%;transform:rotate(${p.tilt ?? 0}deg)">`
      + `<span class="part ${shape} ${loop.cls}" style="--c:${p.color};${loop.style}">${svg}</span></span>`;
  };
  return `<style>${CSS}</style><span class="spinner"><span class="part ${group.cls}" style="${group.style}">${design.parts.map(part).join('')}</span></span>`;
}

const { chromium } = await import(process.env.PLAYWRIGHT ?? 'playwright-core');
const browser = await chromium.launch({ headless: true });
try {
  const box = SIZE + 2 * PAD;
  const page = await browser.newPage({ viewport: { width: box, height: box }, deviceScaleFactor: PIXELS / box });
  for (const id of SPINNER_IDS) {
    const frames = `${OUT}.frames-${id}/`;
    mkdirSync(frames, { recursive: true });
    for (let index = 0; index < CYCLE * FPS; index += 1) {
      await page.setContent(frame(id, index / FPS));
      await page.screenshot({ path: `${frames}${String(index).padStart(2, '0')}.png`, omitBackground: true });
    }
    const input = ['-v', 'error', '-y', '-framerate', String(FPS), '-i', `${frames}%02d.png`];
    // libwebp_anim, not libwebp: the plain encoder blends each transparent frame over the last.
    execFileSync('ffmpeg', [...input, '-c:v', 'libwebp_anim', '-quality', '80', '-pix_fmt', 'yuva420p', '-loop', '0', `${OUT}${id}.webp`]);
    execFileSync('ffmpeg', [...input, '-c:v', 'libwebp', '-lossless', '1', '-frames:v', '1', `${OUT}${id}-still.webp`]);
    rmSync(frames, { recursive: true });
  }
} finally {
  await browser.close();
}
