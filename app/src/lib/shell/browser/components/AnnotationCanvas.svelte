<script lang="ts">
  /**
   * AnnotationCanvas.svelte — the sheet the marks are drawn on.
   *
   * A real canvas, not a stack of positioned boxes. The marks have to end up
   * burned into a picture that leaves the app, and a canvas is the only surface
   * that can both show them and export them; keeping a separate one for each
   * job is how the two would come to disagree. `drawShapes` in
   * `annotationComposite.ts` paints here and paints the export, so there is one
   * answer to what a mark looks like.
   *
   * The page underneath is a still picture of it. While a tool is armed the
   * native browser view is out of the way — nothing in the document can be
   * drawn over a native child view — so what is marked up is exactly the image
   * that gets sent.
   */
  import { drawShapes, shapeAtPoint, type AnnotationShape, type PlacedAnnotationShape } from './annotationComposite.ts';

  interface Props {
    /** Which tool is armed. `browse` leaves the still alone. */
    tool: 'browse' | 'element' | 'region' | 'drawing' | 'erasing';
    /** The marks already made, in this surface's own pixels. */
    shapes: readonly PlacedAnnotationShape[];
    /** The still of the page being marked up. */
    backdrop: string | null;
    /**
     * What the page says is under the pointer, outlined while Annotate is
     * armed. It is a box in the document rather than ink on the canvas: it
     * follows the pointer and must never be burned into the picture that goes.
     */
    highlight: { x: number; y: number; width: number; height: number } | null;
    onAdd(shape: AnnotationShape): void;
    onErase(id: string): void;
    /** Where the pointer is while Annotate is armed, and where it left. */
    onHover(point: { x: number; y: number } | null): void;
    /** A click while Annotate is armed: mark whatever is at this point. */
    onPick(point: { x: number; y: number }): void;
    /** Reports the surface's size so the export can scale the marks onto the picture. */
    onResize(size: { width: number; height: number }): void;
  }

  let { tool, shapes, backdrop, highlight, onAdd, onErase, onHover, onPick, onResize }: Props =
    $props();

  let host = $state<HTMLDivElement | null>(null);
  let canvas = $state<HTMLCanvasElement | null>(null);
  let size = $state({ width: 0, height: 0 });
  let inProgress = $state<AnnotationShape | null>(null);
  let origin: { x: number; y: number } | null = null;

  const cursor = $derived(tool === 'browse' ? 'default' : tool === 'erasing' ? 'cell' : 'crosshair');

  function pointIn(event: PointerEvent): { x: number; y: number } {
    const rect = canvas?.getBoundingClientRect();
    if (!rect) return { x: 0, y: 0 };
    return { x: event.clientX - rect.left, y: event.clientY - rect.top };
  }

  function paint(): void {
    const context = canvas?.getContext('2d');
    if (!canvas || !context) return;
    const ratio = typeof devicePixelRatio === 'number' && devicePixelRatio > 0 ? devicePixelRatio : 1;
    const width = Math.max(1, Math.round(size.width * ratio));
    const height = Math.max(1, Math.round(size.height * ratio));
    if (canvas.width !== width) canvas.width = width;
    if (canvas.height !== height) canvas.height = height;
    context.setTransform(ratio, 0, 0, ratio, 0, 0);
    context.clearRect(0, 0, size.width, size.height);
    const drawn = shapes.map((item) => item.shape);
    drawShapes(context, inProgress ? [...drawn, inProgress] : drawn);
  }

  function down(event: PointerEvent): void {
    if (event.button !== 0) return;
    if (tool === 'browse') return;
    const point = pointIn(event);
    if (tool === 'element') {
      onPick(point);
      return;
    }
    if (tool === 'erasing') {
      const hit = shapeAtPoint(shapes, point.x, point.y);
      if (hit) onErase(hit);
      return;
    }
    event.preventDefault();
    canvas?.setPointerCapture(event.pointerId);
    origin = point;
    inProgress =
      tool === 'region'
        ? { kind: 'region', x: point.x, y: point.y, width: 0, height: 0 }
        : { kind: 'stroke', points: [point] };
  }

  function move(event: PointerEvent): void {
    if (tool === 'element') {
      onHover(pointIn(event));
      return;
    }
    const current = inProgress;
    if (!current || !origin) return;
    const point = pointIn(event);
    if (current.kind === 'stroke') {
      inProgress = { kind: 'stroke', points: [...current.points, point] };
      return;
    }
    inProgress = {
      kind: 'region',
      x: Math.min(origin.x, point.x),
      y: Math.min(origin.y, point.y),
      width: Math.abs(point.x - origin.x),
      height: Math.abs(point.y - origin.y)
    };
  }

  function up(event: PointerEvent): void {
    if (!inProgress) return;
    if (canvas?.hasPointerCapture(event.pointerId)) canvas.releasePointerCapture(event.pointerId);
    const finished = inProgress;
    inProgress = null;
    origin = null;
    onAdd(finished);
  }

  function cancel(): void {
    inProgress = null;
    origin = null;
  }

  $effect(() => {
    if (!host) return;
    const observer = new ResizeObserver((entries) => {
      const box = entries[0]?.contentRect;
      if (!box) return;
      size = { width: box.width, height: box.height };
      onResize({ width: box.width, height: box.height });
    });
    observer.observe(host);
    return () => observer.disconnect();
  });

  $effect(() => {
    size;
    shapes;
    inProgress;
    paint();
  });
</script>

<div class="annotation-host" bind:this={host} data-testid="browser-annotation-host">
  {#if backdrop}
    <img class="backdrop" src={backdrop} alt="The page as it was when marking started" draggable="false" />
  {/if}
  {#if highlight}
    <div
      class="highlight"
      style:left={`${highlight.x}px`}
      style:top={`${highlight.y}px`}
      style:width={`${highlight.width}px`}
      style:height={`${highlight.height}px`}
      data-testid="browser-annotation-highlight"
    ></div>
  {/if}
  <canvas
    bind:this={canvas}
    class="ink"
    style:cursor
    style:width={`${size.width}px`}
    style:height={`${size.height}px`}
    data-testid="browser-annotation-canvas"
    aria-label="Marks on the page"
    onpointerdown={down}
    onpointermove={move}
    onpointerup={up}
    onpointercancel={cancel}
    onpointerleave={() => onHover(null)}
  ></canvas>
</div>

<style>
  .annotation-host {
    position: relative;
    display: block;
    height: 100%;
    width: 100%;
    overflow: hidden;
    background: var(--color-surface);
  }

  .backdrop {
    position: absolute;
    inset: 0;
    height: 100%;
    width: 100%;
    object-fit: contain;
    object-position: top left;
    user-select: none;
  }

  .ink {
    position: absolute;
    inset: 0;
    touch-action: none;
  }

  /* Follows the pointer, so it moves by moving — no size or position
     animation, and nothing left running when the pointer stops. */
  .highlight {
    position: absolute;
    border: 2px solid rgba(37, 99, 235, 0.95);
    border-radius: 3px;
    background: rgba(37, 99, 235, 0.14);
    pointer-events: none;
  }
</style>
