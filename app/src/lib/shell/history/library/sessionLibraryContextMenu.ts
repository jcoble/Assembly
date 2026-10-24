/**
 * Session History context-menu roster.
 *
 * This is deliberately pure: the component owns positioning and click handling,
 * while this file keeps the action names and capability truth testable without a
 * browser. Unsupported actions stay visible and explain their FUTURE-NATIVE
 * status instead of pretending that a no-op succeeded.
 */
import type { SessionLibraryRecord } from './sessionLibraryModel.ts';

export type SessionContextMenuAction =
  | 'resume-worktree'
  | 'continue-new-session'
  | 'view-log'
  | 'copy-id'
  | 'archive'
  | 'delete';

export interface SessionContextMenuItem {
  id: SessionContextMenuAction;
  label: string;
  enabled: boolean;
  disabledReason: string | null;
}

export interface SessionContextMenuOptions {
  target: 'session' | 'center-tab';
  record?: SessionLibraryRecord | null;
  canResume?: boolean;
  canArchive?: boolean;
  canDelete?: boolean;
}

export interface SessionContextMenuAnchor {
  left: number;
  right: number;
  top: number;
  bottom: number;
  containingBlockLeft?: number;
  containingBlockRight?: number;
  containingBlockTop?: number;
  containingBlockBottom?: number;
}

export interface SessionContextMenuSize {
  width: number;
  height: number;
}

export interface SessionContextMenuViewport {
  width: number;
  height: number;
}

const MENU_GAP = 8;
const VIEWPORT_PADDING = 8;

/** Flip beside the trigger, then clamp inside the menu's real containing block. */
export function placeSessionContextMenu(
  anchor: SessionContextMenuAnchor,
  menu: SessionContextMenuSize,
  viewport: SessionContextMenuViewport
): { left: number; top: number } {
  const blockLeft = anchor.containingBlockLeft ?? 0;
  const blockRight = anchor.containingBlockRight ?? viewport.width;
  const blockTop = anchor.containingBlockTop ?? 0;
  const blockBottom = anchor.containingBlockBottom ?? viewport.height;
  const minLeft = blockLeft + VIEWPORT_PADDING;
  const maxLeft = Math.max(minLeft, blockRight - menu.width - VIEWPORT_PADDING);
  const rightPosition = anchor.right + MENU_GAP;
  const leftPosition = anchor.left - menu.width - MENU_GAP;
  const preferredLeft = rightPosition + menu.width <= blockRight - VIEWPORT_PADDING
    ? rightPosition
    : leftPosition;
  const viewportLeft = Math.min(maxLeft, Math.max(minLeft, preferredLeft));

  const minTop = blockTop + VIEWPORT_PADDING;
  const maxTop = Math.max(minTop, blockBottom - menu.height - VIEWPORT_PADDING);
  const belowPosition = anchor.bottom + MENU_GAP;
  const abovePosition = anchor.top - menu.height - MENU_GAP;
  const preferredTop = belowPosition + menu.height <= blockBottom - VIEWPORT_PADDING
    ? belowPosition
    : abovePosition >= minTop
      ? abovePosition
      : belowPosition;
  const viewportTop = Math.min(maxTop, Math.max(minTop, preferredTop));

  return { left: viewportLeft - blockLeft, top: viewportTop - blockTop };
}

const FUTURE_NEW_SESSION = 'Future-native: continue in a new session is not wired yet.';
const FUTURE_LOG = 'Future-native: transcript log viewing is not wired yet.';
const FUTURE_TAB_ACTION = 'Future-native: this action needs a session row target.';
const FUTURE_PROVIDER_MUTATION = 'Future-native: provider session mutation is not available.';

export function sessionContextMenuRoster(options: SessionContextMenuOptions): SessionContextMenuItem[] {
  const isSession = options.target === 'session' && Boolean(options.record);
  const canResume = isSession && options.canResume !== false;
  const canArchive = isSession && options.canArchive !== false && Boolean(options.record?.ownedId);
  const canDelete = isSession && options.canDelete !== false && Boolean(options.record?.ownedId);

  return [
    {
      id: 'resume-worktree',
      label: 'Resume in Worktree',
      enabled: canResume,
      disabledReason: canResume ? null : FUTURE_TAB_ACTION
    },
    {
      id: 'continue-new-session',
      label: 'Continue in New Session',
      enabled: false,
      disabledReason: FUTURE_NEW_SESSION
    },
    {
      id: 'view-log',
      label: 'View Log',
      enabled: false,
      disabledReason: FUTURE_LOG
    },
    {
      id: 'copy-id',
      label: 'Copy ID',
      enabled: isSession || options.target === 'center-tab',
      disabledReason: isSession || options.target === 'center-tab' ? null : FUTURE_TAB_ACTION
    },
    {
      id: 'archive',
      label: 'Archive',
      enabled: canArchive,
      disabledReason: canArchive
        ? null
        : isSession
          ? options.record?.source === 'provider'
            ? FUTURE_PROVIDER_MUTATION
            : 'Archive is not available for this session.'
          : FUTURE_TAB_ACTION
    },
    {
      id: 'delete',
      label: 'Delete',
      enabled: canDelete,
      disabledReason: canDelete
        ? null
        : isSession
          ? options.record?.source === 'provider'
            ? FUTURE_PROVIDER_MUTATION
            : 'Delete is not available for this session.'
          : FUTURE_TAB_ACTION
    }
  ];
}

export const SESSION_CONTEXT_MENU_ACTIONS: readonly SessionContextMenuAction[] = [
  'resume-worktree',
  'continue-new-session',
  'view-log',
  'copy-id',
  'archive',
  'delete'
];
