import { isPermissionGranted, requestPermission, sendNotification } from '@tauri-apps/plugin-notification';

// A macOS notification; asks for permission the first time.
export async function notify(title: string, body?: string): Promise<void> {
  if (!(await isPermissionGranted()) && (await requestPermission()) !== 'granted') return;
  sendNotification({ title, body });
}
