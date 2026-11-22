// Drives the running desktop through its accessibility tree with `agent-desktop`, finding every control by its id
// (desktop/src/core/ui/access/control_ids.rs), never by a snapshot ref, which changes with each snapshot. It reads the
// real tree, so it is also the check that the ids and roles reach assistive technology:
//   node scripts/agent-desktop-ids.ts            runs the walk below and exits 1 on the first control it can't find
// The app must already be running with accessibility on (DISPLAY and DBUS_SESSION_BUS_ADDRESS set, and
// org.a11y.Status IsEnabled true); AGENT_DESKTOP names the binary (default `agent-desktop`). Use it only on the
// Assembly window: every command carries --app assembly-desktop.
import { pathToFileURL } from 'node:url';
import { click, focusChat, focusSessions, focusWindow, waitFor } from './drive/ax.ts';

export { flatten, nodeWithId, type Node } from './drive/ax.ts';

/** Waits for `id` (a pane's controls come and go as focus moves), then checks its role and, when given, its name. */
async function expect(id: string, role: string, name?: string, all = false): Promise<void> {
  const node = await waitFor(id, true, all);
  if (node!.role !== role) throw new Error(`${id} is a ${node!.role}, expected ${role}`);
  if (name !== undefined && node!.name !== name) throw new Error(`${id} is named ${JSON.stringify(node!.name)}, expected ${JSON.stringify(name)}`);
  console.log(`ok  ${id.padEnd(34)} ${role.padEnd(10)} ${node!.name ?? ''}`);
}

async function main(): Promise<void> {
  focusWindow();
  // The tree holds the controls of the pane that has focus (the kit's panels publish only that one). The chat is
  // focused by pressing its composer's place (the saved layout decides what else is open); a column's tab, once it is
  // picked, takes the column only after another tab was picked, so the sessions column is reached through the Files tab.
  await focusChat();
  await expect('topbar.home', 'button', 'Home');
  await expect('topbar.board', 'button', 'Board');
  await expect('tabs.chat', 'tab', 'Chat');
  await expect('composer.input', 'textfield', 'Message');
  await expect('composer.send', 'button', 'Send message');
  await focusSessions();
  await expect('side-tabs.sessions', 'tab', 'Sessions');
  await expect('side-tabs.files', 'tab', 'Files');
  await expect('sessions.new-session', 'button', 'New session');
  await expect('sessions.view-options', 'button', 'View options');
  await expect('sessions.filter.provider', 'button');
  await expect('sessions.filter.status', 'button');
  await expect('utility.settings', 'button', 'Settings');
  await expect('utility.usage', 'button', 'Usage');
  await expect('sessions.empty', 'statictext', 'No sessions here.', true);

  // Settings > Connections, then back.
  await click('utility.settings');
  await waitFor('settings.back');
  await click('settings.nav.connections');
  await waitFor('settings.machine-save');
  await expect('settings.machine-name', 'textfield', 'Machine name');
  await expect('settings.machine.local.name', 'statictext', 'This Mac', true);
  await click('settings.back');
  await waitFor('settings.back', false);

  // The Build Board.
  await click('topbar.board', true);
  await waitFor('board.tab.workflows');
  await expect('board.summary', 'statictext', '0 running · 0 waiting · 0 done today', true);
  await expect('board.tab.needs-you', 'tab', 'Needs you');
  await click('topbar.board', true);
  console.log('walked the main screen, Settings > Connections and the Board by id');
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  main().catch((error) => {
    console.error(`FAIL ${(error as Error).message}`);
    process.exit(1);
  });
}
