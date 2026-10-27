#!/usr/bin/env bash
# Background work is held, never started and dropped (TR1): fails, naming each line, if a tracked .cs line throws a
# task away (_ = FooAsync(...), _ = Task.Run(...), _ = x.ContinueWith(...)), starts one as a bare statement
# (Task.Run, Task.Factory.StartNew, ThreadPool.QueueUserWorkItem), starts a new Thread, or is async void. Tests are
# scanned too, with no exceptions.
cd "$(dirname "$0")/../.."
# A discard is '_ =' with any spacing, but not '_ =>' or '_ =='. async void counts unless the line is a comment.
bare='^[[:space:]]*_[[:space:]]*=[^=>].*(Async|Task\.Run|ContinueWith)[[:space:]]*\(|^[[:space:]]*(_[[:space:]]*=[[:space:]]*)?(Task\.Run|Task\.Factory\.StartNew|ThreadPool\.[A-Za-z]*QueueUserWorkItem)[[:space:]]*\(|^[[:space:]]*new Thread[[:space:]]*\(|^[[:space:]]*([^/*[:space:]].*)?async void'
hits=$(git grep -nE "$bare" -- '*.cs')
rc=$?
[ "$rc" -le 1 ] || { echo "bare task starts: git grep failed ($rc); refusing" >&2; exit 2; }
[ -z "$hits" ] && { echo "bare task starts: none"; exit 0; }
while IFS= read -r hit; do
  echo "bare task starts: $hit drops its task; start it with BackgroundWork.Start, or hold it in its owner and await it in its dispose" >&2
done <<<"$hits"
exit 1
