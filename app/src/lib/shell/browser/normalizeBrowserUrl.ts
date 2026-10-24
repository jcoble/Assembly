/**
 * normalizeBrowserUrl.ts — turn whatever the user typed into a browsable URL.
 *
 * Pure: no DOM, no storage, no backend.
 *
 * Accepted shorthands:
 *   ":5177"                  → http://localhost:5177/
 *   "localhost:5177/foo"     → http://localhost:5177/foo
 *   "127.0.0.1", "[::1]:80"  → http://…
 *   "www.example.com"         → https://www.example.com/
 *   "https://example.com"    → unchanged (normalized by the URL parser)
 *   "file:///Users/me/report.html" → local file URL
 *
 * Plain file paths are intentionally not guessed. A local file must use an
 * explicit file:// URL; the native boundary verifies that it exists and is a
 * regular file before WebKit sees it.
 */
export function normalizeBrowserUrl(value: string): string {
  const trimmedValue = value.trim();
  if (!trimmedValue) return '';

  const withProtocol = /^https?:\/\//i.test(trimmedValue)
    ? trimmedValue
    : trimmedValue.startsWith(':')
      ? `http://localhost${trimmedValue}`
      : /^(localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\])(:\d+)?(\/.*)?$/i.test(trimmedValue)
        ? `http://${trimmedValue}`
        : /^[\w.-]+\.[a-z]{2,}(:\d+)?(\/.*)?$/i.test(trimmedValue)
          ? `https://${trimmedValue}`
          : trimmedValue;

  try {
    const parsedUrl = new URL(withProtocol);
    if (!['http:', 'https:', 'file:'].includes(parsedUrl.protocol)) return '';
    if (parsedUrl.username || parsedUrl.password) return '';
    if (parsedUrl.protocol === 'file:' && parsedUrl.host && parsedUrl.host !== 'localhost') return '';
    return parsedUrl.toString();
  } catch {
    return '';
  }
}
