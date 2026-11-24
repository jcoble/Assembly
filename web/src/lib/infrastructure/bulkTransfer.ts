/**
 * Generic chunked transfer helpers over `.NET`'s `/socket/bulk` channel.
 *
 * Centralized here so any feature that streams large payloads (files, attachments,
 * Notion images, etc.) reuses the exact same chunk sizing, base64 encoding,
 * cancellation, and cleanup semantics in one place.
 */

export const DEFAULT_BULK_CHUNK_BYTES = 256 * 1024;

export function bytesToBase64(bytes: Uint8Array): string {
  let binary = '';
  const step = 0x8000;
  for (let i = 0; i < bytes.length; i += step) {
    binary += String.fromCharCode(...bytes.subarray(i, i + step));
  }
  return btoa(binary);
}

export interface ChunkedUploadHooks<TResult> {
  begin: (totalBytes: number) => Promise<string>;
  sendChunk: (uploadId: string, offset: number, base64Data: string) => Promise<void>;
  commit: (uploadId: string) => Promise<TResult>;
  drop: (uploadId: string) => Promise<void>;
  chunkBytes?: number;
}

/**
 * Uploads UTF-8 text or raw bytes in bounded chunks with automatic `drop()` cleanup on failure/abort.
 */
export async function runChunkedUpload<TResult>(
  payload: string | Uint8Array,
  hooks: ChunkedUploadHooks<TResult>
): Promise<TResult> {
  const bytes = typeof payload === 'string' ? new TextEncoder().encode(payload) : payload;
  const chunkSize = hooks.chunkBytes ?? DEFAULT_BULK_CHUNK_BYTES;
  const uploadId = await hooks.begin(bytes.byteLength);

  try {
    for (let offset = 0; offset < bytes.byteLength; offset += chunkSize) {
      const slice = bytes.subarray(offset, Math.min(offset + chunkSize, bytes.byteLength));
      await hooks.sendChunk(uploadId, offset, bytesToBase64(slice));
    }
    return await hooks.commit(uploadId);
  } catch (err) {
    void hooks.drop(uploadId).catch(() => {});
    throw err;
  }
}
