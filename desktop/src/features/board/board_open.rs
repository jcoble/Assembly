//! Opening a row or tab on the board, from a click, a link or a notification.
use super::*;

impl BoardPanel {
    /// Rerun's new run, or a saved open row, as soon as it's listed.
    pub(super) fn open_pending(&mut self, cx: &mut Context<Self>) {
        if let Some(key) = self.pending_run.clone()
            && let Some(at) = self
                .entries
                .iter()
                .position(|entry| *entry == Entry::Run(key.clone()))
        {
            self.pending_run = None;
            self.open_run(&key, cx);
            self.list.scroll_to_reveal_item(at);
        }
        if self.reopened || self.entries.is_empty() {
            return;
        }
        self.reopened = true;
        for entry in self.entries.clone().iter() {
            match entry {
                Entry::Run(key)
                    if self.prefs.open.contains(&card_key(key))
                        && self.run_drops.read(cx).open_key().is_none() =>
                {
                    self.open_run(key, cx)
                }
                Entry::Session(key)
                    if self.prefs.open.contains(&card_key(key))
                        && self.session_drops.read(cx).open_key().is_none() =>
                {
                    self.open_session_row(key, cx)
                }
                _ => {}
            }
        }
    }

    pub(super) fn open_run(&mut self, key: &EngineKey, cx: &mut Context<Self>) {
        RunRow::open(
            card_key(key),
            &self.run_drops,
            Self::run_body(cx.weak_entity(), key.clone()),
            cx,
        );
    }

    pub(super) fn open_session_row(&mut self, key: &EngineKey, cx: &mut Context<Self>) {
        let body = Self::session_body(cx.weak_entity(), key.clone());
        let drawn = card_key(key);
        self.session_drops.update(cx, |drops, cx| {
            if !drops.is_open(&drawn) {
                drops.toggle(
                    drawn.clone(),
                    move |window, cx| {
                        div()
                            .px_3()
                            .pb_2()
                            .child(body(window, cx))
                            .into_any_element()
                    },
                    cx,
                );
            }
        });
    }

    /// A notification's question, open and in view. One that's been answered shows its answered row.
    pub fn show_need(&mut self, key: &EngineKey, cx: &mut Context<Self>) {
        self.show_tab(BoardTab::Needs, cx);
        let Some(need) = self.data.needs.get(key).cloned() else {
            return;
        };
        if need.state != "open" && !self.prefs.unfolded.contains(ANSWERED_FOLD) {
            self.toggle_fold(ANSWERED_FOLD, cx);
        }
        let Some(at) = self
            .entries
            .iter()
            .position(|entry| *entry == Entry::Need(key.clone()))
        else {
            return;
        };
        self.list.scroll_to_reveal_item(at);
        let drops = self.questions.read(cx).drops().clone();
        NeedsCard::open(
            card_key(key),
            &drops,
            QuestionRows::body(self.questions.downgrade(), key.clone(), &need),
            cx,
        );
        cx.notify();
    }

    /// A new run: show it on Workflows, open, when its row arrives. The run it came from stays listed.
    pub fn select_run(&mut self, key: EngineKey, cx: &mut Context<Self>) {
        self.pending_run = Some(key);
        self.show_tab(BoardTab::Workflows, cx);
    }

    pub(super) fn show_tab(&mut self, tab: BoardTab, cx: &mut Context<Self>) {
        if self.tab != tab {
            self.memory
                .scroll
                .insert(tab_index(self.tab), self.list.logical_scroll_top());
            self.tab = tab;
            self.prefs.tab = tab;
            self.prefs.save_tab(cx);
            self.entries = Rc::new(Vec::new());
            self.list.reset(0);
            self.rebuild(cx);
            self.restore_scroll();
        }
        cx.notify();
    }
}
