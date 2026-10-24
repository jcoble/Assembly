import QRCode from 'qrcode';

/** What `phonePairing` returns; `expiresAt` is in milliseconds. */
export type PhonePairing = { url: string; machineName: string; code: string; expiresAt: number };
export type PhoneDevice = { id: string; name: string; createdAt: number; lastSeenAt: number | null };
export type PhonePanel = { pairing: PhonePairing; qr: string; devices: PhoneDevice[] };
/** Sends one workflow operation to the remote machine (`remoteWorkflowFromTauri`). */
export type RemoteOperation = (operation: string, args: object) => Promise<unknown>;

/** The JSON the phone scans: `{"v":1,"url":…,"machine":…,"code":…}`. */
export function pairingQrPayload(pairing: PhonePairing): string {
  return JSON.stringify({ v: 1, url: pairing.url, machine: pairing.machineName, code: pairing.code });
}

export async function loadPhoneDevices(run: RemoteOperation): Promise<PhoneDevice[]> {
  const result = await run('phoneDevices', {}) as { devices: PhoneDevice[] } | null;
  return result?.devices ?? [];
}

/** Makes a new pairing code, its QR image and the paired phones list; `null` once `signal` aborts. */
export async function loadPhonePanel(run: RemoteOperation, machineName: string, signal: AbortSignal): Promise<PhonePanel | null> {
  const pairing = await run('phonePairing', { machineName }) as PhonePairing;
  if (signal.aborted) return null;
  const qr = await QRCode.toDataURL(pairingQrPayload(pairing), { margin: 2, width: 440, errorCorrectionLevel: 'M' });
  if (signal.aborted) return null;
  const devices = await loadPhoneDevices(run);
  return signal.aborted ? null : { pairing, qr, devices };
}
