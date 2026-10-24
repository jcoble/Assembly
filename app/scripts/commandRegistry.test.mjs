// /**
//  * Tests for the command palette's action list (`src/lib/shell/palette/commandRegistry.ts`).
//  * Pure: no DOM, no backend, no Svelte. Run with:
//  *   node --experimental-strip-types scripts/commandRegistry.test.mjs
//  */
// import assert from 'node:assert/strict';
// import {
//   allCommands,
//   filterCommands,
//   registerCommands,
//   resetCommandRegistry,
//   runPaletteCommand,
//   unregisterCommands
// } from '../src/lib/shell/palette/commandRegistry.ts';

// /** Build a command with a no-op action unless one is given. */
// function makeCommand(id, label, detail, extra = {}) {
//   return { id, label, detail, perform: () => {}, ...extra };
// }

// // an empty registry has nothing to show
// {
//   resetCommandRegistry();
//   assert.deepEqual(allCommands(), []);
//   assert.deepEqual(filterCommands(''), []);
// }

// // commands come back in registration order, sources in the order they first registered
// {
//   resetCommandRegistry();
//   registerCommands('page', [makeCommand('a', 'Reset layout', 'Shell'), makeCommand('b', 'Rescan sessions', 'Shell')]);
//   registerCommands('git', [makeCommand('c', 'Commit changes', 'mac-command-bar')]);
//   assert.deepEqual(
//     allCommands().map((command) => command.id),
//     ['a', 'b', 'c']
//   );
// }

// // registering the same source twice replaces its commands and keeps its position
// {
//   resetCommandRegistry();
//   registerCommands('page', [makeCommand('a', 'Reset layout', 'Shell')]);
//   registerCommands('git', [makeCommand('c', 'Commit changes', 'mac-command-bar')]);
//   registerCommands('page', [makeCommand('a', 'Reset layout', 'Shell'), makeCommand('d', 'Open settings', 'Shell')]);
//   assert.deepEqual(
//     allCommands().map((command) => command.id),
//     ['a', 'd', 'c'],
//     'one copy of each page command, still listed before the git source'
//   );
// }

// // a repeated id is dropped: the palette list is keyed by id and would break on a repeat
// {
//   resetCommandRegistry();
//   registerCommands('first', [makeCommand('open-settings', 'Open settings', 'from the page')]);
//   registerCommands('second', [makeCommand('open-settings', 'Open settings again', 'from a panel')]);
//   const ids = allCommands().map((command) => command.id);
//   assert.deepEqual(ids, ['open-settings']);
//   assert.equal(allCommands()[0].label, 'Open settings', 'the first registration wins');
// }

// // unregistering a source removes only that source's commands
// {
//   resetCommandRegistry();
//   registerCommands('page', [makeCommand('a', 'Reset layout', 'Shell')]);
//   registerCommands('git', [makeCommand('c', 'Commit changes', 'mac-command-bar')]);
//   unregisterCommands('git');
//   assert.deepEqual(
//     allCommands().map((command) => command.id),
//     ['a']
//   );
//   unregisterCommands('nobody-registered-this');
//   assert.equal(allCommands().length, 1, 'removing an unknown source changes nothing');
// }

// // the function returned by registerCommands removes that source again
// {
//   resetCommandRegistry();
//   const remove = registerCommands('panel', [makeCommand('a', 'Reload page', 'Browser')]);
//   assert.equal(allCommands().length, 1);
//   remove();
//   assert.deepEqual(allCommands(), []);
// }

// // searching matches the name and the detail, ignores case, and accepts words in any order
// {
//   resetCommandRegistry();
//   registerCommands('page', [
//     makeCommand('reset-layout', 'Reset layout', 'Put every panel back where it started'),
//     makeCommand('rescan-sessions', 'Rescan sessions', 'Look for agent sessions to resume'),
//     makeCommand('open-settings', 'Settings', 'Fonts, terminal and appearance')
//   ]);

//   assert.deepEqual(
//     filterCommands('reset').map((row) => row.id),
//     ['reset-layout']
//   );
//   assert.deepEqual(
//     filterCommands('RESET').map((row) => row.id),
//     ['reset-layout'],
//     'search ignores capitals'
//   );
//   assert.deepEqual(
//     filterCommands('resume').map((row) => row.id),
//     ['rescan-sessions'],
//     'the detail line is searched too'
//   );
//   assert.deepEqual(
//     filterCommands('layout reset').map((row) => row.id),
//     ['reset-layout'],
//     'words may be given in any order'
//   );
//   assert.deepEqual(
//     filterCommands('reset fonts').map((row) => row.id),
//     [],
//     'every word must match'
//   );
//   assert.deepEqual(
//     filterCommands('   ').map((row) => row.id),
//     ['reset-layout', 'rescan-sessions', 'open-settings'],
//     'a blank search shows everything'
//   );
// }

// // at most twelve rows by default, and the limit can be lowered
// {
//   resetCommandRegistry();
//   registerCommands(
//     'many',
//     Array.from({ length: 30 }, (_, index) => makeCommand(`c${index}`, `Command ${index}`, 'A test command'))
//   );
//   assert.equal(filterCommands('command').length, 12, 'default limit is twelve');
//   assert.deepEqual(
//     filterCommands('command', 3).map((row) => row.id),
//     ['c0', 'c1', 'c2']
//   );
//   assert.deepEqual(filterCommands('command', 0), [], 'a limit of zero shows nothing');
//   assert.deepEqual(filterCommands('command', -5), [], 'a negative limit shows nothing');
// }

// // availability is resolved at search time into a plain true/false
// {
//   resetCommandRegistry();
//   let blocked = true;
//   registerCommands('page', [
//     makeCommand('commit', 'Commit changes', 'mac-command-bar', { disabled: () => blocked })
//   ]);
//   assert.equal(filterCommands('commit')[0].disabled, true);
//   blocked = false;
//   assert.equal(filterCommands('commit')[0].disabled, false, 'the check runs again on every search');
// }

// // a check that throws counts as unavailable instead of taking the palette down
// {
//   resetCommandRegistry();
//   registerCommands('page', [
//     makeCommand('risky', 'Push repository', 'mac-command-bar', {
//       disabled: () => {
//         throw new Error('no project is selected');
//       }
//     })
//   ]);
//   const rows = filterCommands('push');
//   assert.equal(rows.length, 1);
//   assert.equal(rows[0].disabled, true);
// }

// // running a row calls its action; an unavailable row does nothing
// {
//   resetCommandRegistry();
//   let ran = 0;
//   registerCommands('page', [
//     makeCommand('go', 'Reset layout', 'Shell', { perform: () => { ran += 1; } }),
//     makeCommand('stop', 'Push repository', 'Shell', {
//       disabled: () => true,
//       perform: () => { ran += 100; }
//     })
//   ]);
//   await runPaletteCommand(filterCommands('reset')[0]);
//   assert.equal(ran, 1);
//   await runPaletteCommand(filterCommands('push')[0]);
//   assert.equal(ran, 1, 'an unavailable command never runs');
// }

// // an async action is awaited, and a failing action rejects for the caller to report
// {
//   resetCommandRegistry();
//   let finished = false;
//   registerCommands('page', [
//     makeCommand('slow', 'Rescan sessions', 'Shell', {
//       perform: async () => {
//         await new Promise((resolve) => setTimeout(resolve, 5));
//         finished = true;
//       }
//     }),
//     makeCommand('bad', 'Push repository', 'Shell', {
//       perform: () => Promise.reject(new Error('the network is unreachable'))
//     })
//   ]);
//   await runPaletteCommand(filterCommands('rescan')[0]);
//   assert.equal(finished, true, 'an async action is awaited');
//   await assert.rejects(() => runPaletteCommand(filterCommands('push')[0]), /the network is unreachable/);
// }

// // the list handed to registerCommands is copied: changing it later changes nothing
// {
//   resetCommandRegistry();
//   const commands = [makeCommand('a', 'Reset layout', 'Shell')];
//   registerCommands('page', commands);
//   commands.push(makeCommand('b', 'Sneaky', 'Should not appear'));
//   assert.deepEqual(
//     allCommands().map((command) => command.id),
//     ['a']
//   );
// }

// resetCommandRegistry();
// console.log('commandRegistry tests passed');
