/**
 * tone.ts — shared tone → CSS token mapping for Chip and Badge.
 *
 * Each tone maps to:
 *   - `color`   : CSS var for text/icon foreground
 *   - `bg`      : CSS var (or rgba literal) for subtle translucent background
 *
 * Components consume this via `toneStyle()` which returns an inline style string,
 * and expose a `data-tone` attribute for any additional CSS hook-ins.
 */

export type Tone = 'neutral' | 'live' | 'good' | 'bad' | 'attention' | 'muted';

interface ToneTokens {
  color: string;
  bg: string;
}

const TONE_MAP: Record<Tone, ToneTokens> = {
  neutral:   { color: 'var(--color-text-2)',    bg: 'var(--color-surface)'      },
  live:      { color: 'var(--color-live)',      bg: 'var(--color-live-bg)'      },
  good:      { color: 'var(--color-good)',      bg: 'var(--color-good-bg)'      },
  bad:       { color: 'var(--color-bad)',       bg: 'var(--color-bad-bg)'       },
  attention: { color: 'var(--color-attention)', bg: 'var(--color-attention-bg)' },
  muted:     { color: 'var(--color-text-3)',    bg: 'var(--color-surface)'      },
};

/**
 * Returns an inline `style` string applying the tone's color and background.
 * Pass directly to the element's `style` attribute.
 */
export function toneStyle(tone: Tone): string {
  const { color, bg } = TONE_MAP[tone] ?? TONE_MAP.neutral;
  return `--_tone-color:${color};--_tone-bg:${bg};`;
}

/**
 * Returns the raw tokens for a tone — useful when a component needs to
 * apply them differently (e.g. only color, not bg).
 */
export function toneTokens(tone: Tone): ToneTokens {
  return TONE_MAP[tone] ?? TONE_MAP.neutral;
}
