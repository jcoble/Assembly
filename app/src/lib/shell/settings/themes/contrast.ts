/** An sRGB color. Channels are 0-255; alpha is 0-1. */
export type Rgb = { red: number; green: number; blue: number; alpha: number };

const byte = "(?:\\d{1,3})";
const alpha = "(?:\\d+(?:\\.\\d+)?|\\.\\d+)";
const rgbPattern = new RegExp(
  `^rgb\\(\\s*(${byte})\\s*,\\s*(${byte})\\s*,\\s*(${byte})\\s*\\)$`,
  "i"
);
const rgbaPattern = new RegExp(
  `^rgba\\(\\s*(${byte})\\s*,\\s*(${byte})\\s*,\\s*(${byte})\\s*,\\s*(${alpha})\\s*\\)$`,
  "i"
);

function validate(color: Rgb, label: string): Rgb {
  const channels = [color.red, color.green, color.blue];
  if (
    channels.some(
      (channel) => !Number.isInteger(channel) || channel < 0 || channel > 255
    )
  ) {
    throw new Error(
      `${label}: RGB channels must be integers from 0 through 255`
    );
  }
  if (!Number.isFinite(color.alpha) || color.alpha < 0 || color.alpha > 1) {
    throw new Error(`${label}: alpha must be from 0 through 1`);
  }
  return color;
}

/** Place a foreground color over a backdrop without discarding alpha. */
export function composite(foreground: Rgb, backdrop: Rgb): Rgb {
  validate(foreground, "foreground");
  validate(backdrop, "backdrop");

  const outputAlpha =
    foreground.alpha + backdrop.alpha * (1 - foreground.alpha);
  if (outputAlpha === 0) return { red: 0, green: 0, blue: 0, alpha: 0 };

  const channel = (front: number, back: number): number =>
    Math.round(
      (front * foreground.alpha +
        back * backdrop.alpha * (1 - foreground.alpha)) /
        outputAlpha
    );

  return {
    red: channel(foreground.red, backdrop.red),
    green: channel(foreground.green, backdrop.green),
    blue: channel(foreground.blue, backdrop.blue),
    alpha: outputAlpha,
  };
}

/**
 * Parse the deliberately small CSS color grammar used by shell theme tokens.
 * A translucent color requires an opaque backdrop so callers cannot
 * accidentally measure it as black or ignore its alpha channel.
 */
export function parseCssColor(value: string, backdrop?: Rgb): Rgb {
  const source = value.trim();
  let parsed: Rgb | undefined;

  const hex = source.match(/^#([0-9a-f]+)$/i)?.[1];
  if (hex?.length === 3) {
    parsed = {
      red: Number.parseInt(hex[0] + hex[0], 16),
      green: Number.parseInt(hex[1] + hex[1], 16),
      blue: Number.parseInt(hex[2] + hex[2], 16),
      alpha: 1,
    };
  } else if (hex?.length === 6 || hex?.length === 8) {
    parsed = {
      red: Number.parseInt(hex.slice(0, 2), 16),
      green: Number.parseInt(hex.slice(2, 4), 16),
      blue: Number.parseInt(hex.slice(4, 6), 16),
      alpha: hex.length === 8 ? Number.parseInt(hex.slice(6, 8), 16) / 255 : 1,
    };
  } else {
    const rgb = source.match(rgbPattern);
    const rgba = source.match(rgbaPattern);
    const match = rgb ?? rgba;
    if (match) {
      parsed = {
        red: Number(match[1]),
        green: Number(match[2]),
        blue: Number(match[3]),
        alpha: rgba ? Number(match[4]) : 1,
      };
    }
  }

  if (!parsed) {
    throw new Error(
      `Unsupported CSS color "${value}"; expected three, six, or eight digit hex, rgb(), or rgba()`
    );
  }
  validate(parsed, `CSS color "${value}"`);

  if (parsed.alpha < 1) {
    if (!backdrop)
      throw new Error(
        `CSS color "${value}" is translucent and requires an opaque backdrop`
      );
    validate(backdrop, "backdrop");
    if (backdrop.alpha !== 1) {
      throw new Error(
        `CSS color "${value}" cannot resolve against a translucent backdrop`
      );
    }
    return composite(parsed, backdrop);
  }

  return parsed;
}

/** WCAG relative luminance for an opaque sRGB color. */
export function relativeLuminance(color: Rgb): number {
  validate(color, "color");
  if (color.alpha !== 1)
    throw new Error(
      "Relative luminance requires a fully resolved opaque color"
    );

  const linear = (channel: number): number => {
    const value = channel / 255;
    return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
  };

  return (
    0.2126 * linear(color.red) +
    0.7152 * linear(color.green) +
    0.0722 * linear(color.blue)
  );
}

/** WCAG contrast ratio for two fully resolved colors. */
export function contrastRatio(foreground: Rgb, background: Rgb): number {
  const foregroundLuminance = relativeLuminance(foreground);
  const backgroundLuminance = relativeLuminance(background);
  const lighter = Math.max(foregroundLuminance, backgroundLuminance);
  const darker = Math.min(foregroundLuminance, backgroundLuminance);
  return (lighter + 0.05) / (darker + 0.05);
}

/** Parse and assert a labeled CSS foreground/background contrast pair. */
export function assertContrast(
  foreground: string,
  background: string,
  minimum: number,
  label: string
): void {
  if (!Number.isFinite(minimum) || minimum < 1) {
    throw new Error(
      `${label}: minimum contrast must be a finite ratio of at least 1`
    );
  }

  let resolvedBackground: Rgb;
  let resolvedForeground: Rgb;
  try {
    resolvedBackground = parseCssColor(background);
    resolvedForeground = parseCssColor(foreground, resolvedBackground);
  } catch (error) {
    const reason = error instanceof Error ? error.message : String(error);
    throw new Error(`${label}: ${reason}`);
  }
  const ratio = contrastRatio(resolvedForeground, resolvedBackground);
  if (ratio + Number.EPSILON < minimum) {
    throw new Error(
      `${label}: contrast ${ratio.toFixed(2)}:1 is below ${minimum.toFixed(
        2
      )}:1`
    );
  }
}
