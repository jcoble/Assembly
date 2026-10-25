// The rows the live socket sends for a scenario's thread from seq AFTER within 3 s, as one JSON array of
// { seq, prevSeq } on stdout. node scripts/features/live-rows.ts AFTER, with CHECK4_API, CHECK4_TOKEN and
// CHECK4_THREAD set (scripts/features/README.md). The token goes in the socket's query, never in argv or output.
const { CHECK4_API: api, CHECK4_TOKEN: token, CHECK4_THREAD: thread } = process.env;
const after = process.argv[2] ?? '0';
const rows: { seq: number; prevSeq?: number }[] = [];
const url = `${api!.replace(/^http/, 'ws')}/live?thread=${encodeURIComponent(thread!)}&after=${after}&token=${token}`;
const socket = new WebSocket(url);
socket.onmessage = (event) => {
  const frame = JSON.parse(String(event.data)) as { type: string; row?: { seq: number; prevSeq?: number } };
  if (frame.type === 'row' && frame.row) rows.push({ seq: frame.row.seq, prevSeq: frame.row.prevSeq });
};
socket.onerror = () => {
  console.error('the socket failed');
  process.exit(1);
};
setTimeout(() => {
  socket.close();
  console.log(JSON.stringify(rows));
  process.exit(0);
}, 3000);
