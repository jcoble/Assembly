/**
 * resourceDiskViewModel.ts — how the disk section reads, and what it refuses to
 * offer.
 *
 * The one rule worth stating out loud: only a Rust build folder and an
 * installed dependency folder ever get a reclaim button. A repository's own
 * history, an extra checkout and anything the app stores are shown so the
 * growth is visible, and that is all — removing those is either destructive or
 * belongs to the worktree route that already exists.
 *
 * The backend enforces the same rule. This file is what makes it visible, and
 * both are tested, because "the button was not rendered" is not a safety
 * property on its own.
 */

import { formatResourceBytes } from './resourceSampleViewModel.ts';
import type {
  DiskReclaimRequest,
  DiskUsageEntry,
  DiskUsageReport,
  DiskUsageSection
} from './resourceDiskTypes.ts';

export type DiskEntryView = DiskUsageEntry & {
  sizeLabel: string;
  /** The share of its section, 0 to 1, for the bar behind the row. */
  share: number;
};

export type DiskSectionView = Omit<DiskUsageSection, 'entries'> & {
  entries: DiskEntryView[];
  sizeLabel: string;
  reclaimableBytes: number;
  reclaimableLabel: string;
};

export type DiskReportView = {
  sections: DiskSectionView[];
  totalLabel: string;
  reclaimableBytes: number;
  reclaimableLabel: string;
};

/** Folders a build command puts back. Everything else is shown, never offered. */
export function diskEntryIsReclaimable(entry: DiskUsageEntry): boolean {
  return (
    entry.reclaimable && (entry.category === 'cargo-target' || entry.category === 'node-modules')
  );
}

export function shapeDiskReport(report: DiskUsageReport): DiskReportView {
  const sections = report.sections
    .map((section) => {
      const entries = [...section.entries]
        .sort((left, right) => right.bytes - left.bytes || left.path.localeCompare(right.path))
        .map((entry) => ({
          ...entry,
          reclaimable: diskEntryIsReclaimable(entry),
          sizeLabel: formatResourceBytes(entry.bytes),
          share: section.bytes > 0 ? Math.min(1, entry.bytes / section.bytes) : 0
        }));
      const reclaimableBytes = entries
        .filter((entry) => entry.reclaimable)
        .reduce((total, entry) => total + entry.bytes, 0);
      return {
        ...section,
        entries,
        sizeLabel: formatResourceBytes(section.bytes),
        reclaimableBytes,
        reclaimableLabel: formatResourceBytes(reclaimableBytes)
      } satisfies DiskSectionView;
    })
    .filter((section) => section.entries.length > 0)
    .sort((left, right) => right.bytes - left.bytes || left.label.localeCompare(right.label));

  const reclaimableBytes = sections.reduce((total, section) => total + section.reclaimableBytes, 0);
  return {
    sections,
    totalLabel: formatResourceBytes(report.totalBytes),
    reclaimableBytes,
    reclaimableLabel: formatResourceBytes(reclaimableBytes)
  };
}

export type DiskReclaimQuestion = {
  title: string;
  intro: string;
  lines: string[];
  confirmLabel: string;
  cancelLabel: string;
};

/** What the dialog says before a folder is removed. It names the exact path. */
export function describeReclaimQuestion(entry: DiskUsageEntry): DiskReclaimQuestion {
  const putsItBack =
    entry.category === 'cargo-target'
      ? 'A build puts it back, slower the first time.'
      : 'Installing dependencies puts it back.';
  return {
    title: 'Remove this folder?',
    intro: `${entry.categoryLabel}, ${formatResourceBytes(entry.bytes)}.`,
    lines: [entry.path, putsItBack, 'Nothing else on disk is touched.'],
    confirmLabel: `Remove ${formatResourceBytes(entry.bytes)}`,
    cancelLabel: 'Keep it'
  };
}

/** The request the backend receives, refused here for anything not offered. */
export function buildReclaimRequest(entry: DiskUsageEntry): DiskReclaimRequest | null {
  if (!diskEntryIsReclaimable(entry)) return null;
  return {
    path: entry.path,
    category: entry.category,
    expectedBytes: entry.bytes
  };
}

export function formatDiskMeasuredAgo(measuredAtMs: number, nowMs: number): string {
  const seconds = Math.max(0, Math.floor((nowMs - measuredAtMs) / 1000));
  if (seconds < 60) return 'measured just now';
  return `measured ${Math.floor(seconds / 60)}m ago`;
}
