import { BASE_WS } from '../api';
import { delay, TrackedWebSocket } from '../debug.svelte';
import { liveStep, type Row } from './store';

// After the socket drops (the backend closed it or went away), it is reopened this often.
const RECONNECT_MS = 1000;

// The conversation's one /live socket. It sends the rows after `after` in seq
// order, then live rows. A row whose prevSeq isn't the watermark means rows
// were missed, so the socket is reopened from the last row applied. Aborting `signal` closes
// it; it is counted in the debug sockets until its close event.
export function openLive(opts: {
  token: string;
  threadId: string;
  after: number;
  signal: AbortSignal;
  onRow: (row: Row) => void;
}): void {
  const { token, threadId, signal, onRow } = opts;
  if (signal.aborted) return;
  let watermark = opts.after;
  let socket: WebSocket | null = null;

  const connect = () => {
    const ws = new TrackedWebSocket(
      `${BASE_WS}/live?thread=${encodeURIComponent(threadId)}&after=${watermark}&token=${encodeURIComponent(token)}`,
    );
    socket = ws;
    ws.onmessage = (event) => {
      if (socket !== ws) return;
      const frame = JSON.parse(String(event.data)) as { type: string; row?: Row };
      if (frame.type !== 'row' || !frame.row) return; // a ping
      const step = liveStep(watermark, frame.row);
      if (step === 'apply') {
        watermark = frame.row.seq;
        onRow(frame.row);
      } else if (step === 'reload') {
        socket = null;
        ws.close();
        connect();
      }
    };
    ws.onclose = () => {
      if (socket !== ws) return;
      socket = null;
      void reconnect();
    };
  };

  const reconnect = async () => {
    try {
      await delay(RECONNECT_MS, signal);
      connect();
    } catch {
      // The conversation closed.
    }
  };

  signal.addEventListener(
    'abort',
    () => {
      const ws = socket;
      socket = null;
      ws?.close();
    },
    { once: true },
  );
  connect();
}
