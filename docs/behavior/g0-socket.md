# g0-socket: behavior list

Draft by Claude, not corrected by Jesse.

g0-socket#1 The window connects to its backend with its token and shows which machine answered.
g0-socket#2 A call gives its result, or says why it failed. A call the window gave up on shows nothing.
g0-socket#3 A newer load replaces an older one: the older stops in the window and on the backend, and its result never shows.
g0-socket#4 Work nobody wants any more is cancelled on the backend.
g0-socket#5 A topic shows its state, then each change, until its view lets go.
g0-socket#6 A machine that doesn't answer is named in the error.
g0-socket#7 After a dropped connection the window reconnects at once and every view comes back current. A send, a new session or an image upload that was in flight is never sent again.
g0-socket#8 If that fails, the window shows Disconnected and reconnects when the backend is ready again or on Reconnect, never on a timer.
g0-socket#9 A reconnected chat gets only the rows it missed. If too many were missed, it reloads its newest page.
g0-socket#10 The list shows the newest 200 sessions, and older ones load on scroll.
g0-socket#11 A new or changed session moves to the top without a reload.
g0-socket#12 Helper sessions never appear in the list.
g0-socket#13 The selection survives list changes.
g0-socket#14 Machines show whether each can be reached (once the backend reports it).
g0-socket#15 The clock ticks only while something needs it.
