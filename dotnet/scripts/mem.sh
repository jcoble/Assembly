#!/usr/bin/env bash
# Memory on this Mac, by pid, never by name:
#   dotnet/scripts/mem.sh sample [label]
# Prints pid, ppid, rss_kb and a short command for the backend (from ~/assembly-dotnet/mac/backend.pid) and every
# process under it, and for the Assembly app (found by its bundle path) and the WebKit GPU, Networking and
# WebContent processes it's responsible for (launchd starts those, so they're found by macOS's responsible pid,
# not by parent). Then a total for each, and the same lines appended to ~/assembly-dotnet/mac/mem.csv.
set -euo pipefail
mode=${1:?usage: dotnet/scripts/mem.sh sample [label]}
[ "$mode" = sample ] || { echo "unknown mode: $mode" >&2; exit 2; }
home=~/assembly-dotnet/mac
backend=$(cat "$home/backend.pid" 2>/dev/null || echo 0)
exec python3 - "$backend" "${2:-}" "$home/mem.csv" <<'PY'
import ctypes, os, subprocess, sys, time
backend, label, csv = int(sys.argv[1]), sys.argv[2], sys.argv[3]
responsible = ctypes.CDLL("/usr/lib/libSystem.B.dylib").responsibility_get_pid_responsible_for_pid
responsible.argtypes, responsible.restype = [ctypes.c_int], ctypes.c_int

procs = {}
for line in subprocess.run(["/bin/ps", "-A", "-o", "pid=,ppid=,rss=,command="], capture_output=True, text=True).stdout.splitlines():
    pid, ppid, rss, command = line.split(None, 3)
    procs[int(pid)] = (int(ppid), int(rss), command)

def short(command):
    exe, _, args = command.partition(" ")
    if "/node_modules/" in command and exe.endswith("node"):
        return "node " + args.split("/node_modules/")[-1][:50]
    return (os.path.basename(exe) + " " + args)[:60] if "/" in exe else command[:60]

def tree(root):
    found = [root] if root in procs else []
    for pid in found:
        found += [child for child, (ppid, _, _) in procs.items() if ppid == pid and child not in found]
    return found

app = next((pid for pid, (_, _, c) in procs.items() if "Assembly.app/Contents/MacOS/" in c), 0)
webkit = [pid for pid, (_, _, c) in procs.items() if "com.apple.WebKit." in c and app and responsible(pid) == app]
groups = [("backend", tree(backend)), ("app", ([app] if app else []) + webkit)]

stamp = time.strftime("%Y-%m-%dT%H:%M:%S")
with open(csv, "a") as out:
    if out.tell() == 0:
        out.write("time,label,group,pid,ppid,rss_kb,command\n")
    for name, pids in groups:
        print(f"== {name}")
        for pid in pids:
            ppid, rss, command = procs[pid]
            print(f"{pid:>7} {ppid:>7} {rss:>9}  {short(command)}")
            out.write(f'{stamp},{label},{name},{pid},{ppid},{rss},"{short(command)}"\n')
        total = sum(procs[pid][1] for pid in pids)
        print(f"   total {total:>9} kb ({total / 1024:.0f} MB), {len(pids)} processes")
        out.write(f"{stamp},{label},{name},total,,{total},\n")
PY
