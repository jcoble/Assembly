// The owner's neon spinner set (TSK-1391). Every shape is drawn once and only moved by flat
// transforms; a ring "turning" in 3D is the same ring squashed along one axis. Redrawing shapes per
// frame, rotating them in real 3D, or animation-direction: reverse cost 6-20% CPU per spinner on
// macOS (measured 2026-10-07); this costs about the same as the old rotating icon.
export type Part = {
  shape: string; // 'ring', 'dot', or an SVG path in a 24x24 box
  size: number; // % of the parent
  color: string;
  at?: [number, number]; // centre, % of the parent; default the middle
  tilt?: number; // degrees; the axis a turn squashes along
  turn?: number; // seconds per half turn (squash 1 -> -1, eased, and back)
  spin?: number; // seconds per flat revolution; negative spins the other way
  delay?: number; // seconds; negative starts mid-cycle
};

export type Design = { spin?: number; parts: Part[] };

const C = '#00e5ff', M = '#ff2bd6', Y = '#ffe600', L = '#b4ff00', P = '#a259ff', O = '#ff9100';

const SQUARE = 'M5 5H19V19H5Z';
const DIAMOND = 'M12 2L22 12L12 22L2 12Z';
const TRIANGLE = 'M12 3L21.5 19.5H2.5Z';
const HEXAGON = 'M12 2L20.7 7V17L12 22L3.3 17V7Z';
const STAR = 'M12 2L14.4 9.2H22L15.8 13.8L18.2 21L12 16.6L5.8 21L8.2 13.8L2 9.2H9.6Z';
const AXLE = 'M12 1V23';
const RIDER = 'M12 1.5h0.01'; // a dot on the rim; spinning it orbits a ring of the same size
const RUNG = 'M3 12H21M3 12h0.01M21 12h0.01';

const core = (color: string): Part => ({ shape: 'dot', size: 10, color });
const around = (angle: number, radius: number): [number, number] =>
  [50 + radius * Math.cos((angle * Math.PI) / 180), 50 + radius * Math.sin((angle * Math.PI) / 180)];

export const DESIGNS = {
  gyroscope: { spin: 9, parts: [
    { shape: 'ring', size: 88, color: C, tilt: 15, turn: 1.3 },
    { shape: 'ring', size: 64, color: M, tilt: -60, turn: 1.8 },
    { shape: 'ring', size: 40, color: Y, tilt: 75, turn: 1 },
    core(C)
  ] },
  'gyro-armillary': { spin: 14, parts: [
    { shape: 'ring', size: 90, color: C, tilt: 0, turn: 2 },
    { shape: 'ring', size: 90, color: P, tilt: 60, turn: 2, delay: -0.66 },
    { shape: 'ring', size: 90, color: L, tilt: 120, turn: 2, delay: -1.33 },
    core(P)
  ] },
  'gyro-cross': { spin: -10, parts: [
    { shape: 'ring', size: 86, color: M, tilt: 0, turn: 1.4 },
    { shape: 'ring', size: 86, color: C, tilt: 90, turn: 1.4, delay: -0.7 },
    core(M)
  ] },
  'gyro-riders': { spin: 12, parts: [
    { shape: 'ring', size: 88, color: C, tilt: 20, turn: 1.6 },
    { shape: 'ring', size: 56, color: M, tilt: -50, turn: 1.2 },
    { shape: RIDER, size: 88, color: O, spin: 1.2 },
    { shape: RIDER, size: 56, color: Y, spin: -0.9 },
    core(O)
  ] },
  'gyro-precess': { spin: 5, parts: [
    { shape: 'ring', size: 90, color: P, tilt: 0, turn: 2.2 },
    { shape: AXLE, size: 100, color: Y },
    { shape: 'ring', size: 50, color: C, tilt: 90, turn: 1.1 },
    core(Y)
  ] },
  'gyro-toy': { parts: [
    { shape: 'ring', size: 90, color: C, tilt: 0, turn: 3 },
    { shape: AXLE, size: 100, color: Y },
    { shape: 'ring', size: 60, color: O, tilt: 90, turn: 0.9 },
    core(O)
  ] },
  'gyro-linked': { spin: 10, parts: [
    { shape: 'ring', size: 60, color: C, at: [36, 50], tilt: 90, turn: 1.4 },
    { shape: 'ring', size: 60, color: M, at: [64, 50], tilt: 90, turn: 1.4, delay: -0.7 }
  ] },
  'gyro-ripple': { parts: [
    { shape: 'ring', size: 90, color: C, tilt: 30, turn: 1.6 },
    { shape: 'ring', size: 66, color: P, tilt: 30, turn: 1.6, delay: -0.3 },
    { shape: 'ring', size: 42, color: M, tilt: 30, turn: 1.6, delay: -0.6 },
    core(C)
  ] },
  'gyro-torus': { spin: 8, parts: [0, 60, 120, 180, 240, 300].map((angle, index): Part => (
    { shape: 'ring', size: 36, color: index % 2 ? M : C, at: around(angle, 28), tilt: angle, turn: 1.2, delay: -index * 0.2 }
  )) },
  'gyro-cascade': { spin: 7, parts: [
    { shape: 'ring', size: 92, color: Y, tilt: 40, turn: 1 },
    { shape: 'ring', size: 70, color: O, tilt: 80, turn: 1, delay: -0.15 },
    { shape: 'ring', size: 48, color: M, tilt: 120, turn: 1, delay: -0.3 },
    core(Y)
  ] },
  cube: { spin: 8, parts: [
    { shape: SQUARE, size: 84, color: C, turn: 1.6 },
    { shape: SQUARE, size: 58, color: M, turn: 1.6, delay: -0.8 }
  ] },
  octahedron: { parts: [
    { shape: DIAMOND, size: 90, color: L, turn: 1.6 },
    { shape: 'ring', size: 90, color: C, tilt: 90, turn: 1.6 },
    core(L)
  ] },
  tetrahedron: { spin: 6, parts: [
    { shape: TRIANGLE, size: 88, color: Y, turn: 1.3 },
    { shape: TRIANGLE, size: 60, color: O, tilt: 180, turn: 1.3, delay: -0.65 }
  ] },
  icosahedron: { spin: -9, parts: [
    { shape: HEXAGON, size: 88, color: C, turn: 1.8 },
    { shape: TRIANGLE, size: 66, color: P, turn: 1.8, delay: -0.9 },
    { shape: TRIANGLE, size: 66, color: M, tilt: 180, turn: 1.8, delay: -0.9 }
  ] },
  star: { spin: 4, parts: [
    { shape: STAR, size: 92, color: Y, turn: 1.4 },
    { shape: STAR, size: 46, color: O, tilt: 36, spin: -3 }
  ] },
  dna: { parts: [14, 26, 38, 50, 62, 74, 86].map((top, index): Part => (
    { shape: RUNG, size: 80, color: index % 2 ? M : C, at: [50, top], turn: 1.4, delay: -index * 0.2 }
  )) }
} satisfies Record<string, Design>;

export type SpinnerId = keyof typeof DESIGNS;

export const SPINNER_IDS = Object.keys(DESIGNS) as SpinnerId[];

// A seed (a session's ownedId) always maps to the same design; no seed picks at random.
export function pickSpinner(seed?: string): SpinnerId {
  if (seed === undefined) return SPINNER_IDS[Math.floor(Math.random() * SPINNER_IDS.length)];
  let hash = 0;
  for (let index = 0; index < seed.length; index += 1) hash = (Math.imul(hash, 31) + seed.charCodeAt(index)) | 0;
  return SPINNER_IDS[(hash >>> 0) % SPINNER_IDS.length];
}
