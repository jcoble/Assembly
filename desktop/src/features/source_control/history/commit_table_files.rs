//! The file list shown under an expanded row of the commit table.
use super::*;

impl CommitTimeline {
    pub(super) fn table_files(
        &self,
        commit: &crate::features::source_control::git_history::GitCommit,
        graph: f32,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        let theme = cx.theme();
        let (muted, danger, border, surface, hover, selected) = (
            theme.muted_foreground,
            theme.danger,
            theme.border,
            theme.secondary.opacity(0.4),
            theme.secondary_hover,
            theme.secondary,
        );
        let merge = commit.parents.len() > 1;
        let note = |text: String, color: Hsla| {
            div()
                .py(px(3.))
                .pr(px(COLUMN_GAP))
                .text_role(TextRole::Body, cx)
                .text_color(color)
                .child(text)
        };
        let files = self.files.get(&commit.sha);
        let failed = matches!(files, Some(CommitFiles::Failed(_)));
        let mut block = v_flex()
            .w_full()
            .pb_1()
            .pl(px(graph + 8.))
            .bg(surface)
            .border_b_1()
            .border_color(border)
            .when(!commit.body.trim().is_empty(), |block| {
                block.child(
                    div()
                        .pr(px(COLUMN_GAP))
                        .py(px(3.))
                        .child(commit.body.trim().to_string()),
                )
            });
        if let Some(sentence) = files_sentence(files, merge) {
            return block
                .child(note(sentence, if failed { danger } else { muted }))
                .into_any_element();
        }
        let Some(CommitFiles::Read(files)) = files else {
            return block.into_any_element();
        };
        let summary = super::super::inline::files_summary(files.len());
        block = block.child(note(
            if merge {
                format!("{summary} against its first parent")
            } else {
                format!("{summary} in this commit")
            },
            muted,
        ));
        for file in files {
            let (name, folder) = split_path(&file.path);
            let unreadable = unreadable_path(&file.path);
            let chosen = self
                .chosen
                .as_ref()
                .is_some_and(|(sha, path)| sha == &commit.sha && path == &file.path);
            let open = self.commit_diff(
                &commit.sha,
                &file.path,
                format!("{} {}", commit.short_sha, commit.subject),
            );
            let current = self.current_file(&file.path);
            let tip = if unreadable {
                "This file has a name git could not print in plain letters.".to_string()
            } else {
                file.path.clone()
            };
            let (picked_sha, picked_path) = (commit.sha.clone(), file.path.clone());
            let menu_open = open.clone();
            let copied = file.path.clone();
            let id = format!("history-file:{}:{}", commit.sha, file.path);
            let selector = id.clone();
            block = block.child(
                h_flex()
                    .id(SharedString::from(id))
                    .debug_selector(move || selector.clone())
                    .w_full()
                    .gap(px(6.))
                    .py(px(2.))
                    .pl_1()
                    .pr(px(COLUMN_GAP))
                    .rounded(px(4.))
                    .cursor_pointer()
                    .when(chosen, move |row| row.bg(selected))
                    .when(unreadable, |row| row.opacity(0.7))
                    .hover(move |row| row.bg(hover))
                    .tooltip(move |window, cx| Tooltip::new(tip.clone()).build(window, cx))
                    .on_click(cx.listener(move |timeline, _, window, cx| {
                        timeline.chosen = Some((picked_sha.clone(), picked_path.clone()));
                        window.dispatch_action(Box::new(open.clone()), cx);
                        cx.notify();
                    }))
                    .child(div().flex_none().child(name.to_string()))
                    .when(!folder.is_empty(), |row| {
                        row.child(
                            div()
                                .min_w_0()
                                .truncate()
                                .text_role(TextRole::Body, cx)
                                .text_color(muted)
                                .child(folder.to_string()),
                        )
                    })
                    .child(
                        div()
                            .ml_auto()
                            .flex_none()
                            .w(px(12.))
                            .text_center()
                            .text_role(TextRole::Body, cx)
                            .font_weight(FontWeight::MEDIUM)
                            .text_color(super::super::inline::letter_tone(file.letter, cx))
                            .child(file.letter.to_string()),
                    )
                    .context_menu(move |menu, _, _| {
                        commit_file_menu(
                            menu,
                            menu_open.clone(),
                            current.clone(),
                            copied.clone(),
                            !unreadable,
                        )
                    }),
            );
        }
        block.into_any_element()
    }
}
