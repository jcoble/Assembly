import { openUrl as open } from '@tauri-apps/plugin-opener';

// A link, in the default browser.
export const openUrl = (url: string): Promise<void> => open(url);
