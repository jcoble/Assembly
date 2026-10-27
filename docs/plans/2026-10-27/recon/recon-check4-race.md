Recon, read-only, 2026-10-27, not re-verified unless marked

# check4 race recon (all verified by reading, repo base-machines)
1. Deploy/restart: scripts/check4.sh does NOT deploy or restart anything. dotnet/scripts/workbox.sh serve does:
   publish :108, unit file :111-119, daemon-reload :120, `systemctl --user restart` :121, then the only wait is `sleep 3` :122,
   `systemctl is-active` :123 (true as soon as the process forks, not when it listens), journalctl :124. No /version poll.
   Listening line is printed by dotnet/Assembly.Remote/Serve/ServeRun.cs:53 after startup, hence ~4 s on a cold start.
2. Message: scripts/check4.sh:78 (inside preflight :68-81). The /version calls are single shots: local :71, box :72, via api() :61-66
   (curl -s --max-time 30, no --retry). Empty result => `blocked` set => every case fails with that reason (:108-109). No retry. 
   box_base = 127.0.0.1:(BOX_PORT+10000) :57 (the local backend's tunnel).
3. Smallest fix: in scripts/check4.sh replace :72 with a poll, ~3 lines:
     for _ in $(seq 30); do box_commit=$(api box GET /version | jq -r '.commit // empty' 2>/dev/null || true); [ -n "$box_commit" ] && break; sleep 1; done
   (same shape for local :71 if wanted). Net diff +2/-1 lines, one file.
   Alternative (root cause side): workbox.sh:122 `sleep 3` -> poll on the box, e.g. `for _ in $(seq 30); do journalctl ... | grep -q listening || sleep 1`; 
   but the tunnel can lag too, so polling through the tunnel in check4 covers both. 
   Existing waits to mirror (no shared helper exists): check4.sh:127-132 (for seq 120 / sleep 2 loop), scripts/features/rows.sh:11-14, 
   shell/assembly.sh:51 and dotnet/scripts/desktop.sh:70 (nc -z loops). Reuse the inline `for _ in $(seq N)` idiom; no new function.
Assumed (not checked): tunnel re-establishes by itself; the rerun passing is consistent with that.
