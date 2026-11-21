// What a person looks at after a journey: a video of the bench display and a screenshot of the window. Nothing asserts
// on either. The ffmpeg process is ours: started here, stopped by its pid and checked gone.
import { execFileSync, spawn } from 'node:child_process';
import { once } from 'node:events';

export type Video = { stop(): Promise<void> };

export async function startVideo(file: string, display: string): Promise<Video> {
  const ffmpeg = spawn('ffmpeg', ['-y', '-loglevel', 'error', '-f', 'x11grab', '-framerate', '10', '-video_size', '1710x990',
    '-i', display, '-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p', file], { stdio: ['ignore', 'ignore', 'pipe'] });
  let said = '';
  ffmpeg.stderr!.on('data', data => { said += String(data); });
  const exited = once(ffmpeg, 'exit');
  // ffmpeg that can't open the display ends at once; one that records keeps going.
  const early = await Promise.race([exited.then(() => true), new Promise<false>(resolve => setTimeout(resolve, 500, false))]);
  if (early) throw new Error(`video did not start: ${said.trim() || 'ffmpeg exited'}`);
  return {
    async stop() {
      if (ffmpeg.exitCode !== null || ffmpeg.signalCode !== null) return;
      ffmpeg.kill('SIGINT'); // ffmpeg finishes the file on SIGINT.
      const ended = await Promise.race([exited.then(() => true), new Promise<false>(resolve => setTimeout(resolve, 10_000, false))]);
      if (!ended) { ffmpeg.kill('SIGKILL'); await exited; }
    }
  };
}

export function shot(file: string): void {
  execFileSync(`${process.env.HOME}/.local/bin/assembly-native-ui`, ['shot', file], { stdio: 'ignore' });
}
