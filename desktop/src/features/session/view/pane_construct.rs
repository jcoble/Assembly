//! Building a `SessionPane`: its composer, header, timeline, row renderer and the subscriptions it keeps.
use super::*;

impl SessionPane {
    pub fn new(client: Rc<dyn SessionClient>, window: &mut Window, cx: &mut Context<Self>) -> Self {
        let pane = cx.weak_entity();
        let on_action: OnAction = {
            let pane = pane.clone();
            Rc::new(move |action, window, cx| {
                pane.update(cx, |pane, cx| pane.on_action(action, window, cx))
                    .ok();
            })
        };
        let render_row: RenderRow = Rc::new(move |entry, last, window, cx| {
            let Some(pane) = pane.upgrade() else {
                return div().into_any_element();
            };
            let (row, fresh, fold, space) = pane.update(cx, |pane, cx| {
                (
                    pane.row(entry, cx),
                    pane.fresh_key(entry),
                    pane.open_fold_of(entry),
                    pane.space_under(entry),
                )
            });
            let row = match fresh {
                Some(key) => fade_in(key, row, window, cx),
                None => row,
            };
            // The space under a row by what it is (pane_rows.rs), inside the row so it grows with it.
            let row = div()
                .when(!last, |row| row.pb(space))
                .child(row)
                .into_any_element();
            // A fold's work grows in place as the fold opens and shuts.
            match fold {
                Some((key, open, toggled_at)) => {
                    let grown = turn(open, toggled_at, window, cx);
                    let faded = faded(grown.0);
                    grow(
                        format!("{key}:fold-grow").into(),
                        grown,
                        open,
                        faded,
                        row,
                        window,
                        cx,
                    )
                }
                None => row,
            }
        });
        let timeline = cx.new(|cx| Timeline::new(render_row, cx));
        let composer = cx.new(|cx| Composer::new(client.clone(), window, cx));
        let header = cx.new(|_| Header::default());
        let heard = [
            cx.subscribe(&composer, |pane, _, event, cx| match event {
                ComposerEvent::StartRequested { .. } => {}
                ComposerEvent::Sending => {
                    pane.refresh(Change::Nothing, cx);
                    pane.timeline
                        .update(cx, |timeline, cx| timeline.follow(FollowEvent::Send, cx));
                }
                // Acceptance is queued, even when newer delivered rows already arrived. Only real
                // pushed turns and the page's activeRunId establish a running turn.
                ComposerEvent::Accepted { key, receipt }
                    if pane.session.as_ref() == Some(&receipt.session_id)
                        && pane
                            .row_source
                            .as_ref()
                            .is_some_and(|source| source.machine == key.machine) =>
                {
                    pane.refresh(Change::Nothing, cx)
                }
                ComposerEvent::Accepted { .. } => {}
                ComposerEvent::PendingChanged => pane.refresh(Change::Nothing, cx),
            }),
            cx.subscribe(&timeline, |pane, _, TimelineEvent::NearTop, cx| {
                pane.load_older(cx)
            }),
            cx.observe(&header, |pane, _, cx| {
                pane.tell_offline(cx);
                cx.notify()
            }),
        ];
        let machines = cx.observe_global::<GlobalMachines>(|pane, cx| pane.follow_machines(cx));
        let mut pane = SessionPane {
            client,
            open_serial: 0,
            pending_open: None,
            active_open_serial: None,
            active_reopen: None,
            source_lost: false,
            reopen_queued: None,
            read_only: false,
            active_provider: None,
            _source_connections: Vec::new(),
            active_source_cut: None,
            focus: cx.focus_handle(),
            draft: None,
            session: None,
            row_source: None,
            view: SessionView::default(),
            known_run: None,
            folds: HashMap::new(),
            open_folds: HashSet::new(),
            kept: HashMap::new(),
            fresh: HashMap::new(),
            forms: HashMap::new(),
            plan_open: false,
            on_action,
            header,
            timeline,
            composer,
            reveal: None,
            revealed_at: Instant::now(),
            frame_asked: false,
            now: None,
            live_since: None,
            reading: HashMap::new(),
            expands: HashMap::new(),
            open_asks: HashSet::new(),
            growing: HashSet::new(),
            grow_asked: false,
            _clock: None,
            held: None,
            loading: false,
            failed_before: None,
            retry_first: None,
            status: SharedString::default(),
            _rows: None,
            _selected: None,
            _page: None,
            _loads: HashMap::new(),
            _writes: HashMap::new(),
            read_to: 0,
            _heard: heard,
            _machines_started: machines,
            _machines: None,
        };
        pane.follow_machines(cx);
        pane
    }
}
