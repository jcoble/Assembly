<script lang="ts">
  import {
    Search, Plus, Folder, Settings, Trash2, Check,
    Star, Bell, FileText, ChevronRight, Zap, AlertCircle
  } from '@lucide/svelte';

  import Button from '$lib/components/Button.svelte';
  import { PRODUCT_UI_LIBRARY_TITLE } from '$lib/productIdentity';
  import IconButton from '$lib/components/IconButton.svelte';
  import Chip from '$lib/components/Chip.svelte';
  import Badge from '$lib/components/Badge.svelte';
  import SearchInput from '$lib/components/SearchInput.svelte';
  import CollapsibleSection from '$lib/components/CollapsibleSection.svelte';
  import Menu from '$lib/components/Menu.svelte';
  import ContextMenu from '$lib/components/ContextMenu.svelte';
  import Tooltip from '$lib/components/Tooltip.svelte';
  import Dialog from '$lib/components/Dialog.svelte';
  import Select from '$lib/components/Select.svelte';
  import Tabs from '$lib/components/Tabs.svelte';
  import Slider from '$lib/components/Slider.svelte';
  import Switch from '$lib/components/Switch.svelte';

  // --- SearchInput ---
  let searchValue = $state('');

  // --- Dialog ---
  let dialogOpen = $state(false);

  // --- Select ---
  let selectValue = $state('');
  const selectItems = [
    { value: 'vscode', label: 'VS Code' },
    { value: 'zed', label: 'Zed' },
    { value: 'neovim', label: 'Neovim' },
    { value: 'cursor', label: 'Cursor' },
    { value: 'helix', label: 'Helix' },
  ];

  // --- Tabs ---
  let activeTab = $state('overview');
  const tabDefs = [
    { value: 'overview', label: 'Overview', icon: FileText },
    { value: 'settings', label: 'Settings', icon: Settings },
    { value: 'activity', label: 'Activity', icon: Zap },
  ];

  // --- Slider ---
  let fontSize = $state(14);

  // --- Switch ---
  let darkMode = $state(true);
  let notifications = $state(false);

  // --- Menu items ---
  const menuItems = [
    { id: 'new', label: 'New File', icon: Plus },
    { id: 'open', label: 'Open Folder', icon: Folder, separatorBefore: false },
    { id: 'settings', label: 'Settings', icon: Settings, separatorBefore: true },
    { id: 'delete', label: 'Delete', icon: Trash2, danger: true, separatorBefore: true },
  ];

  // --- ContextMenu items ---
  const contextItems = [
    { id: 'copy', label: 'Copy', onselect: () => {} },
    { id: 'paste', label: 'Paste', onselect: () => {} },
    { id: 'rename', label: 'Rename', icon: FileText, separatorBefore: true, onselect: () => {} },
    { id: 'delete', label: 'Delete', icon: Trash2, danger: true, separatorBefore: true, onselect: () => {} },
  ];
</script>

<div class="gallery">
  <header class="gallery-header">
    <h1>Component Gallery</h1>
    <p class="subtitle">{PRODUCT_UI_LIBRARY_TITLE} — visual verification</p>
  </header>

  <!-- ═══ Button ═══════════════════════════════════════════════════ -->
  <section class="gallery-section">
    <h2 class="section-title">Button</h2>

    <div class="row">
      <span class="row-label">Variants (md)</span>
      <div class="items">
        <Button variant="primary">Primary</Button>
        <Button variant="secondary">Secondary</Button>
        <Button variant="ghost">Ghost</Button>
        <Button variant="danger">Danger</Button>
      </div>
    </div>

    <div class="row">
      <span class="row-label">Sizes</span>
      <div class="items">
        <Button variant="primary" size="sm">Primary sm</Button>
        <Button variant="secondary" size="sm">Secondary sm</Button>
        <Button variant="ghost" size="sm">Ghost sm</Button>
        <Button variant="danger" size="sm">Danger sm</Button>
      </div>
    </div>

    <div class="row">
      <span class="row-label">With icon / disabled</span>
      <div class="items">
        <Button variant="primary" leftIcon={Plus}>New File</Button>
        <Button variant="secondary" leftIcon={Folder}>Open</Button>
        <Button variant="secondary" disabled>Disabled</Button>
        <Button variant="danger" leftIcon={Trash2} size="sm">Delete sm</Button>
      </div>
    </div>
  </section>

  <!-- ═══ IconButton ════════════════════════════════════════════════ -->
  <section class="gallery-section">
    <h2 class="section-title">IconButton</h2>

    <div class="row">
      <span class="row-label">Variants (md)</span>
      <div class="items">
        <IconButton icon={Settings} label="Settings" variant="default" />
        <IconButton icon={Search} label="Search" variant="ghost" />
        <IconButton icon={Trash2} label="Delete" variant="danger" />
      </div>
    </div>

    <div class="row">
      <span class="row-label">Sizes</span>
      <div class="items">
        <IconButton icon={Plus} label="Add (md)" size="md" variant="default" />
        <IconButton icon={Plus} label="Add (sm)" size="sm" variant="default" />
        <IconButton icon={Bell} label="Notify (sm)" size="sm" variant="ghost" />
        <IconButton icon={Trash2} label="Delete (sm)" size="sm" variant="danger" />
        <IconButton icon={Settings} label="Disabled" variant="default" disabled />
      </div>
    </div>
  </section>

  <!-- ═══ Chip ══════════════════════════════════════════════════════ -->
  <section class="gallery-section">
    <h2 class="section-title">Chip</h2>

    <div class="row">
      <span class="row-label">Tones</span>
      <div class="items">
        <Chip tone="neutral">neutral</Chip>
        <Chip tone="live">live</Chip>
        <Chip tone="good">good</Chip>
        <Chip tone="bad">bad</Chip>
        <Chip tone="attention">attention</Chip>
        <Chip tone="muted">muted</Chip>
      </div>
    </div>

    <div class="row">
      <span class="row-label">With icon / dismiss</span>
      <div class="items">
        <Chip tone="live" leadingIcon={Zap}>Live</Chip>
        <Chip tone="good" leadingIcon={Check}>Passing</Chip>
        <Chip tone="bad" ondismiss={() => {}}>Error</Chip>
        <Chip tone="attention" leadingIcon={AlertCircle} ondismiss={() => {}}>Warning</Chip>
      </div>
    </div>

    <div class="row">
      <span class="row-label">Sizes</span>
      <div class="items">
        <Chip tone="neutral" size="sm">sm (default)</Chip>
        <Chip tone="neutral" size="xs">xs</Chip>
      </div>
    </div>
  </section>

  <!-- ═══ Badge ═════════════════════════════════════════════════════ -->
  <section class="gallery-section">
    <h2 class="section-title">Badge</h2>

    <div class="row">
      <span class="row-label">Tones</span>
      <div class="items">
        <Badge tone="neutral">12</Badge>
        <Badge tone="live">3</Badge>
        <Badge tone="good">42</Badge>
        <Badge tone="bad">7</Badge>
        <Badge tone="attention">99</Badge>
        <Badge tone="muted">0</Badge>
      </div>
    </div>
  </section>

  <!-- ═══ SearchInput ═══════════════════════════════════════════════ -->
  <section class="gallery-section">
    <h2 class="section-title">SearchInput</h2>

    <div class="row">
      <span class="row-label">Bound value</span>
      <div class="items search-items">
        <SearchInput bind:value={searchValue} placeholder="Search files…" />
        <span class="value-display">value: "{searchValue}"</span>
      </div>
    </div>
  </section>

  <!-- ═══ CollapsibleSection ════════════════════════════════════════ -->
  <section class="gallery-section">
    <h2 class="section-title">CollapsibleSection</h2>

    <div class="collapsible-container">
      <CollapsibleSection title="Open Files" icon={FileText} badge={3} expanded={true}>
        <div class="section-body">
          <p>main.rs</p>
          <p>terminal.rs</p>
          <p>lsp.rs</p>
        </div>
      </CollapsibleSection>

      <CollapsibleSection title="Git History" icon={Star} badge={12} expanded={false}>
        <div class="section-body">
          <p>feat: add worktree cleanup</p>
          <p>fix: reuse source cache</p>
        </div>
      </CollapsibleSection>
    </div>
  </section>

  <!-- ═══ Menu ══════════════════════════════════════════════════════ -->
  <section class="gallery-section">
    <h2 class="section-title">Menu</h2>

    <div class="row">
      <span class="row-label">Array-driven</span>
      <div class="items">
        <Menu items={menuItems}>
          {#snippet trigger()}
            <Button variant="secondary" leftIcon={Plus}>Open Menu</Button>
          {/snippet}
        </Menu>

        <Menu items={menuItems} align="end">
          {#snippet trigger()}
            <IconButton icon={Settings} label="Settings menu" variant="ghost" />
          {/snippet}
        </Menu>
      </div>
    </div>
  </section>

  <!-- ═══ ContextMenu ═══════════════════════════════════════════════ -->
  <section class="gallery-section">
    <h2 class="section-title">ContextMenu</h2>

    <div class="row">
      <span class="row-label">Right-click zone</span>
      <div class="items">
        <ContextMenu items={contextItems}>
          {#snippet children()}
            <div class="context-zone">Right-click anywhere in this box</div>
          {/snippet}
        </ContextMenu>
      </div>
    </div>
  </section>

  <!-- ═══ Tooltip ═══════════════════════════════════════════════════ -->
  <section class="gallery-section">
    <h2 class="section-title">Tooltip</h2>

    <div class="row">
      <span class="row-label">Sides / types</span>
      <div class="items">
        <Tooltip text="Creates a new file" side="top">
          {#snippet children()}
            <Button variant="primary" leftIcon={Plus}>New File (top)</Button>
          {/snippet}
        </Tooltip>

        <Tooltip text="Open folder" side="bottom">
          {#snippet children()}
            <Button variant="secondary" leftIcon={Folder}>Open (bottom)</Button>
          {/snippet}
        </Tooltip>

        <Tooltip text="Delete selected" side="right">
          {#snippet children()}
            <IconButton icon={Trash2} label="Delete" variant="danger" />
          {/snippet}
        </Tooltip>

        <Tooltip text="Application settings" side="left">
          {#snippet children()}
            <IconButton icon={Settings} label="Settings" variant="ghost" />
          {/snippet}
        </Tooltip>
      </div>
    </div>
  </section>

  <!-- ═══ Dialog ════════════════════════════════════════════════════ -->
  <section class="gallery-section">
    <h2 class="section-title">Dialog</h2>

    <div class="row">
      <span class="row-label">Trigger-controlled</span>
      <div class="items">
        <Dialog
          bind:open={dialogOpen}
          title="Confirm Deletion"
          description="This action cannot be undone. The selected files will be permanently removed."
        >
          {#snippet trigger()}
            <Button variant="danger" leftIcon={Trash2}>Open Dialog</Button>
          {/snippet}

          {#snippet children()}
            <p class="dialog-body-text">
              Are you sure you want to delete <strong>3 files</strong>? This will remove them
              from the workspace and cannot be reversed.
            </p>
          {/snippet}

          {#snippet footer()}
            <Button variant="ghost" onclick={() => (dialogOpen = false)}>Cancel</Button>
            <Button variant="danger" onclick={() => (dialogOpen = false)}>Delete Files</Button>
          {/snippet}
        </Dialog>

        <span class="value-display">open: {dialogOpen}</span>
      </div>
    </div>
  </section>

  <!-- ═══ Select ════════════════════════════════════════════════════ -->
  <section class="gallery-section">
    <h2 class="section-title">Select</h2>

    <div class="row">
      <span class="row-label">Single select</span>
      <div class="items search-items">
        <Select bind:value={selectValue} items={selectItems} placeholder="Choose editor…" />
        <span class="value-display">value: "{selectValue}"</span>
      </div>
    </div>

    <div class="row">
      <span class="row-label">Disabled</span>
      <div class="items">
        <Select value="vscode" items={selectItems} disabled />
      </div>
    </div>
  </section>

  <!-- ═══ Tabs ══════════════════════════════════════════════════════ -->
  <section class="gallery-section">
    <h2 class="section-title">Tabs</h2>

    <div class="tabs-container">
      <Tabs bind:value={activeTab} tabs={tabDefs}>
        {#snippet panel(tabValue)}
          <div class="tab-panel">
            {#if tabValue === 'overview'}
              <p>Overview panel — showing project summary, recent files, and status indicators.</p>
            {:else if tabValue === 'settings'}
              <p>Settings panel — configure editor preferences, keybindings, and extensions.</p>
            {:else if tabValue === 'activity'}
              <p>Activity panel — real-time build output, LSP diagnostics, and terminal events.</p>
            {/if}
          </div>
        {/snippet}
      </Tabs>
      <span class="value-display" style="margin-top: var(--space-2)">active: "{activeTab}"</span>
    </div>
  </section>

  <!-- ═══ Slider ════════════════════════════════════════════════════ -->
  <section class="gallery-section">
    <h2 class="section-title">Slider</h2>

    <div class="row">
      <span class="row-label">Font size (11–24)</span>
      <div class="items slider-items">
        <Slider bind:value={fontSize} min={11} max={24} step={1} label="Editor font size" />
        <span class="value-display">→ {fontSize}px</span>
      </div>
    </div>

    <div class="row">
      <span class="row-label">Disabled</span>
      <div class="items slider-items">
        <Slider value={50} min={0} max={100} label="Volume" disabled />
      </div>
    </div>
  </section>

  <!-- ═══ Switch ════════════════════════════════════════════════════ -->
  <section class="gallery-section">
    <h2 class="section-title">Switch</h2>

    <div class="row">
      <span class="row-label">Toggles</span>
      <div class="items">
        <Switch bind:checked={darkMode} label="Dark mode" />
        <span class="value-display">{darkMode ? 'on' : 'off'}</span>
      </div>
    </div>

    <div class="row">
      <span class="row-label">Notifications / disabled</span>
      <div class="items">
        <Switch bind:checked={notifications} label="Notifications" />
        <span class="value-display">{notifications ? 'on' : 'off'}</span>
        <Switch checked={true} label="Disabled on" disabled />
        <Switch checked={false} label="Disabled off" disabled />
      </div>
    </div>
  </section>
</div>

<style>
  .gallery {
    background: var(--color-bg);
    color: var(--color-text);
    min-height: 100vh;
    padding: var(--space-6);
    display: flex;
    flex-direction: column;
    gap: var(--space-6);
    font-family: inherit;
  }

  .gallery-header {
    border-bottom: 1px solid var(--color-border);
    padding-bottom: var(--space-4);
  }

  .gallery-header h1 {
    font-size: var(--text-xl);
    font-weight: var(--weight-semibold);
    color: var(--color-text);
    margin: 0 0 var(--space-1) 0;
  }

  .subtitle {
    font-size: var(--text-sm);
    color: var(--color-text-2);
    margin: 0;
  }

  /* ── Section ─────────────────────────────────────────────────── */
  .gallery-section {
    display: flex;
    flex-direction: column;
    gap: var(--space-3);
    padding: var(--space-4);
    background: var(--color-surface);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-md);
  }

  .section-title {
    font-size: var(--text-lg);
    font-weight: var(--weight-medium);
    color: var(--color-text);
    margin: 0 0 var(--space-1) 0;
    padding-bottom: var(--space-2);
    border-bottom: 1px solid var(--color-border);
  }

  /* ── Row ─────────────────────────────────────────────────────── */
  .row {
    display: flex;
    align-items: center;
    gap: var(--space-4);
    flex-wrap: wrap;
  }

  .row-label {
    font-size: var(--text-xs);
    color: var(--color-text-3);
    min-width: 140px;
    flex-shrink: 0;
  }

  .items {
    display: flex;
    align-items: center;
    gap: var(--space-3);
    flex-wrap: wrap;
  }

  /* ── Specialty layouts ───────────────────────────────────────── */
  .search-items {
    align-items: center;
    gap: var(--space-3);
  }

  .slider-items {
    align-items: center;
    gap: var(--space-3);
    min-width: 260px;
  }

  .value-display {
    font-size: var(--text-xs);
    color: var(--color-text-3);
    font-family: monospace;
  }

  /* ── CollapsibleSection container ────────────────────────────── */
  .collapsible-container {
    display: flex;
    flex-direction: column;
    gap: var(--space-2);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-sm);
    overflow: hidden;
  }

  .section-body {
    display: flex;
    flex-direction: column;
    gap: var(--space-1);
    padding: var(--space-2) var(--space-3);
  }

  .section-body p {
    margin: 0;
    font-size: var(--text-sm);
    color: var(--color-text-2);
  }

  /* ── ContextMenu zone ────────────────────────────────────────── */
  .context-zone {
    padding: var(--space-3) var(--space-4);
    border: 1px dashed var(--color-border);
    border-radius: var(--radius-sm);
    font-size: var(--text-sm);
    color: var(--color-text-2);
    cursor: context-menu;
    user-select: none;
  }

  /* ── Tabs container ──────────────────────────────────────────── */
  .tabs-container {
    display: flex;
    flex-direction: column;
  }

  .tab-panel {
    padding: var(--space-3) var(--space-4);
    font-size: var(--text-sm);
    color: var(--color-text-2);
  }

  .tab-panel p {
    margin: 0;
  }

  /* ── Dialog body ─────────────────────────────────────────────── */
  .dialog-body-text {
    font-size: var(--text-sm);
    color: var(--color-text-2);
    margin: 0;
    line-height: 1.5;
  }

  .dialog-body-text strong {
    color: var(--color-text);
  }
</style>
