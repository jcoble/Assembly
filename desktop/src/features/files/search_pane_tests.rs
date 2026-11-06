//! A search result's folder stays inside a narrow pane, so it ends in "…" instead of running past the edge.

use gpui::TestAppContext;
use gpui_kit::{
    AppContext as _, Context, Entity, IntoElement, ParentElement as _, Render, Styled as _, Window,
    div, px,
};

use super::entry::NameMatch;
use super::search_pane::SearchPane;
use crate::core::state::selected_session::SelectedSession;

const WIDTH: f32 = 240.;

/// Draws the pane in a column `WIDTH` wide, as the left column would.
struct Host(Entity<SearchPane>);

impl Render for Host {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div()
            .w(px(WIDTH))
            .h(px(400.))
            .overflow_hidden()
            .child(self.0.clone())
    }
}

#[gpui::test]
fn a_long_folder_ends_inside_the_pane(cx: &mut TestAppContext) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
    });
    let (_host, cx) = cx.add_window_view(|window, cx| {
        let pane = cx.new(|cx| {
            let mut pane = SearchPane::new(window, cx);
            pane.matches = vec![NameMatch {
                path: "a/very/long/folder/path/that/goes/on/and/on/files_pane.rs".into(),
                name: "files_pane.rs".into(),
                is_directory: false,
                relative_path: "a/very/long/folder/path/that/goes/on/and/on/files_pane.rs".into(),
            }];
            pane
        });
        Host(pane)
    });
    cx.update(|window, cx| window.draw(cx).clear(cx));
    let folder = cx
        .debug_bounds("search-folder")
        .expect("the folder is drawn");
    assert!(
        folder.right() <= px(WIDTH),
        "the folder runs to {:?}, past the pane's {WIDTH} px",
        folder.right()
    );
}
