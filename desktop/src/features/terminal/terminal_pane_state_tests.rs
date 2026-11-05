//! What the Terminal tab says and does in each state of its connection and its shell (spec section 4).

use super::*;
use crate::core::state::machines::Machines;

const GONE: &str = "This terminal has ended. Close it and open it again for a new shell.";
const SOURCE: &str = "The terminal's original owning source is unavailable or changed.";
const RECONNECTING: &str = "Reconnecting…";

fn unavailable(value: &str) -> Value {
    json!({"unavailable": value})
}

fn output(from: usize, text: &str) -> Value {
    json!({"seq": from + text.len(), "prevSeq": from, "data": STANDARD.encode(text)})
}

/// The tab on session a, following a shell that has printed `kept`.
async fn showing<'a>(cx: &'a mut TestAppContext, kept: &str, machines: Vec<Value>) -> Open<'a> {
    let terminals = Terminals {
        kept: kept.as_bytes().to_vec(),
        machines,
        ..Default::default()
    };
    let mut open = open(cx, terminals, true).await;
    let expected = kept.trim_end().to_string();
    open.until("the shell's output", |_, pane| {
        following(pane) && pane.grid.text() == expected
    })
    .await;
    open
}

/// A machine as the `machines` topic has it.
fn machine(reachable: bool) -> Value {
    json!({"name": "Workbox Test", "kind": "remote", "reachable": reachable, "reason": null, "checked": true})
}

impl Open<'_> {
    /// Asserts the note, its color, the output and that keys go nowhere.
    #[track_caller]
    fn assert_stopped(&mut self, shown: Shown, note: &str, error: bool, text: &str) {
        self.pane.read_with(self.cx, |pane, _| {
            assert_eq!(pane.shown, shown);
            assert_eq!(pane.status().as_deref(), Some(note));
            assert_eq!(pane.error_note(), error, "the note's color");
            assert_eq!(pane.grid.text(), text, "the output");
            assert!(!pane.typing());
        });
        let before = self.terminals.lock().unwrap().of("write").len();
        self.focus();
        self.cx.simulate_keystrokes("a");
        self.cx.run_until_parked();
        let after = self.terminals.lock().unwrap().of("write").len();
        assert_eq!(after, before, "a key was sent while typing is off");
    }

    fn focus(&mut self) {
        let focus = self.pane.read_with(self.cx, |pane, _| pane.focus.clone());
        self.cx.update(|window, cx| window.focus(&focus, cx));
    }

    fn refuse(&mut self, method: &str, code: &str, message: &str) {
        (self.terminals.lock().unwrap()).refuse(method, code, message);
    }

    fn allow(&mut self, method: &str) {
        self.terminals.lock().unwrap().allow(method);
    }

    fn count(&self, method: &str) -> usize {
        self.terminals.lock().unwrap().of(method).len()
    }

    /// Starts the machines list and waits for its first state.
    async fn start_machines(&mut self) {
        let machines = self.cx.update(|_, cx| {
            Machines::start(cx);
            Machines::try_global(cx).unwrap()
        });
        let tell = self.changed.clone();
        let heard = self
            .cx
            .update(|_, cx| cx.observe(&machines, move |_, _| _ = tell.unbounded_send(())));
        let (cx, watched) = (&mut *self.cx, machines.clone());
        until(
            "the machines list",
            &mut self.changes,
            &self.changed,
            || {
                cx.run_until_parked();
                cx.update(|_, cx| watched.read(cx).machines().next().is_some())
            },
        )
        .await;
        drop(heard);
    }

    /// A change on the `machines` topic.
    fn machine_changed(&mut self, reachable: bool) {
        self.cx.update(|_, cx| {
            let machines = Machines::try_global(cx).unwrap();
            machines.update(cx, |machines, cx| {
                let update = Update::Change {
                    key: "Workbox Test".into(),
                    item: machine(reachable),
                };
                machines.apply(update, cx)
            })
        });
        self.cx.run_until_parked();
    }

    /// Waits for the call under way to settle, as the pane leaves `Opening`.
    async fn settled(&mut self) {
        self.until("the open settled", |_, pane| pane.shown != Shown::Opening)
            .await;
    }

    /// Keeps the wait awake while the terminal socket's state changes.
    fn watch_socket(&mut self) -> Subscription {
        let tell = self.changed.clone();
        self.cx.update(|_, cx| {
            let client = SocketClient::try_terminal(cx).unwrap();
            cx.observe(&client, move |_, _| _ = tell.unbounded_send(()))
        })
    }

    /// Whether the terminal socket is connected.
    fn socket_up(&mut self) -> bool {
        self.cx.update(|_, cx| {
            let client = SocketClient::try_terminal(cx).unwrap();
            matches!(
                client.read(cx).state(),
                crate::core::socket::connection_state::ConnectionState::Connected { .. }
            )
        })
    }
}

#[gpui::test]
async fn connection_lost_row_shows_reconnecting_over_the_output(cx: &mut TestAppContext) {
    let mut open = showing(cx, "hello", Vec::new()).await;
    open.push_row(unavailable("connection_lost"));
    open.assert_stopped(Shown::Reconnecting, RECONNECTING, false, "hello");
}

#[gpui::test]
async fn a_write_caught_in_the_drop_goes_quiet(cx: &mut TestAppContext) {
    let mut open = showing(cx, "hello", Vec::new()).await;
    open.refuse("write", "connection_lost", "the machine dropped");
    open.focus();
    open.cx.simulate_keystrokes("a");
    open.until("the write answered", |terminals, pane| {
        terminals.of("write").len() == 1 && !pane.writing()
    })
    .await;
    open.assert_stopped(Shown::Reconnecting, RECONNECTING, false, "hello");
    open.pane.read_with(open.cx, |pane, _| {
        assert!(pane._follow.is_some(), "still following");
        assert!(pane.typed.is_empty(), "the key was dropped, not queued");
    });
    // Back again: what was caught in the drop isn't sent.
    open.allow("write");
    open.push_row(output(5, ""));
    open.cx.run_until_parked();
    assert_eq!(open.count("write"), 1);
}

#[gpui::test]
async fn a_write_in_flight_when_the_socket_drops_goes_quiet(cx: &mut TestAppContext) {
    let mut open = showing(cx, "hello", Vec::new()).await;
    let _socket = open.watch_socket();
    {
        let mut terminals = open.terminals.lock().unwrap();
        terminals.drop_on = Some("write".into());
        terminals.refuse_hello = true;
    }
    open.focus();
    open.cx.simulate_keystrokes("a");
    open.until("the write aborted", |terminals, pane| {
        terminals.of("write").len() == 1 && !pane.writing()
    })
    .await;
    open.assert_stopped(Shown::Reconnecting, RECONNECTING, false, "hello");
    open.terminals.lock().unwrap().refuse_hello = false;
    open.until("back on the socket's own", |_, pane| following(pane))
        .await;
    assert_eq!(open.count("write"), 1, "the key wasn't sent again");
}

#[gpui::test]
async fn a_resize_caught_in_the_drop_goes_quiet(cx: &mut TestAppContext) {
    let mut open = showing(cx, "hello", Vec::new()).await;
    open.refuse("resize", "connection_lost", "the machine dropped");
    (open.pane).update(open.cx, |pane, cx| pane.resized((120, 40), cx));
    open.until("the resize sent", |terminals, _| {
        !terminals.of("resize").is_empty()
    })
    .await;
    open.cx.run_until_parked();
    open.assert_stopped(Shown::Reconnecting, RECONNECTING, false, "hello");
}

#[gpui::test]
async fn the_boundary_row_clears_reconnecting_and_resends_the_size(cx: &mut TestAppContext) {
    let mut open = showing(cx, "hello", Vec::new()).await;
    open.push_row(unavailable("connection_lost"));
    open.assert_stopped(Shown::Reconnecting, RECONNECTING, false, "hello");
    let before = open.count("resize");
    open.push_row(output(5, ""));
    let size = open.pane.read_with(open.cx, |pane, _| pane.size);
    open.until("the size sent again", |terminals, _| {
        terminals.of("resize").len() == before + 1
    })
    .await;
    assert_eq!(
        open.terminals.lock().unwrap().of("resize")[before],
        json!({"terminal": "term-1", "cols": size.0, "rows": size.1})
    );
    open.pane.read_with(open.cx, |pane, _| {
        assert_eq!(pane.shown, Shown::Open);
        assert_eq!(pane.status(), None);
        assert!(pane.typing());
    });
}

#[gpui::test]
async fn new_output_clears_reconnecting(cx: &mut TestAppContext) {
    let mut open = showing(cx, "hello", Vec::new()).await;
    open.push_row(unavailable("connection_lost"));
    open.assert_stopped(Shown::Reconnecting, RECONNECTING, false, "hello");
    open.push_row(output(5, " there"));
    open.pane.read_with(open.cx, |pane, _| {
        assert_eq!(pane.shown, Shown::Open);
        assert_eq!(pane.grid.text(), "hello there");
    });
    open.focus();
    open.cx.simulate_keystrokes("x");
    open.until("typing is back", |terminals, pane| {
        terminals.of("write").len() == 1 && !pane.writing()
    })
    .await;
}

#[gpui::test]
async fn the_window_socket_reconnecting_clears_it(cx: &mut TestAppContext) {
    let mut open = showing(cx, "hello", Vec::new()).await;
    // The window's socket has no "lost" signal, so it shows only once a call fails.
    open.refuse("write", "unreachable", "no answer");
    open.focus();
    open.cx.simulate_keystrokes("a");
    open.until("the write answered", |terminals, pane| {
        terminals.of("write").len() == 1 && !pane.writing()
    })
    .await;
    open.assert_stopped(Shown::Reconnecting, RECONNECTING, false, "hello");
    open.allow("write");
    open.terminals.lock().unwrap().kept = b"hello there".to_vec();
    let before = open.count("resize");
    open.socket_returned();
    open.until("caught up", |_, pane| {
        !pane.catching_up() && pane.grid.text() == "hello there"
    })
    .await;
    open.until("the size sent again", |terminals, _| {
        terminals.of("resize").len() == before + 1
    })
    .await;
    open.pane.read_with(open.cx, |pane, _| {
        assert_eq!(pane.shown, Shown::Open);
        assert_eq!(pane.status(), None);
    });
}

#[gpui::test]
async fn exit_during_the_outage_shows_exited(cx: &mut TestAppContext) {
    let mut open = showing(cx, "hello", Vec::new()).await;
    open.push_row(unavailable("connection_lost"));
    open.assert_stopped(Shown::Reconnecting, RECONNECTING, false, "hello");
    open.push_row(json!({"seq": 6, "prevSeq": 5, "exit": 7}));
    open.assert_stopped(Shown::Exited(7), "exited with 7", false, "hello");
}

#[gpui::test]
async fn missing_during_the_outage_shows_gone(cx: &mut TestAppContext) {
    let mut open = showing(cx, "hello", Vec::new()).await;
    let starts = open.count("start");
    open.push_row(unavailable("connection_lost"));
    open.assert_stopped(Shown::Reconnecting, RECONNECTING, false, "hello");
    open.push_row(unavailable("terminal_missing"));
    open.assert_stopped(Shown::Gone, GONE, false, "hello");
    assert_eq!(open.count("start"), starts, "no automatic restart");
}

#[gpui::test]
async fn terminal_missing_by_row_and_by_read_shows_gone(cx: &mut TestAppContext) {
    let mut open = showing(cx, "hello", Vec::new()).await;
    let starts = open.count("start");
    open.push_row(unavailable("terminal_missing"));
    open.assert_stopped(Shown::Gone, GONE, false, "hello");
    assert_eq!(open.count("start"), starts, "no automatic restart");

    pick("b", open.cx);
    open.until("b's shell", |_, pane| {
        following(pane) && pane.grid.text() == "hello"
    })
    .await;
    let starts = open.count("start");
    open.refuse("read", "terminal_missing", "the terminal has gone");
    open.socket_returned();
    open.until("the read answered", |_, pane| !pane.catching_up())
        .await;
    open.assert_stopped(Shown::Gone, GONE, false, "hello");
    assert_eq!(open.count("start"), starts, "no automatic restart");
}

#[gpui::test]
async fn source_changed_row_says_so_in_the_error_color(cx: &mut TestAppContext) {
    let mut open = showing(cx, "hello", Vec::new()).await;
    open.push_row(unavailable("source_changed"));
    open.assert_stopped(Shown::Said(SOURCE.into()), SOURCE, true, "hello");
}

#[gpui::test]
async fn a_read_or_resize_failure_stops_and_says_why_in_the_error_color(cx: &mut TestAppContext) {
    let mut open = showing(cx, "hello", Vec::new()).await;
    open.refuse("read", "failed", "the output can't be read");
    open.socket_returned();
    open.until("the read answered", |_, pane| !pane.catching_up())
        .await;
    let why = "the output can't be read";
    open.assert_stopped(Shown::Said(why.into()), why, true, "hello");

    open.allow("read");
    pick("b", open.cx);
    open.until("b's shell", |_, pane| {
        following(pane) && pane.grid.text() == "hello"
    })
    .await;
    let why = "that size can't be set";
    open.refuse("resize", "failed", why);
    (open.pane).update(open.cx, |pane, cx| pane.resized((120, 40), cx));
    open.until("the resize sent", |terminals, _| {
        !terminals.of("resize").is_empty()
    })
    .await;
    open.cx.run_until_parked();
    open.assert_stopped(Shown::Said(why.into()), why, true, "hello");
}

#[gpui::test]
async fn opening_while_down_is_quiet_and_retries_when_the_machine_returns(cx: &mut TestAppContext) {
    let mut open = showing(
        cx,
        "hello",
        vec![json!({"key": "Workbox Test", "item": machine(false)})],
    )
    .await;
    open.start_machines().await;
    open.refuse("list", "unreachable", "Workbox Test didn't answer");
    pick("b", open.cx);
    open.settled().await;
    open.assert_stopped(Shown::Reconnecting, RECONNECTING, false, "");
    // Nothing tries again on a timer.
    let lists = open.count("list");
    open.advance(60_000);
    assert_eq!(open.count("list"), lists, "it tried again on a timer");
    open.allow("list");
    open.machine_changed(true);
    open.until("the shell opened", |terminals, pane| {
        following(pane) && terminals.of("list").len() == lists + 1
    })
    .await;
    assert_eq!(
        open.pane.read_with(open.cx, |pane, _| pane.grid.text()),
        "hello"
    );
}

#[gpui::test]
async fn opening_while_the_socket_is_down_retries_when_it_returns(cx: &mut TestAppContext) {
    let mut open = showing(cx, "hello", Vec::new()).await;
    let _socket = open.watch_socket();
    {
        let mut terminals = open.terminals.lock().unwrap();
        terminals.drop_on = Some("resize".into());
        terminals.refuse_hello = true;
    }
    (open.pane).update(open.cx, |pane, cx| pane.resized((120, 40), cx));
    let terminals = open.terminals.clone();
    let (cx, changes, changed) = (&mut *open.cx, &mut open.changes, &open.changed);
    until("the socket dropped", changes, changed, || {
        cx.run_until_parked();
        let dropped = terminals.lock().unwrap().drop_on.is_none();
        dropped
            && cx.update(|_, cx| {
                let client = SocketClient::try_terminal(cx).unwrap();
                !matches!(
                    client.read(cx).state(),
                    crate::core::socket::connection_state::ConnectionState::Connected { .. }
                )
            })
    })
    .await;
    assert!(!open.socket_up());
    // The socket stays down until hellos are answered, so this call fails at once.
    pick("b", open.cx);
    open.settled().await;
    open.assert_stopped(Shown::Reconnecting, RECONNECTING, false, "");
    let starts = open.count("start");
    assert_eq!(starts, 1, "b's shell waits for the socket");
    open.terminals.lock().unwrap().refuse_hello = false;
    open.until("the shell opened", |terminals, pane| {
        following(pane) && terminals.of("start").len() > starts
    })
    .await;
}

#[gpui::test]
async fn hide_then_show_during_an_outage(cx: &mut TestAppContext) {
    let mut open = showing(
        cx,
        "hello",
        vec![json!({"key": "Workbox Test", "item": machine(true)})],
    )
    .await;
    let starts = open.count("start");
    open.start_machines().await;
    open.push_row(unavailable("connection_lost"));
    open.assert_stopped(Shown::Reconnecting, RECONNECTING, false, "hello");
    open.shown(false);
    open.cx.run_until_parked();
    assert_eq!(open.pane.read_with(open.cx, |pane, _| pane.status()), None);
    open.refuse("list", "connection_lost", "the machine dropped");
    open.shown(true);
    open.settled().await;
    // Hiding dropped the grid, so there's no output until the connection is back.
    open.assert_stopped(Shown::Reconnecting, RECONNECTING, false, "");
    open.allow("list");
    open.machine_changed(true);
    open.until("the output came back", |_, pane| {
        following(pane) && pane.grid.text() == "hello"
    })
    .await;
    assert_eq!(open.count("start"), starts, "the same shell came back");
}

#[gpui::test]
async fn switching_sessions_during_an_outage(cx: &mut TestAppContext) {
    let mut open = showing(
        cx,
        "hello",
        vec![json!({"key": "Workbox Test", "item": machine(true)})],
    )
    .await;
    open.start_machines().await;
    open.push_row(unavailable("connection_lost"));
    open.assert_stopped(Shown::Reconnecting, RECONNECTING, false, "hello");
    open.refuse("list", "unreachable", "Workbox Test didn't answer");
    pick("b", open.cx);
    open.settled().await;
    open.assert_stopped(Shown::Reconnecting, RECONNECTING, false, "");
    open.allow("list");
    pick("a", open.cx);
    open.until("a again", |_, pane| {
        following(pane) && pane.grid.text() == "hello"
    })
    .await;
    // Opened: it no longer waits on the machines list.
    let lists = open.count("list");
    open.machine_changed(true);
    assert_eq!(open.count("list"), lists, "an open pane opened again");
}

impl Open<'_> {
    /// Waits `seconds` on the test clock: no `list` a moment before, and exactly one more, from `owner`, by then.
    async fn retried_after(&mut self, seconds: u64, owner: &str) {
        let lists = self.count("list");
        self.advance(seconds * 1000 - 1);
        assert_eq!(self.count("list"), lists, "too soon, before {seconds} s");
        self.advance(1);
        let wanted = lists + 1;
        self.until("the retry failed", |terminals, pane| {
            terminals.of("list").len() >= wanted && pane.shown == Shown::Reconnecting
        })
        .await;
        let list = self.terminals.lock().unwrap().of("list");
        assert_eq!(list.len(), wanted, "more than one retry after {seconds} s");
        assert_eq!(list[wanted - 1]["owner"], owner);
    }
}

// Spec section 4, no push can come: the machine isn't offline and the window's socket is connected.
#[gpui::test]
async fn a_dead_link_retries_with_backoff_only_while_showing(cx: &mut TestAppContext) {
    let mut open = showing(
        cx,
        "hello",
        vec![json!({"key": "Workbox Test", "item": machine(true)})],
    )
    .await;
    open.start_machines().await;
    open.refuse("list", "unreachable", "Workbox Test didn't answer");
    pick("b", open.cx);
    open.settled().await;
    open.assert_stopped(Shown::Reconnecting, RECONNECTING, false, "");
    for seconds in [1, 2, 5, 5, 5] {
        open.retried_after(seconds, "b").await;
    }

    // Hidden: nothing more, however long.
    open.shown(false);
    open.cx.run_until_parked();
    let lists = open.count("list");
    open.advance(60_000);
    assert_eq!(open.count("list"), lists, "it retried while hidden");

    // Shown again with the link back: it opens, and that's the last of the retries.
    open.allow("list");
    open.shown(true);
    open.until("the shell opened", |_, pane| {
        following(pane) && pane.grid.text() == "hello"
    })
    .await;
    let lists = open.count("list");
    open.advance(60_000);
    assert_eq!(open.count("list"), lists, "it retried once open");

    // Another session: its own retries start over, and the old one's are gone.
    open.refuse("list", "unreachable", "Workbox Test didn't answer");
    open.shown(false);
    open.shown(true);
    open.settled().await;
    open.retried_after(1, "b").await;
    open.advance(1500);
    pick("a", open.cx);
    open.settled().await;
    for seconds in [1, 2] {
        open.retried_after(seconds, "a").await;
    }
}

#[gpui::test]
async fn folder_missing_fills_the_pane_with_the_backends_message(cx: &mut TestAppContext) {
    let why = "The session's folder /gone isn't available.";
    let mut terminals = Terminals::default();
    terminals.refuse("start", "failed", why);
    let mut open = open(cx, terminals, false).await;
    open.settled().await;
    open.assert_stopped(Shown::Said(why.into()), why, true, "");
    // With no link, the note fills the pane.
    open.pane
        .read_with(open.cx, |pane, _| assert!(pane.link.is_none()));
}

#[gpui::test]
async fn each_output_row_draws_again(cx: &mut TestAppContext) {
    let mut open = showing(cx, "hello", Vec::new()).await;
    let drawn = std::rc::Rc::new(std::cell::Cell::new(0));
    let (count, pane) = (drawn.clone(), open.pane.clone());
    let _drawn =
        (open.cx).update(|_, cx| cx.observe(&pane, move |_, _| count.set(count.get() + 1)));
    open.push_row(output(5, " there"));
    assert!(drawn.get() > 0, "the new output wasn't drawn");
    let text = open.pane.read_with(open.cx, |pane, _| pane.grid.text());
    assert_eq!(text, "hello there");
}
