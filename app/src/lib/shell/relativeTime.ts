/**
 * relativeTime.ts — turns a session's last-activity stamp into something a
 * person can read at a glance.
 *
 * The rail used to print the stamp exactly as the scanner found it:
 * `2026-07-27T21:16:04.312Z`. That is UTC, so it is the wrong hour for anyone
 * reading it, and comparing two of them means reading twenty-four characters
 * looking for the digits that differ.
 *
 * PURE: no DOM, no store, and no clock of its own — `now` is a parameter, so a
 * test can stand anywhere in time. The component passes `new Date()`.
 *
 * Everything it prints is in the reader's own time zone.
 */

import { formatFullDateTime, formatMonthDayTime, formatMonthDayYear } from './dateFormat.ts';

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

/**
 * How long counts as "just now". Below a minute there is nothing useful to
 * say, and "0m ago" reads like a bug.
 */
const JUST_NOW = MINUTE;

/**
 * A stamp inside the last day is said as a distance ("5m ago"), because that is
 * how recent work is actually thought about. Older than that it becomes a date,
 * because "312h ago" is not a time anybody can place.
 */
export function formatLastActivity(lastActivity: string | null | undefined, now: Date): string {
  const when = parseStamp(lastActivity);
  if (!when) return '';

  const elapsed = now.getTime() - when.getTime();

  // Negative elapsed lands here too. A stamp in the future means the clocks
  // disagree, not that something is scheduled, so it reads as "just now"
  // rather than "-3m ago".
  if (elapsed < JUST_NOW) return 'just now';
  if (elapsed < HOUR) return `${Math.floor(elapsed / MINUTE)}m ago`;
  if (elapsed < DAY) return `${Math.floor(elapsed / HOUR)}h ago`;
  if (isYesterday(when, now)) return 'yesterday';
  if (when.getFullYear() === now.getFullYear()) return sameYearStamp(when);

  return olderYearStamp(when);
}

/**
 * The short distance GitHub prints beside pull request events: "5m", "3h",
 * "2d", "1w", "1mo", "1y". A page of events reads as a timeline this way; the
 * exact moment stays one hover away through `exactLocalTime`.
 */
export function formatAge(stamp: string | null | undefined, now: Date): string {
  const when = parseStamp(stamp);
  if (!when) return '';
  const elapsed = now.getTime() - when.getTime();
  if (elapsed < JUST_NOW) return 'now';
  if (elapsed < HOUR) return `${Math.floor(elapsed / MINUTE)}m`;
  if (elapsed < DAY) return `${Math.floor(elapsed / HOUR)}h`;
  if (elapsed < 7 * DAY) return `${Math.floor(elapsed / DAY)}d`;
  if (elapsed < 30 * DAY) return `${Math.floor(elapsed / (7 * DAY))}w`;
  if (elapsed < 365 * DAY) return `${Math.floor(elapsed / (30 * DAY))}mo`;
  return `${Math.floor(elapsed / (365 * DAY))}y`;
}

/**
 * The full local date and time, for hovering over a shortened stamp. "3h ago"
 * is the right thing to read at a glance and the wrong thing to quote in a bug
 * report, so the exact moment stays one hover away.
 */
export function exactLocalTime(lastActivity: string | null | undefined): string {
  const when = parseStamp(lastActivity);
  return when ? formatFullDateTime(when) : '';
}

/** Anything unparseable is nothing at all — the rail then prints no stamp,
 * which is better than printing the raw text back at the user. */
function parseStamp(value: string | null | undefined): Date | null {
  const trimmed = (value ?? '').trim();
  if (!trimmed) return null;

  const when = new Date(trimmed);
  return Number.isNaN(when.getTime()) ? null : when;
}

/** Calendar days apart, in local time — not "24 hours ago". At 1am, work from
 * 11pm last night is three hours old and says so; at 2pm it is yesterday. */
function isYesterday(when: Date, now: Date): boolean {
  const yesterday = new Date(now.getFullYear(), now.getMonth(), now.getDate() - 1);
  return when.getFullYear() === yesterday.getFullYear()
    && when.getMonth() === yesterday.getMonth()
    && when.getDate() === yesterday.getDate();
}

/** `Jul 27, 9:16 PM` — the year is this one, so saying it adds nothing. */
function sameYearStamp(when: Date): string {
  return formatMonthDayTime(when);
}

/** `Jul 27, 2025` — past the turn of the year the hour stops mattering. */
function olderYearStamp(when: Date): string {
  return formatMonthDayYear(when);
}
