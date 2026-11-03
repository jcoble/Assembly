use super::*;

impl Render for SourceControlPane {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme();
        let (muted, danger) = (theme.muted_foreground, theme.danger);
        let line = |text: String| div().px_3().text_sm().text_color(muted).child(text);
        let (shown, reading, session) = {
            let read = self.read.read(cx);
            let session = read.session().unwrap_or_default().to_string();
            (read.shown().clone(), read.reading(), session)
        };
        let header = h_flex()
            .px_3()
            .py_2()
            .gap_2()
            .justify_between()
            .child(
                div()
                    .text_sm()
                    .font_weight(FontWeight::MEDIUM)
                    .child(SOURCE_CONTROL),
            )
            .child(
                Button::new("source-control-refresh")
                    .ghost()
                    .compact()
                    .label(if reading { "Reading…" } else { "Refresh" })
                    .disabled(reading || shown == Shown::NoSession)
                    .on_click(cx.listener(|pane, _, _, cx| {
                        pane.read.update(cx, |read, cx| read.refresh(cx))
                    })),
            );
        let commit_status = match &shown {
            Shown::Read(status) => Some(status.clone()),
            _ => None,
        };
        let busy = self.action.read(cx).pending.is_some();
        let body = match shown {
            Shown::NoSession => {
                line("Pick a session to see its project's changes.".into()).into_any_element()
            }
            Shown::Reading => line("Reading the changes…".into()).into_any_element(),
            Shown::Failed(message) => {
                let not_repository = message.starts_with("This folder isn't a git repository yet.");
                v_flex()
                    .px_3()
                    .gap_2()
                    .items_start()
                    .child(div().text_sm().text_color(danger).child(message))
                    .child(
                        Button::new("source-control-retry")
                            .ghost()
                            .compact()
                            .label("Try again")
                            .on_click(cx.listener(|pane, _, _, cx| {
                                pane.read.update(cx, |read, cx| read.refresh(cx))
                            })),
                    )
                    .when(not_repository, |view| {
                        view.child(
                            Button::new("git-initialize")
                                .primary()
                                .label("Initialize repository")
                                .disabled(busy)
                                .on_click(cx.listener(|pane, _, _, cx| {
                                    if let Some(session) =
                                        pane.read.read(cx).session().map(String::from)
                                    {
                                        pane.start_write("init", json!({"session":session}), cx);
                                    }
                                })),
                        )
                    })
                    .into_any_element()
            }
            Shown::Read(status) => div()
                .id("source-control")
                .flex_1()
                .min_h_0()
                .overflow_y_scroll()
                .px_1()
                .child(
                    h_flex()
                        .px_2()
                        .pb_2()
                        .gap_2()
                        .justify_between()
                        .child(
                            div()
                                .min_w_0()
                                .text_sm()
                                .truncate()
                                .child(status.branch_line()),
                        )
                        .child({
                            let open = OpenBranchDiff {
                                session_id: session.clone(),
                            };
                            Button::new("source-control-branch-diff")
                                .ghost()
                                .compact()
                                .label("Branch diff")
                                .on_click(move |_, window, cx| {
                                    window.dispatch_action(Box::new(open.clone()), cx)
                                })
                        }),
                )
                .map(|pane| {
                    if status.files.is_empty() {
                        return pane.child(line("No changes.".into()));
                    }
                    pane.child(lists(&status, &session, cx.entity().downgrade(), busy, cx))
                })
                .when_some(status.more_line(), |pane, more| pane.child(line(more)))
                .into_any_element(),
        };
        let message = self.message.read(cx).value().to_string();
        let commit_allowed = commit_status
            .as_ref()
            .is_some_and(|status| self.amend || status.has_staged_files())
            && (self.amend || !message.trim().is_empty())
            && !busy;
        let commit_form = v_flex()
            .px_2()
            .py_2()
            .gap_2()
            .child(Input::new(&self.message).disabled(busy))
            .child(
                h_flex()
                    .gap_2()
                    .child(
                        Checkbox::new("git-amend")
                            .label("Amend")
                            .checked(self.amend)
                            .on_click(cx.listener(|pane, value, _, cx| {
                                pane.amend = *value;
                                cx.notify();
                            })),
                    )
                    .child(
                        Button::new("git-commit")
                            .primary()
                            .label(if self.amend { "Amend" } else { "Commit" })
                            .disabled(!commit_allowed)
                            .on_click(cx.listener(|pane, _, window, cx| pane.commit(window, cx))),
                    ),
            )
            .children(
                self.action
                    .read(cx)
                    .message
                    .clone()
                    .map(|message| div().text_sm().child(message)),
            )
            .children(
                self.action
                    .read(cx)
                    .failure
                    .clone()
                    .map(|message| div().text_sm().text_color(danger).child(message)),
            );
        let mut all_actions = h_flex().px_2().gap_2();
        if let Some(status) = &commit_status {
            let stage = json!({"session":session,"expectedFingerprint":status.fingerprint});
            let unstage = stage.clone();
            let discarded = status.files.clone();
            let fingerprint = status.fingerprint.clone();
            let omitted = status.total_files.saturating_sub(status.files.len());
            all_actions = all_actions
                .child(
                    Button::new("git-stage-all")
                        .ghost()
                        .compact()
                        .label("Stage all")
                        .disabled(busy || status.total_files == 0)
                        .on_click(cx.listener(move |pane, _, _, cx| {
                            pane.start_write("stage", stage.clone(), cx)
                        })),
                )
                .child(
                    Button::new("git-unstage-all")
                        .ghost()
                        .compact()
                        .label("Unstage all")
                        .disabled(busy || !status.has_staged_files())
                        .on_click(cx.listener(move |pane, _, _, cx| {
                            pane.start_write("unstage", unstage.clone(), cx)
                        })),
                )
                .child(
                    Button::new("git-discard-all")
                        .ghost()
                        .compact()
                        .label("Discard all…")
                        .disabled(busy || status.total_files == 0)
                        .on_click(cx.listener(move |pane, _, window, cx| {
                            pane.ask_discard(
                                discarded.clone(),
                                Some(fingerprint.clone()),
                                omitted,
                                window,
                                cx,
                            )
                        })),
                );
        }
        let project_actions = h_flex()
            .px_2()
            .gap_2()
            .flex_wrap()
            .child(
                Button::new("git-add-remote")
                    .ghost()
                    .compact()
                    .label("Add remote…")
                    .disabled(busy || commit_status.is_none())
                    .on_click(cx.listener(|pane, _, window, cx| {
                        if let Some(session) = pane.read.read(cx).session().map(String::from) {
                            super::remote_dialogs::add(session, pane.action.clone(), window, cx);
                        }
                    })),
            )
            .child(
                Button::new("git-stash")
                    .ghost()
                    .compact()
                    .label("Stash changes…")
                    .disabled(busy || commit_status.is_none())
                    .on_click(cx.listener(|pane, _, window, cx| {
                        if let Some(session) = pane.read.read(cx).session().map(String::from) {
                            super::stash_dialogs::create(session, pane.action.clone(), window, cx);
                        }
                    })),
            )
            .child(
                Button::new("git-stashes")
                    .ghost()
                    .compact()
                    .label("Stashes…")
                    .disabled(busy || commit_status.is_none())
                    .on_click(cx.listener(|pane, _, window, cx| {
                        if let Some(session) = pane.read.read(cx).session().map(String::from) {
                            super::stash_dialogs::list(session, pane.action.clone(), window, cx);
                        }
                    })),
            );
        let open_history = OpenGitHistory {
            session_id: session,
            path: None,
        };
        let history = v_flex()
            .child(
                h_flex()
                    .px_2()
                    .gap_2()
                    .child(
                        Button::new("history-section")
                            .ghost()
                            .compact()
                            .label(if self.history_open {
                                "▾ History"
                            } else {
                                "▸ History"
                            })
                            .on_click(cx.listener(|pane, _, _, cx| pane.toggle_history(cx))),
                    )
                    .child(
                        Button::new("open-git-history")
                            .ghost()
                            .compact()
                            .label("Open history")
                            .disabled(open_history.session_id.is_empty())
                            .on_click(move |_, window, cx| {
                                window.dispatch_action(Box::new(open_history.clone()), cx)
                            }),
                    ),
            )
            .when(self.history_open, |section| match &self.history {
                Some((_, timeline)) => section.child(div().h(px(240.)).child(timeline.clone())),
                None => section.child(line("Pick a session to see its history.".into())),
            });
        v_flex()
            .track_focus(&self.focus)
            .size_full()
            .child(header)
            .children(self.sync.as_ref().map(|(_, controls, _)| controls.clone()))
            .child(commit_form)
            .child(all_actions)
            .child(project_actions)
            .child(body)
            .child(history)
            .children(
                self.pull_requests
                    .as_ref()
                    .map(|(_, section)| section.clone()),
            )
    }
}
