export type PasteCleanupMode = 'plain' | 'compact' | 'prompt' | 'reply';
export type PasteCleanupHistoryKind = 'cleaned' | 'reply';
export type PasteCleanupHistoryItem = {
  id: string;
  kind: PasteCleanupHistoryKind;
  mode: PasteCleanupMode;
  text: string;
  summary: string;
  charCount: number;
  createdAt: number;
};

export const pasteCleanupModes: PasteCleanupMode[] = ['plain', 'compact', 'prompt', 'reply'];

export function cleanupPasteText(input: string, mode: PasteCleanupMode): string {
  const normalized = normalizePastedText(input);

  switch (mode) {
    case 'compact':
      return normalized.replace(/\s+/g, ' ').trim();
    case 'prompt':
      return stripWrappingCodeFence(normalized).trim();
    case 'reply':
      return cleanupReplyPaste(normalized);
    case 'plain':
    default:
      return normalized.trim();
  }
}

export function formatPasteCleanupStats(input: string, output: string): string {
  const charCount = output.length;
  const trimmedCount = Math.max(0, input.length - output.length);
  if (trimmedCount === 0) return `${charCount.toLocaleString()} chars`;
  return `${charCount.toLocaleString()} chars - trimmed ${trimmedCount.toLocaleString()}`;
}

export function cleanupPasteReplyDraft(input: string): string {
  return normalizePastedText(input).trim();
}

export function summarizePasteCleanupHistoryText(input: string, maxLength = 72): string {
  const summary = normalizePastedText(input).replace(/\s+/g, ' ').trim();
  if (summary.length <= maxLength) return summary;

  const cutLength = Math.max(1, maxLength - 3);
  return `${summary.slice(0, cutLength).trimEnd()}...`;
}

export function createPasteCleanupHistoryItem(
  kind: PasteCleanupHistoryKind,
  text: string,
  mode: PasteCleanupMode,
  createdAt = Date.now()
): PasteCleanupHistoryItem {
  const normalizedText = normalizePastedText(text).trim();
  const randomID = Math.random().toString(36).slice(2);
  return {
    id: `${kind}-${createdAt}-${randomID}`,
    kind,
    mode,
    text: normalizedText,
    summary: summarizePasteCleanupHistoryText(normalizedText),
    charCount: normalizedText.length,
    createdAt
  };
}

function normalizePastedText(input: string): string {
  return input
    .replace(/^\uFEFF/, '')
    .replace(/\u00a0/g, ' ')
    .replace(/\r\n?/g, '\n')
    .split('\n')
    .map((line) => line.replace(/[ \t]+$/g, '').replace(/^\t+/g, ''))
    .join('\n')
    .replace(/\n{3,}/g, '\n\n');
}

function stripWrappingCodeFence(input: string): string {
  const trimmed = input.trim();
  const match = /^```[^\n]*\n(?<body>[\s\S]*?)\n```$/.exec(trimmed);
  return match?.groups?.body ?? trimmed;
}

function cleanupReplyPaste(input: string): string {
  let insideCodeFence = false;
  const lines: string[] = [];

  for (const rawLine of input.split('\n')) {
    const line = rawLine.replace(/^ {0,3}>\s?/, '');
    const trimmedLine = line.trim();

    if (!insideCodeFence && /^(copy code|copied!)$/i.test(trimmedLine)) {
      continue;
    }

    if (/^```/.test(trimmedLine)) {
      insideCodeFence = !insideCodeFence;
    }

    lines.push(line);
  }

  return lines.join('\n').replace(/\n{3,}/g, '\n\n').trim();
}
