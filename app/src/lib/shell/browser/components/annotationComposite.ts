/**
 * annotationComposite.ts — the marks drawn on a page, and burning them in.
 *
 * There is one description of a mark and one routine that paints it. The live
 * surface in the panel paints it onto a canvas sized to the panel; Attach
 * paints the same marks onto the captured image at the image's own size. Two
 * drawing systems would drift within a week, which is exactly what happened to
 * the div-based layer this replaces: it could show a mark but never export one.
 *
 * The geometry here is pure — no DOM — so it can be tested under Node. Only
 * `drawShapes` and `compositeAnnotations` need a canvas.
 */
import type { BrowserMarkupCapture } from '../browserTypes.ts';

export type AnnotationShape =
  | { kind: 'region'; x: number; y: number; width: number; height: number }
  | { kind: 'stroke'; points: readonly { x: number; y: number }[] }
  | { kind: 'element'; x: number; y: number; width: number; height: number; tag: string };

export interface PlacedAnnotationShape {
  id: string;
  shape: AnnotationShape;
}

export interface AnnotationSurfaceSize {
  width: number;
  height: number;
}

/** How near a click has to be to a freehand line to count as being on it. */
export const STROKE_HIT_TOLERANCE = 8;

/** The ink. These are literal colors on purpose: the marks are burned into a
 *  raster image that leaves the app, where a theme token means nothing. */
const MARK_LINE = 'rgba(37, 99, 235, 0.95)';
const MARK_FILL = 'rgba(37, 99, 235, 0.14)';
const MARK_WIDTH = 2;

function box(shape: AnnotationShape): { width: number; height: number } {
  if (shape.kind === 'stroke') {
    const xs = shape.points.map((point) => point.x);
    const ys = shape.points.map((point) => point.y);
    return {
      width: Math.max(...xs) - Math.min(...xs),
      height: Math.max(...ys) - Math.min(...ys)
    };
  }
  return { width: Math.abs(shape.width), height: Math.abs(shape.height) };
}

/**
 * Whether a mark is worth keeping. A box needs at least a pixel on both sides
 * or it paints nothing; a freehand line needs two points and a pixel of travel
 * on either axis, so a straight vertical line — no width at all — still counts.
 */
function isDrawable(shape: AnnotationShape): boolean {
  const size = box(shape);
  if (shape.kind === 'stroke') {
    if (shape.points.length < 2) return false;
    return Math.max(size.width, size.height) >= 1;
  }
  return size.width >= 1 && size.height >= 1;
}

/** Drops any shape whose id is in `erased`, and any shape smaller than one pixel. */
export function liveShapes(
  shapes: readonly PlacedAnnotationShape[],
  erased: ReadonlySet<string>
): PlacedAnnotationShape[] {
  return shapes.filter((item) => !erased.has(item.id) && isDrawable(item.shape));
}

function withinRect(
  shape: { x: number; y: number; width: number; height: number },
  x: number,
  y: number
): boolean {
  const left = Math.min(shape.x, shape.x + shape.width);
  const top = Math.min(shape.y, shape.y + shape.height);
  return (
    x >= left &&
    x <= left + Math.abs(shape.width) &&
    y >= top &&
    y <= top + Math.abs(shape.height)
  );
}

/** Shortest distance from a point to a line segment. */
function distanceToSegment(
  px: number,
  py: number,
  ax: number,
  ay: number,
  bx: number,
  by: number
): number {
  const dx = bx - ax;
  const dy = by - ay;
  const lengthSquared = dx * dx + dy * dy;
  if (lengthSquared === 0) return Math.hypot(px - ax, py - ay);
  const along = Math.min(1, Math.max(0, ((px - ax) * dx + (py - ay) * dy) / lengthSquared));
  return Math.hypot(px - (ax + along * dx), py - (ay + along * dy));
}

function withinStroke(
  points: readonly { x: number; y: number }[],
  x: number,
  y: number
): boolean {
  for (let index = 1; index < points.length; index += 1) {
    const from = points[index - 1];
    const to = points[index];
    if (distanceToSegment(x, y, from.x, from.y, to.x, to.y) <= STROKE_HIT_TOLERANCE) return true;
  }
  return false;
}

/** Which shape an erase click at (x, y) removes — the topmost hit, or null. */
export function shapeAtPoint(
  shapes: readonly PlacedAnnotationShape[],
  x: number,
  y: number
): string | null {
  for (let index = shapes.length - 1; index >= 0; index -= 1) {
    const { id, shape } = shapes[index];
    const hit = shape.kind === 'stroke' ? withinStroke(shape.points, x, y) : withinRect(shape, x, y);
    if (hit) return id;
  }
  return null;
}

/**
 * Scales shapes from layer coordinates onto the captured image's pixel grid.
 * Each axis takes its own factor: the capture may be at a device pixel ratio
 * that does not match the layer's shape, and forcing one factor onto both would
 * slide every mark off what it was pointing at.
 */
export function scaleShapes(
  shapes: readonly AnnotationShape[],
  layer: AnnotationSurfaceSize,
  image: AnnotationSurfaceSize
): AnnotationShape[] {
  const scaleX = layer.width > 0 ? image.width / layer.width : 1;
  const scaleY = layer.height > 0 ? image.height / layer.height : 1;
  return shapes.map((shape) => {
    if (shape.kind === 'stroke') {
      return {
        kind: 'stroke',
        points: shape.points.map((point) => ({ x: point.x * scaleX, y: point.y * scaleY }))
      };
    }
    const scaled = {
      x: shape.x * scaleX,
      y: shape.y * scaleY,
      width: shape.width * scaleX,
      height: shape.height * scaleY
    };
    return shape.kind === 'element' ? { kind: 'element', ...scaled, tag: shape.tag } : { kind: 'region', ...scaled };
  });
}

/**
 * Paint the marks. `scale` is how much bigger the surface is than the
 * coordinates, so line weight stays readable on a high-resolution capture.
 */
export function drawShapes(
  context: CanvasRenderingContext2D,
  shapes: readonly AnnotationShape[],
  scale = 1
): void {
  context.save();
  context.lineJoin = 'round';
  context.lineCap = 'round';
  context.lineWidth = MARK_WIDTH * Math.max(1, scale);
  context.strokeStyle = MARK_LINE;
  context.fillStyle = MARK_FILL;
  for (const shape of shapes) {
    if (shape.kind === 'stroke') {
      context.beginPath();
      context.moveTo(shape.points[0].x, shape.points[0].y);
      for (const point of shape.points.slice(1)) context.lineTo(point.x, point.y);
      context.stroke();
      continue;
    }
    context.beginPath();
    context.rect(shape.x, shape.y, shape.width, shape.height);
    context.fill();
    context.stroke();
  }
  context.restore();
}

/** A file name a person can recognise in a transcript, taken from the address. */
export function captureFileName(url: string): string {
  let host = 'page';
  try {
    host = new URL(url).hostname || 'page';
  } catch {
    host = 'page';
  }
  return `${host.replace(/[^a-z0-9.-]+/gi, '-')}-markup.png`;
}

function blobFrom(capture: BrowserMarkupCapture): Blob {
  return new Blob([Uint8Array.from(capture.bytes)], { type: capture.mimeType || 'image/png' });
}

function fileFromCanvasPng(canvas: HTMLCanvasElement, name: string): File {
  const dataUrl = canvas.toDataURL('image/png');
  const comma = dataUrl.indexOf(',');
  if (!dataUrl.startsWith('data:image/png') || comma < 0) {
    throw new Error('The marked-up picture could not be written.');
  }

  const binary = atob(dataUrl.slice(comma + 1));
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index += 1) {
    bytes[index] = binary.charCodeAt(index);
  }
  return new File([bytes], name, { type: 'image/png' });
}

/**
 * Burns the shapes onto the capture with a canvas and answers a PNG File named
 * after the page, ready for `saveConversationClipboardImage`.
 */
export async function compositeAnnotations(
  capture: BrowserMarkupCapture,
  shapes: readonly AnnotationShape[],
  layer: AnnotationSurfaceSize,
  options: { url?: string } = {}
): Promise<File> {
  const name = captureFileName(options.url ?? '');
  const source = blobFrom(capture);
  const bitmap = await createImageBitmap(source);
  const width = capture.width || bitmap.width;
  const height = capture.height || bitmap.height;

  const canvas = document.createElement('canvas');
  canvas.width = width;
  canvas.height = height;
  const context = canvas.getContext('2d');
  if (!context) {
    bitmap.close();
    throw new Error('This build cannot draw on a canvas, so the marks cannot be added to the picture.');
  }
  context.drawImage(bitmap, 0, 0, width, height);
  bitmap.close();

  drawShapes(context, scaleShapes(shapes, layer, { width, height }), layer.width > 0 ? width / layer.width : 1);

  return fileFromCanvasPng(canvas, name);
}
