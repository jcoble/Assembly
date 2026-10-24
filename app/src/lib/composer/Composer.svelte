<script lang="ts">
  import { onDestroy } from 'svelte';
  import { trackedListen } from '../debug.svelte';
  import { keyPressed } from '../lag.svelte';
  import { getConfig, setConfig, uploadAttachment, type ThreadConfig } from '../api';
  import { call } from '../host/call';
  import { fileUrl } from '../host/fileUrl';
  import { on } from '../host/on';
  import { pickFiles } from '../host/pickFiles';
  import { baseName, formatBytes, insertAt, pathsText, rejection, thumbnailType, withAttachments, type AttachmentRef } from './attachments';
  import FullImage from './FullImage.svelte';
  import { contextMeter } from './contextMeter';
  import { slashMatches, slashPick, slashQuery, type SlashCommand } from './slashMenu';

  let {
    token,
    threadId,
    machine,
    signal,
    busy,
    context,
    onsend,
    onstop,
  }: {
    token: string;
    threadId: string;
    // The machine the thread runs on; null for this Mac.
    machine: string | null;
    signal: AbortSignal;
    busy: boolean;
    // The agent's latest context window report, or null before it sends one.
    context: { used: number; size: number } | null;
    onsend: (content: string) => Promise<void>;
    onstop: () => void;
  } = $props();

  // Images stay local until send. The chip shows the Rust-made WebP thumbnail,
  // never the original; every object URL is revoked on remove, send and unmount.
  // A picked or dropped image is a `path` that Rust reads and uploads, so its
  // bytes never cross invoke; only a pasted image is a `file` sent as base64.
  type Pending = { id: number; name: string; size?: number; file?: File; path?: string; thumb?: Blob; url?: string };
  let pending = $state<Pending[]>([]);
  let draft = $state('');
  let error = $state('');
  let uploading = $state(false);
  let dragging = $state(false);
  let note = $state('');
  let input = $state<HTMLTextAreaElement>();
  const meter = $derived(contextMeter(context));
  const RING = 2 * Math.PI * 6;
  let full = $state<{ url: string; name: string }>();
  let nextId = 0;
  const thumbsReady = $derived(pending.every((p) => p.url));

  // The agent's own menus (model, effort, mode…). Read again when a turn ends, since the agent
  // can change a value itself (leaving plan mode, say).
  let config = $state<ThreadConfig>({ options: null, choices: {} });
  const menus = $derived((config.options ?? []).filter((o) => o.type === 'select' && o.options?.length));
  $effect(() => {
    if (busy) return;
    getConfig(token, threadId, signal).then(
      (read) => (config = read),
      (e) => { if (!signal.aborted) error = `Could not read the session settings: ${String(e)}`; },
    );
  });

  // The "/" menu. Esc hides it until the draft changes again.
  let menuIndex = $state(0);
  let menuDismissed = $state(false);
  let menuList = $state<HTMLUListElement>();
  const query = $derived(slashQuery(draft));
  const matches = $derived(query === null || menuDismissed ? [] : slashMatches(config.commands ?? [], query));
  $effect(() => {
    menuList?.children[menuIndex]?.scrollIntoView({ block: 'nearest' });
  });

  function pickCommand(command: SlashCommand) {
    const { draft: picked, select } = slashPick(command);
    draft = picked;
    menuDismissed = true;
    queueMicrotask(() => {
      input?.focus();
      input?.setSelectionRange(...select);
    });
  }

  // Arrows move, Enter or Tab picks, Esc closes. True when the key was the menu's.
  function menuKey(event: KeyboardEvent): boolean {
    if (!matches.length) return false;
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      const step = event.key === 'ArrowDown' ? 1 : -1;
      menuIndex = (Math.min(menuIndex, matches.length - 1) + step + matches.length) % matches.length;
    } else if ((event.key === 'Enter' && !event.shiftKey) || event.key === 'Tab') {
      pickCommand(matches[Math.min(menuIndex, matches.length - 1)]!);
    } else if (event.key === 'Escape') {
      menuDismissed = true;
    } else {
      return false;
    }
    event.preventDefault();
    return true;
  }

  async function choose(configId: string, value: string) {
    try {
      config = await setConfig(token, threadId, configId, value, signal);
    } catch (e) {
      if (!signal.aborted) error = `Could not change the setting: ${String(e)}`;
    }
  }

  // The shell's native drops give paths, and the page gets no HTML5 drop events for them.
  const unlisten = trackedListen(on('drop', ({ type, paths }) => {
    if (type === 'enter') dragging = paths.length > 0;
    else if (type === 'leave') dragging = false;
    else {
      dragging = false;
      dropped(paths);
    }
  }));

  onDestroy(() => {
    void unlisten.then((stop) => stop());
    for (const item of pending) {
      if (item.url) URL.revokeObjectURL(item.url);
    }
    if (full) URL.revokeObjectURL(full.url);
  });

  function attach(files: File[]) {
    error = '';
    for (const file of files) {
      const reason = rejection(file.type, file.size);
      if (reason) error = `${file.name} is ${reason}.`;
      else void makeThumb({ id: nextId++, name: file.name, size: file.size, file });
    }
  }

  // Rust checks the type and size of each path when it makes the thumbnail.
  function attachPaths(paths: string[]) {
    error = '';
    for (const path of paths) void makeThumb({ id: nextId++, name: baseName(path), path });
  }

  async function pick() {
    try {
      attachPaths(await pickFiles());
    } catch (e) {
      error = String(e);
    }
  }

  async function makeThumb(added: Pending) {
    pending.push(added);
    try {
      const { thumbnail, size } = added.file
        ? {
            thumbnail: await call<string>('attachment.thumbnail', { data: new Uint8Array(await added.file.arrayBuffer()).toBase64() }),
            size: added.file.size,
          }
        : await call<{ thumbnail: string; size: number }>('attachment.thumbnail_path', { path: added.path });
      const item = pending.find((p) => p.id === added.id);
      if (signal.aborted || !item) return;
      item.size = size;
      const bytes = Uint8Array.fromBase64(thumbnail);
      item.thumb = new Blob([bytes], { type: thumbnailType(bytes) });
      item.url = URL.createObjectURL(item.thumb);
    } catch (e) {
      if (signal.aborted) return;
      error = `${added.name}: ${String(e)}`;
      pending = pending.filter((p) => p.id !== added.id);
    }
  }

  function remove(id: number) {
    const item = pending.find((p) => p.id === id);
    if (item?.url) URL.revokeObjectURL(item.url);
    pending = pending.filter((p) => p.id !== id);
  }

  // A path is shown through the shell's asset protocol.
  function open(item: Pending) {
    full = { url: item.file ? URL.createObjectURL(item.file) : fileUrl(item.path!), name: item.name };
  }

  function close() {
    if (full) URL.revokeObjectURL(full.url);
    full = undefined;
  }

  async function send() {
    const text = draft.trim();
    // While a turn runs, a send steers it (RunRoutes /send).
    if ((!text && !pending.length) || uploading || !thumbsReady) return;
    error = '';
    uploading = true;
    const refs: AttachmentRef[] = [];
    try {
      for (const item of pending) {
        const { path } = item.path
          ? await call<{ path: string }>('attachment.upload_path', { threadId, path: item.path })
          : await uploadAttachment(token, threadId, item.file!, signal);
        const thumb = await uploadAttachment(token, threadId, new File([item.thumb!], 'thumb', { type: item.thumb!.type }), signal);
        refs.push({ path, name: item.name, thumbnail: thumb.path });
      }
    } catch (e) {
      if (!signal.aborted) error = `Could not upload the image: ${String(e)}`;
      return;
    } finally {
      uploading = false;
    }
    for (const item of pending) URL.revokeObjectURL(item.url!);
    pending = [];
    draft = '';
    await onsend(withAttachments(text, refs));
  }

  function onkeydown(event: KeyboardEvent) {
    keyPressed(event);
    if (menuKey(event)) return;
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      void send();
    }
  }

  // Dropped images attach. Anything else goes into the draft as its path, which only means something to an agent
  // on this Mac.
  function dropped(paths: string[]) {
    const images = paths.filter((path) => IMAGE.test(path));
    attachPaths(images);
    const others = paths.filter((path) => !IMAGE.test(path));
    if (!others.length) return;
    if (machine) {
      note = `These files are on your Mac; this thread runs on ${machine}.`;
      return;
    }
    const caret = input?.selectionStart ?? draft.length;
    const next = insertAt(draft, caret, pathsText(others));
    draft = next.draft;
    requestAnimationFrame(() => input?.setSelectionRange(next.caret, next.caret));
    input?.focus();
  }
  const IMAGE = /\.(png|jpe?g|gif|webp)$/i;

  // A paste with files attaches them; a text paste is left alone.
  function onpaste(event: ClipboardEvent) {
    const files = [...(event.clipboardData?.files ?? [])];
    if (!files.length) return;
    event.preventDefault();
    attach(files);
  }

</script>

{#if error}
  <div class="error">{error} <button class="link" onclick={() => (error = '')}>Dismiss</button></div>
{/if}
<div
  class="box"
  class:dragging
  role="group"
  aria-label="Message composer"
>
  {#if pending.length}
    <div class="chips">
      {#each pending as item (item.id)}
        <figure>
          <button class="remove" aria-label={`Remove ${item.name}`} onclick={() => remove(item.id)} disabled={uploading}>×</button>
          {#if item.url}
            <button class="thumb" onclick={() => open(item)} aria-label={`Open ${item.name}`}><img src={item.url} alt="" /></button>
          {:else}
            <div class="thumb loading" role="status" aria-label="Loading image">…</div>
          {/if}
          <figcaption><strong>{item.name}</strong><small>{item.size === undefined ? '' : formatBytes(item.size)}</small></figcaption>
        </figure>
      {/each}
    </div>
  {/if}
  <div class="row">
    {#if matches.length}
      <ul class="slash" role="listbox" aria-label="Commands" bind:this={menuList}>
        {#each matches as command, i (command.name)}
          <li role="option" aria-selected={i === Math.min(menuIndex, matches.length - 1)} class:active={i === Math.min(menuIndex, matches.length - 1)}>
            <button tabindex="-1" onmousedown={(event) => event.preventDefault()} onclick={() => pickCommand(command)} onmousemove={() => (menuIndex = i)}>
              <strong>{command.name.startsWith('$') ? '' : '/'}{command.name}</strong><span>{command.description}</span>
            </button>
          </li>
        {/each}
      </ul>
    {/if}
    <textarea bind:this={input} bind:value={draft} oninput={() => ((note = ''), (menuDismissed = false), (menuIndex = 0))} {onkeydown} {onpaste} rows="3" placeholder="Ask about this repo" disabled={uploading}></textarea>
    <button class="ghost" onclick={() => void pick()} disabled={uploading} title="Attach images">Attach</button>
    {#if busy}<button class="ghost" onclick={onstop}>Stop</button>{/if}
    <button onclick={() => void send()} disabled={uploading || !thumbsReady || (!draft.trim() && !pending.length)}>
      {uploading ? 'Uploading…' : 'Send'}
    </button>
  </div>
  <div class="foot">
    {#each menus as menu (menu.id)}
      <label class="menu" title={menu.name}>
        <span>{menu.name}</span>
        <select
          value={config.choices[menu.id] ?? menu.currentValue}
          onchange={(event) => void choose(menu.id, event.currentTarget.value)}
        >
          {#each menu.options ?? [] as option (option.value)}<option value={option.value} title={option.description}>{option.name}</option>{/each}
        </select>
      </label>
    {/each}
    <div class="hint">
      {note || (dragging ? 'Drop images to attach them, other files as their paths' : 'Paste or drop images and files')}{#if meter && !note && !dragging}<span class="left" class:warm={meter.warm} class:hot={meter.hot}>{` · ${meter.left}% left`}</span>{/if}
    </div>
    {#if meter}
      <button class="meter" class:warm={meter.warm} class:hot={meter.hot} aria-label={`Context: ${meter.detail}`}>
        <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true">
          <circle class="track" cx="8" cy="8" r="6" />
          <circle class="arc" cx="8" cy="8" r="6" stroke-dasharray={`${(meter.arc / 100) * RING} ${RING}`} transform="rotate(-90 8 8)" />
        </svg>
        <span class="pop" role="tooltip">{meter.detail}</span>
      </button>
    {/if}
  </div>
</div>
{#if full}<FullImage src={full.url} name={full.name} onclose={close} />{/if}

<style>
  .box {
    border: 1px solid transparent;
    border-radius: 12px;
  }
  .box.dragging {
    border-color: var(--accent);
  }
  .row {
    position: relative;
    display: flex;
    gap: 10px;
    align-items: flex-end;
  }
  textarea {
    flex: 1;
    resize: none;
    font: inherit;
    padding: 10px 12px;
    border: 1px solid var(--line);
    border-radius: 10px;
    background: var(--panel);
    color: inherit;
  }
  .chips {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
    margin-bottom: 8px;
  }
  figure {
    position: relative;
    display: grid;
    grid-template-columns: 60px minmax(0, 1fr);
    align-items: center;
    column-gap: 8px;
    min-width: 210px;
    max-width: 280px;
    margin: 0;
    padding: 6px 28px 6px 6px;
    border: 1px solid var(--line);
    border-radius: 8px;
    background: var(--panel);
  }
  .thumb {
    display: grid;
    place-items: center;
    width: 60px;
    height: 52px;
    padding: 0;
    border: 0;
    border-radius: 6px;
    overflow: hidden;
    background: var(--line);
    color: var(--muted);
    cursor: zoom-in;
  }
  .thumb.loading {
    cursor: default;
  }
  img {
    width: 100%;
    height: 100%;
    object-fit: cover;
  }
  figcaption {
    display: grid;
    gap: 2px;
    min-width: 0;
  }
  figcaption strong {
    overflow: hidden;
    font-size: 13px;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  figcaption small {
    color: var(--muted);
    font-size: 12px;
  }
  .remove {
    position: absolute;
    top: 4px;
    right: 4px;
    width: 20px;
    height: 20px;
    padding: 0;
    border: 0;
    border-radius: 999px;
    background: var(--line);
    color: inherit;
    line-height: 1;
  }
  .foot {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 6px 12px;
    margin-top: 6px;
  }
  .menu {
    display: flex;
    align-items: center;
    gap: 4px;
    color: var(--muted);
    font-size: 12px;
  }
  .menu select {
    max-width: 180px;
    padding: 2px 4px;
    border: 1px solid var(--line);
    border-radius: 6px;
    background: var(--panel);
    color: var(--text);
    font: inherit;
  }
  .hint {
    margin-left: auto;
    color: var(--muted);
    font-size: 12px;
  }
  .slash {
    position: absolute;
    left: 0;
    right: 0;
    bottom: calc(100% + 6px);
    z-index: 5;
    max-height: 280px;
    margin: 0;
    padding: 4px;
    overflow-y: auto;
    list-style: none;
    border: 1px solid var(--line);
    border-radius: 10px;
    background: var(--panel);
    box-shadow: 0 6px 20px rgb(0 0 0 / 0.08);
  }
  .slash button {
    display: flex;
    gap: 10px;
    align-items: baseline;
    width: 100%;
    padding: 6px 10px;
    border: 0;
    border-radius: 6px;
    background: none;
    color: var(--text);
    font: inherit;
    font-size: 13px;
    text-align: left;
    cursor: pointer;
  }
  .slash .active button {
    background: var(--user-bg);
  }
  .slash strong {
    flex: none;
    font-weight: 600;
  }
  .slash span {
    overflow: hidden;
    color: var(--muted);
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .left.warm,
  .meter.warm {
    color: #b26a00;
  }
  .left.hot,
  .meter.hot {
    color: var(--danger);
  }
  .meter {
    position: relative;
    display: grid;
    place-items: center;
    width: 22px;
    height: 22px;
    padding: 0;
    border: 0;
    background: none;
    color: var(--muted);
    cursor: default;
  }
  .meter circle {
    fill: none;
    stroke-width: 2.2;
  }
  .meter .track {
    stroke: var(--line);
  }
  .meter .arc {
    stroke: currentColor;
    stroke-linecap: round;
  }
  .pop {
    position: absolute;
    right: 0;
    bottom: calc(100% + 6px);
    padding: 6px 9px;
    border: 1px solid var(--line);
    border-radius: 8px;
    background: var(--panel);
    color: var(--text);
    font-size: 12px;
    white-space: nowrap;
    box-shadow: 0 4px 14px rgb(0 0 0 / 0.08);
    opacity: 0;
    pointer-events: none;
  }
  .meter:hover .pop,
  .meter:focus-visible .pop {
    opacity: 1;
  }
  .box.dragging .hint {
    color: var(--accent);
  }
  .error {
    color: var(--danger);
    font-size: 13px;
    margin-bottom: 6px;
  }
  .link {
    border: 0;
    background: none;
    padding: 0;
    color: inherit;
    text-decoration: underline;
    font-size: 13px;
  }
</style>
