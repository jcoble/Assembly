import assert from 'node:assert/strict';
import {
  loadLayout,
  saveLayout,
  clearLayout,
  gridPanelIds,
  storedColumnWidth,
  paneviewPanelIds,
  panelSetMatches,
  CENTER_LAYOUT_KEY,
  GRID_LAYOUT_KEY
} from '../src/lib/shell/layout/layoutStorage.ts';
import {
  SIDE_PANE_LAYOUT_KEY,
  SIDE_PANE_LAYOUT_VERSION
} from '../src/lib/shell/layout/paneStack.ts';

function memoryStorage(initial = {}) {
  const map = new Map(Object.entries(initial));
  return {
    getItem: (k) => (map.has(k) ? map.get(k) : null),
    setItem: (k, v) => map.set(k, v),
    removeItem: (k) => map.delete(k),
    _map: map
  };
}

// loadLayout: absent, corrupt, and round-trip
{
  const storage = memoryStorage();
  assert.equal(loadLayout(storage, 'k'), null, 'absent key -> null');
  storage.setItem('k', '{not json');
  assert.equal(loadLayout(storage, 'k'), null, 'corrupt json -> null, no throw');
  storage.setItem('k', JSON.stringify({ a: 1 }));
  assert.deepEqual(loadLayout(storage, 'k'), { a: 1 }, 'round-trip');
}

// saveLayout: happy path true, quota error false (never throws)
{
  const storage = memoryStorage();
  assert.equal(saveLayout(storage, 'k', { a: 1 }), true);
  assert.deepEqual(JSON.parse(storage.getItem('k')), { a: 1 });
  const failing = {
    getItem: () => null,
    setItem: () => {
      throw new DOMException('quota', 'QuotaExceededError');
    },
    removeItem: () => {}
  };
  assert.equal(saveLayout(failing, 'k', { a: 1 }), false, 'quota -> false, no throw');
}

// clearLayout tolerates a throwing storage
{
  const throwing = {
    getItem: () => null,
    setItem: () => {},
    removeItem: () => {
      throw new Error('nope');
    }
  };
  assert.doesNotThrow(() => clearLayout(throwing, 'k'));
}

// gridPanelIds walks the SerializedGridviewComponent tree (branch/leaf shape)
{
  const grid = {
    grid: {
      root: {
        type: 'branch',
        data: [
          { type: 'leaf', data: { id: 'rail' } },
          {
            type: 'branch',
            data: [
              { type: 'leaf', data: { id: 'center' } },
              { type: 'leaf', data: { id: 'dock' } }
            ]
          },
          { type: 'leaf', data: { id: 'context' } }
        ]
      },
      width: 1200,
      height: 800,
      orientation: 'HORIZONTAL'
    }
  };
  assert.deepEqual([...gridPanelIds(grid)].sort(), ['center', 'context', 'dock', 'rail']);
  assert.deepEqual([...gridPanelIds({})], [], 'malformed -> empty, no throw');
}

// storedColumnWidth reads the width a top-level column was saved at. Restoring
// scales every column by the new grid width over the saved one, so the saved
// number itself is what the sessions column must be given back (TSK-1544).
{
  const grid = {
    grid: {
      root: {
        type: 'branch',
        size: 900,
        data: [
          { type: 'leaf', size: 320, data: { id: 'sessions' } },
          {
            type: 'branch',
            size: 680,
            data: [
              { type: 'leaf', size: 720, data: { id: 'center' } },
              { type: 'leaf', size: 180, data: { id: 'dock' } }
            ]
          }
        ]
      },
      width: 1000,
      height: 900,
      orientation: 'HORIZONTAL'
    }
  };
  assert.equal(storedColumnWidth(grid, 'sessions'), 320, 'the saved width, not a scaled one');
  assert.equal(storedColumnWidth(grid, 'center'), null, 'a leaf inside a column has no column width');
  assert.equal(storedColumnWidth(grid, 'tools'), null, 'a missing column -> null');
  assert.equal(storedColumnWidth({}, 'sessions'), null, 'malformed -> null, no throw');
  assert.equal(
    storedColumnWidth({ grid: { root: { type: 'branch', data: [{ type: 'leaf', data: { id: 'sessions' } }] } } }, 'sessions'),
    null,
    'a leaf with no size -> null'
  );
}

// paneviewPanelIds reads SerializedPaneview.views[].data.id.
//
// This is the exact object dockview-core 6.6.1 writes: `PaneviewComponent.toJSON`
// (paneviewComponent.js:281-307) builds `{ size, views: [...] }`, and each view is
// `{ size, data, minimumSize, maximumSize, headerSize, expanded }` where `data` is
// `PaneviewPanel.toJSON()` (paneviewPanel.js:240-243, over basePanelView.js:145-153)
// = `{ id, component, params?, headerComponent, title }`. Note the serialized names
// are `minimumSize`/`maximumSize`/`expanded`, while the ADD options are spelled
// `minimumBodySize`/`maximumBodySize`/`isExpanded` — the two shapes do not match.
{
  const paneview = {
    size: 800,
    views: [
      {
        size: 240,
        data: {
          id: 'sessions',
          component: 'pane',
          params: { paneId: 'sessions' },
          headerComponent: undefined,
          title: 'Sessions'
        },
        minimumSize: undefined,
        maximumSize: undefined,
        headerSize: 22,
        expanded: true
      },
      {
        size: 22,
        data: { id: 'files', component: 'pane', params: { paneId: 'files' }, title: 'Files' },
        headerSize: 22,
        expanded: false
      },
      {
        size: 300,
        data: {
          id: 'source-control',
          component: 'pane',
          params: { paneId: 'source-control' },
          title: 'Source Control'
        },
        headerSize: 22,
        expanded: true
      }
    ]
  };
  assert.deepEqual(
    [...paneviewPanelIds(paneview)].sort(),
    ['files', 'sessions', 'source-control'],
    'ids come out whether the pane is open or collapsed'
  );

  assert.deepEqual([...paneviewPanelIds({})], [], 'no views key -> empty, no throw');
  assert.deepEqual([...paneviewPanelIds(null)], [], 'null -> empty, no throw');
  assert.deepEqual([...paneviewPanelIds('nonsense')], [], 'not an object -> empty, no throw');
  assert.deepEqual(
    [...paneviewPanelIds({ size: 800, views: { sessions: {} } })],
    [],
    'views must be an array, not a record -> empty'
  );
  assert.deepEqual([...paneviewPanelIds({ views: [] })], [], 'no views -> empty');
  assert.deepEqual(
    [...paneviewPanelIds({ views: [{ size: 10 }, { size: 10, data: null }, { data: {} }] })],
    [],
    'views with no usable data -> empty, no throw'
  );
  assert.deepEqual(
    [...paneviewPanelIds({ views: [{ data: { id: 7 } }, { data: { id: 'files' } }] })],
    ['files'],
    'a non-string id is skipped, the good one still comes through'
  );
  assert.deepEqual(
    [...paneviewPanelIds({ views: [{ data: { id: 'files' } }, { data: { id: 'files' } }] })],
    ['files'],
    'duplicate ids collapse to one'
  );
}

// panelSetMatches: exact set equality, order-independent
{
  assert.equal(panelSetMatches(['a', 'b'], ['b', 'a']), true);
  assert.equal(panelSetMatches(['a'], ['a', 'b']), false);
  assert.equal(panelSetMatches(['a', 'b', 'c'], ['a', 'b']), false);
  assert.equal(panelSetMatches([], []), true);
}

// PaneStack uses the existing LayoutStorage authority through one versioned key.
{
  assert.equal(SIDE_PANE_LAYOUT_KEY, 'mac-command-bar.next.side-panes-v1');
  assert.equal(SIDE_PANE_LAYOUT_VERSION, 1);
  const storage = memoryStorage();
  const payload = { version: SIDE_PANE_LAYOUT_VERSION, layouts: { 'left-rail': { views: [] } } };
  assert.equal(saveLayout(storage, SIDE_PANE_LAYOUT_KEY, payload), true);
  assert.deepEqual([...storage._map.keys()], [SIDE_PANE_LAYOUT_KEY], 'one side-pane storage key');
  assert.deepEqual(loadLayout(storage, SIDE_PANE_LAYOUT_KEY), payload);
}

// Both roster bumps are exact-set based: a stored layout is restored only when
// its panel set is exactly the current one, and anything else — including every
// older roster — falls back to the defaults rather than being translated.
{
  assert.equal(GRID_LAYOUT_KEY, 'mac-command-bar.next.grid-layout-v3');
  assert.equal(CENTER_LAYOUT_KEY, 'mac-command-bar.next.center-layout-v6');
  assert.equal(
    panelSetMatches(['session', 'editor', 'diff'], ['diff', 'editor', 'session']),
    true,
    'the current center roster is an exact set, regardless of stored order'
  );
  assert.equal(
    panelSetMatches(
      ['session', 'editor', 'browser', 'diff', 'session-library', 'agents'],
      ['session', 'editor', 'diff']
    ),
    false,
    'the retired six-panel roster is not restored into the three-panel center'
  );
  assert.equal(
    panelSetMatches(['session', 'editor'], ['session', 'editor', 'diff']),
    false,
    'a partial center roster must fall back safely'
  );
  assert.equal(
    panelSetMatches(
      ['sessions', 'center', 'tools', 'activity', 'dock'],
      ['sessions', 'center', 'tools', 'dock']
    ),
    false,
    'a grid layout still naming the deleted far-right rail must fall back safely'
  );
}

console.log('layoutStorage: all tests passed');
