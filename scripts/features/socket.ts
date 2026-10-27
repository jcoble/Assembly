// The socket on the case's own machine (plan 5.1): hello, a welcome naming that machine, and not_found for a call
// nobody registered. node scripts/features/socket.ts, with CHECK4_HOST_API, CHECK4_HOST_TOKEN and CHECK4_HOST_NAME
// set (scripts/features/README.md). The token goes in the hello frame, never in argv or output.
const { CHECK4_HOST_API: api, CHECK4_HOST_TOKEN: token, CHECK4_HOST_NAME: host } = process.env;
const label = (name: string) => name.split('.')[0]!.toLowerCase();

function fail(step: string, why: string): never {
  console.log(`fail ${step}: ${why}`);
  process.exit(1);
}

const socket = new WebSocket(`${api!.replace(/^http/, 'ws')}/socket`);
const frames: { t: string; machine?: string; id?: number; code?: string }[] = [];
let next: (() => void) | undefined;
socket.onmessage = (event) => {
  frames.push(JSON.parse(String(event.data)));
  next?.();
};
socket.onerror = () => fail('the socket opens', 'it failed');
socket.onclose = (event) => fail('the socket stays open', `it closed with ${event.code}`);
const frame = () =>
  new Promise<(typeof frames)[number]>((resolve) => {
    const take = () => frames.length && resolve(frames.shift()!);
    next = take;
    take();
  });
setTimeout(() => fail('the socket answers within 10 s', 'it was still waiting'), 10_000);

await new Promise((resolve) => (socket.onopen = resolve));
socket.send(JSON.stringify({ t: 'hello', token, protocol: 1 }));
const welcome = await frame();
if (welcome.t !== 'welcome') fail('hello is answered with welcome', `it was ${welcome.t}`);
if (label(welcome.machine ?? '') !== label(host!)) fail(`the welcome names ${host}`, `it named ${welcome.machine}`);
console.log(`ok hello is answered with welcome from ${welcome.machine}`);

socket.send(JSON.stringify({ t: 'call', id: 1, feature: 'check4', method: 'nothing', args: {} }));
const answer = await frame();
if (answer.t !== 'error' || answer.id !== 1 || answer.code !== 'not_found')
  fail('an unknown call answers not_found', `it answered ${JSON.stringify(answer)}`);
console.log('ok an unknown call answers not_found');

socket.onclose = null;
socket.close();
process.exit(0);
