import { open } from '@tauri-apps/plugin-dialog';

// The native picker for images; the chosen paths ([] on cancel).
export async function pickFiles(): Promise<string[]> {
  const picked = await open({ multiple: true, filters: [{ name: 'Images', extensions: ['png', 'jpg', 'jpeg', 'gif', 'webp'] }] });
  return picked ?? [];
}
