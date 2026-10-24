/** Reveal the selected tab centrally so either neighbor remains reachable. */
export function revealTab(strip: HTMLElement | null, tab: Element | null): void {
  if (!strip || !tab || strip.scrollWidth <= strip.clientWidth) return;
  const item = tab.getBoundingClientRect();
  const viewport = strip.getBoundingClientRect();
  strip.scrollTo({ left: strip.scrollLeft + item.left - viewport.left
    + (item.width - strip.clientWidth) / 2 });
}

/** One continuous tab track: native touch scrolling, wheel, and mouse/trackpad dragging. */
export function scrollTabStrip(strip: HTMLElement): { destroy(): void } {
  let pointer: number | null = null;
  let originX = 0;
  let originScroll = 0;
  let dragged = false;

  function wheel(event: WheelEvent): void {
    if (Math.abs(event.deltaX) >= Math.abs(event.deltaY)) return;
    const limit = strip.scrollWidth - strip.clientWidth;
    if (limit <= 0) return;
    const unit = event.deltaMode === WheelEvent.DOM_DELTA_LINE ? 16
      : event.deltaMode === WheelEvent.DOM_DELTA_PAGE ? strip.clientWidth : 1;
    const next = Math.max(0, Math.min(limit, strip.scrollLeft + event.deltaY * unit));
    if (next === strip.scrollLeft) return;
    event.preventDefault();
    strip.scrollTo({ left: next, behavior: 'instant' });
  }

  function move(event: PointerEvent): void {
    if (event.pointerId !== pointer) return;
    const delta = event.clientX - originX;
    if (!dragged && Math.abs(delta) < 5) return;
    dragged = true;
    strip.setPointerCapture(event.pointerId);
    event.preventDefault();
    strip.scrollTo({ left: originScroll - delta, behavior: 'instant' });
  }

  function end(): void {
    if (pointer !== null && strip.hasPointerCapture(pointer)) strip.releasePointerCapture(pointer);
    pointer = null;
    window.removeEventListener('pointermove', move);
    window.removeEventListener('pointerup', end);
    window.removeEventListener('pointercancel', end);
  }

  function start(event: PointerEvent): void {
    dragged = false;
    if (event.button !== 0 || event.pointerType !== 'mouse'
      || strip.scrollWidth <= strip.clientWidth) return;
    end();
    pointer = event.pointerId;
    originX = event.clientX;
    originScroll = strip.scrollLeft;
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', end);
    window.addEventListener('pointercancel', end);
  }

  function click(event: MouseEvent): void {
    if (!dragged || event.detail === 0) return;
    // Releasing a swipe must not select a tab or activate its close button.
    event.preventDefault();
    event.stopImmediatePropagation();
    dragged = false;
  }

  strip.addEventListener('pointerdown', start);
  strip.addEventListener('click', click, true);
  strip.addEventListener('wheel', wheel, { passive: false });
  return {
    destroy() {
      end();
      strip.removeEventListener('pointerdown', start);
      strip.removeEventListener('click', click, true);
      strip.removeEventListener('wheel', wheel);
    }
  };
}
