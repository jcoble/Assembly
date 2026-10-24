import { writeText } from '@tauri-apps/plugin-clipboard-manager';

// Text to the clipboard.
export const copyText = (text: string): Promise<void> => writeText(text);
