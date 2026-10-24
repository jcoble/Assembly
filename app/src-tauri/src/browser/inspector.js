(() => {
  // This is a deliberately tiny, metadata-only inspector.  It never exposes a
  // page bridge, reads storage/cookies/form values, serializes HTML, or runs
  // code supplied by the page. Rust polls the one bounded result through a
  // fixed expression after the picker is armed.
  const KEY = '__mcbBrowserInspector';
  const MAX_SELECTOR_BYTES = 2048;
  const MAX_TEXT_CHARS = 500;
  const MAX_NAME_CHARS = 500;
  const MAX_CLASSES = 32;

  if (window[KEY]) return;

  const state = {
    armed: false,
    picked: null,
    hovered: null,
    previousOutline: null,
    onMove: null,
    onClick: null
  };

  function bounded(value, limit) {
    if (typeof value !== 'string') return null;
    const trimmed = value.trim();
    if (!trimmed) return null;
    // A JS string's length is UTF-16 code units. Rust applies the final UTF-8
    // byte bound before emitting the payload.
    return trimmed.slice(0, limit);
  }

  function cssPart(value) {
    if (typeof value !== 'string' || !value || value.length > 128) return null;
    if (window.CSS && typeof window.CSS.escape === 'function') return window.CSS.escape(value);
    return /^[A-Za-z_][A-Za-z0-9_-]*$/.test(value) ? value : null;
  }

  function selectorFor(element) {
    const parts = [];
    let current = element;
    while (current && current.nodeType === Node.ELEMENT_NODE && parts.length < 32) {
      let part = current.tagName.toLowerCase();
      const id = cssPart(current.getAttribute('id'));
      if (id) {
        part += `#${id}`;
        parts.unshift(part);
        break;
      }
      let classParts = [];
      for (const className of Array.from(current.classList).slice(0, MAX_CLASSES)) {
        const escaped = cssPart(className);
        if (escaped) classParts.push(`.${escaped}`);
      }
      classParts = classParts.slice(0, 4);
      part += classParts.join('');
      let sibling = current;
      let index = 1;
      while ((sibling = sibling.previousElementSibling)) {
        if (sibling.tagName === current.tagName) index += 1;
      }
      part += `:nth-of-type(${index})`;
      parts.unshift(part);
      current = current.parentElement;
    }
    const selector = parts.join(' > ');
    return selector.length <= MAX_SELECTOR_BYTES ? selector : null;
  }

  function boundedText(element) {
    const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
    let node = walker.nextNode();
    let text = '';
    while (node && text.length < MAX_TEXT_CHARS) {
      const value = typeof node.nodeValue === 'string' ? node.nodeValue : '';
      if (value) text += `${value} `;
      node = walker.nextNode();
    }
    return bounded(text.replace(/\s+/g, ' '), MAX_TEXT_CHARS);
  }

  function accessibleName(element) {
    const aria = bounded(element.getAttribute('aria-label'), MAX_NAME_CHARS);
    if (aria) return aria;
    const labelledBy = bounded(element.getAttribute('aria-labelledby'), 256);
    if (labelledBy) {
      const labels = labelledBy
        .split(/\s+/)
        .slice(0, 8)
        .map((id) => document.getElementById(id))
        .filter(Boolean)
        .map((label) => boundedText(label))
        .filter(Boolean)
        .join(' ');
      if (labels) return bounded(labels, MAX_NAME_CHARS);
    }
    return (
      bounded(element.getAttribute('alt'), MAX_NAME_CHARS) ||
      bounded(element.getAttribute('title'), MAX_NAME_CHARS) ||
      null
    );
  }

  function unavailable(reason) {
    return {
      status: 'unavailable',
      reason: bounded(reason, 200) || 'inspector-unavailable',
      url: bounded(window.location.href, 8192),
      title: bounded(document.title, MAX_NAME_CHARS)
    };
  }

  function metadataFor(element) {
    if (!(element instanceof Element)) return unavailable('no-element-at-pointer');
    if (element.tagName === 'IFRAME' || element.tagName === 'FRAME' || element.tagName === 'OBJECT') {
      try {
        if (!element.contentDocument) return unavailable('cross-origin-frame');
      } catch (_error) {
        return unavailable('cross-origin-frame');
      }
    }
    const rect = element.getBoundingClientRect();
    const classes = Array.from(element.classList).slice(0, MAX_CLASSES).map((name) => bounded(name, 128)).filter(Boolean);
    return {
      status: 'selected',
      selector: selectorFor(element),
      role: bounded(element.getAttribute('role'), 64),
      accessibleName: accessibleName(element),
      textSnippet: boundedText(element),
      rect: {
        x: Number.isFinite(rect.x) ? rect.x : 0,
        y: Number.isFinite(rect.y) ? rect.y : 0,
        width: Number.isFinite(rect.width) ? rect.width : 0,
        height: Number.isFinite(rect.height) ? rect.height : 0
      },
      classes,
      classCount: Math.min(element.classList.length, MAX_CLASSES),
      url: bounded(window.location.href, 8192),
      title: bounded(document.title, MAX_NAME_CHARS)
    };
  }

  function inspectRect(x, y, width, height) {
    // This reads only the same bounded DOM and accessibility metadata as the
    // picker. It cannot read storage, cookies, form values, or page HTML.
    const right = x + width;
    const bottom = y + height;
    const points = [
      [x + width / 2, y + height / 2],
      [x, y],
      [right, y],
      [x, bottom],
      [right, bottom]
    ];
    const seen = new Set();
    const candidates = [];
    for (const [pointX, pointY] of points) {
      for (const element of document.elementsFromPoint(pointX, pointY).slice(0, 8)) {
        if (seen.has(element)) continue;
        seen.add(element);
        const rect = element.getBoundingClientRect();
        const intersects = rect.right >= x && rect.left <= right && rect.bottom >= y && rect.top <= bottom;
        if (intersects) candidates.push(element);
      }
    }
    return metadataFor(candidates[0] ?? null);
  }

  function clearHover() {
    if (!state.hovered) return;
    if (state.previousOutline === null) state.hovered.style.outline = '';
    else state.hovered.style.outline = state.previousOutline;
    state.hovered = null;
    state.previousOutline = null;
  }

  function disarm(clearPicked) {
    state.armed = false;
    document.removeEventListener('mousemove', state.onMove, true);
    document.removeEventListener('click', state.onClick, true);
    clearHover();
    if (clearPicked) state.picked = null;
  }

  function arm() {
    disarm(true);
    state.armed = true;
    state.onMove = (event) => {
      if (!state.armed) return;
      const element = event.target instanceof Element
        ? event.target
        : document.elementFromPoint(event.clientX, event.clientY);
      if (!(element instanceof Element) || element === state.hovered) return;
      clearHover();
      state.hovered = element;
      state.previousOutline = element.style.outline;
      element.style.outline = '2px solid #2f80ed';
    };
    state.onClick = (event) => {
      if (!state.armed) return;
      event.preventDefault();
      event.stopPropagation();
      state.picked = metadataFor(event.target instanceof Element ? event.target : null);
      disarm(false);
    };
    document.addEventListener('mousemove', state.onMove, true);
    document.addEventListener('click', state.onClick, true);
  }

  const api = Object.freeze({
    arm,
    inspectRect,
    cancel: () => disarm(true),
    take: () => {
      const result = state.picked;
      state.picked = null;
      return result;
    }
  });
  Object.defineProperty(window, KEY, {
    configurable: false,
    enumerable: false,
    writable: false,
    value: api
  });
})();
