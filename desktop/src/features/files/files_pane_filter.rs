//! The filter field under the Files buttons, and its results in place of the tree.
use super::FilesPane;
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::Control as _;
use crate::core::ui::access::control_ids::files as ids;
use crate::core::ui::file_icon::file_glyph;
use crate::core::ui::spinner::Spinner;
use crate::features::files::file_filter::FilterTarget;
use crate::features::files::file_view::FileView;
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::{Input, InputEvent, InputState};
use gpui_kit::component::menu::ContextMenuExt as _;
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::*;

impl FilesPane {
    /// The folder the filter searches: the tree's, as it stands.
    pub(super) fn sync_filter(&self, cx: &mut Context<Self>) {
        let target = self.session.clone().map(|session| FilterTarget {
            session,
            source: self.source.clone(),
            owner: self.owner.clone(),
            include_excluded: self.include_excluded,
        });
        self.filter
            .update(cx, |filter, cx| filter.set_target(target, cx));
    }

    /// Another folder starts with an empty filter.
    pub(in crate::features::files) fn clear_filter(&mut self, cx: &mut Context<Self>) {
        self.filter.update(cx, |filter, cx| filter.clear(cx));
        self.clear_filter_input = true;
    }

    /// A result clicked: the filter clears, and the tree shows it selected. A file opens as a preview; a folder opens.
    pub(in crate::features::files) fn open_result(
        &mut self,
        path: String,
        folder: bool,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.clear_filter(cx);
        self.revealing = Some((path.clone(), folder));
        self.continue_reveal(cx);
        if let (false, Some(session)) = (folder, self.session.clone()) {
            FileView::open_at(
                session,
                path,
                (None, None),
                false,
                self.source.clone(),
                window,
                cx,
            );
        }
    }

    /// The field, made once there's a window to make it in.
    fn filter_input(&mut self, window: &mut Window, cx: &mut Context<Self>) -> Entity<InputState> {
        if let Some((input, _)) = &self.filter_input {
            if std::mem::take(&mut self.clear_filter_input) {
                input.update(cx, |input, cx| input.set_value("", window, cx));
            }
            return input.clone();
        }
        self.clear_filter_input = false;
        let input = cx.new(|cx| InputState::new(window, cx).placeholder("Filter files"));
        let typed = cx.subscribe(&input, |pane, input, event: &InputEvent, cx| {
            if let InputEvent::Change = event {
                let query = input.read(cx).value().to_string();
                pane.filter
                    .update(cx, |filter, cx| filter.set_query(&query, cx));
            }
        });
        self.filter_input = Some((input.clone(), typed));
        input
    }

    pub(super) fn filter_field(&mut self, window: &mut Window, cx: &mut Context<Self>) -> Div {
        let input = self.filter_input(window, cx);
        let muted = cx.theme().muted_foreground;
        div().flex_none().px_2().pb_1().child(
            Input::new(&input)
                .control(ids::FILTER, "Filter files")
                .small()
                .cleanable(true)
                .prefix(Icon::new(IconName::Search).size(px(14.)).text_color(muted)),
        )
    }

    /// The results in place of the tree, or what the filter has to say instead.
    pub(super) fn filter_results(&self, cx: &mut Context<Self>) -> AnyElement {
        let filter = self.filter.read(cx);
        let theme = cx.theme();
        let (muted, danger) = (theme.muted_foreground, theme.danger);
        let line = |text: &'static str| {
            h_flex()
                .px_3()
                .py_1()
                .gap_2()
                .text_role(TextRole::Body, cx)
                .text_color(muted)
                .child(text)
        };
        if !filter.available() {
            return line("The file search is not available here.").into_any_element();
        }
        if let Some(error) = filter.error() {
            return div()
                .px_3()
                .py_1()
                .text_role(TextRole::Body, cx)
                .text_color(danger)
                .child(error.to_string())
                .into_any_element();
        }
        if filter.matches().is_empty() {
            return if filter.searching() {
                line("Searching files…")
                    .child(Spinner::new().xsmall().color(muted))
                    .into_any_element()
            } else {
                line("Nothing matches that search.").into_any_element()
            };
        }
        let more = filter.has_more().then(|| {
            Button::new("files-filter-more")
                .ghost()
                .compact()
                .label(if filter.searching() {
                    "Loading…"
                } else {
                    "Load more"
                })
                .on_click(cx.listener(|pane, _, _, cx| {
                    pane.filter.update(cx, |filter, cx| filter.more(cx))
                }))
        });
        v_flex()
            .flex_1()
            .min_h_0()
            .child(
                uniform_list(
                    "files-filter",
                    filter.matches().len(),
                    cx.processor(|pane, range: std::ops::Range<usize>, _window, cx| {
                        range.map(|ix| pane.result_row(ix, cx)).collect()
                    }),
                )
                .track_scroll(&self.filter_list)
                .flex_1()
                .px_1(),
            )
            .children(more.map(|more| h_flex().px_2().py_1().child(more)))
            .into_any_element()
    }

    fn result_row(&self, ix: usize, cx: &mut Context<Self>) -> AnyElement {
        let Some(found) = self.filter.read(cx).matches().get(ix).cloned() else {
            return div().into_any_element();
        };
        let theme = cx.theme();
        let (muted, accent) = (theme.muted_foreground, theme.accent);
        let folder = found.is_directory;
        let glyph = if folder {
            Icon::new(IconName::Folder).size(px(14.)).text_color(muted)
        } else {
            file_glyph(&found.path, cx)
        };
        let (path, menu_path, tip) = (
            found.path.clone(),
            found.path.clone(),
            found.relative_path.clone(),
        );
        let (session, source) = (self.session.clone(), self.source.clone());
        let (menu_session, menu_source) = (self.session.clone(), self.source.clone());
        let pane = cx.entity().downgrade();
        h_flex()
            .id(("files-filter-row", ix))
            .h(super::TREE_LINE)
            .px_2()
            .gap_1()
            .rounded(px(6.))
            .cursor_pointer()
            .text_role(TextRole::Title, cx)
            .whitespace_nowrap()
            .overflow_hidden()
            .hover(|row| row.bg(accent))
            .child(glyph)
            .child(
                div()
                    .min_w_0()
                    .overflow_hidden()
                    .text_ellipsis()
                    .child(found.name.clone()),
            )
            .tooltip(move |window, cx| Tooltip::new(tip.clone()).build(window, cx))
            .on_click(cx.listener(move |pane, _, window, cx| {
                if pane.session == session && pane.source == source {
                    pane.open_result(path.clone(), folder, window, cx);
                }
            }))
            .context_menu(move |menu, _, cx| match &menu_session {
                Some(session) => Self::file_menu(
                    menu,
                    pane.clone(),
                    session.clone(),
                    menu_path.clone(),
                    folder,
                    menu_source.clone(),
                    cx,
                ),
                None => menu,
            })
            .into_any_element()
    }
}
