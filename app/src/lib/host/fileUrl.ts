import { convertFileSrc } from '@tauri-apps/api/core';

// A file on this Mac as a URL the page can show, through the shell's asset protocol.
export const fileUrl = (path: string): string => convertFileSrc(path);
