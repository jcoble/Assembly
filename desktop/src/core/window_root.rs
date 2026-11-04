//! `WindowRoot` owns the window's content view (the kit's `Root` wraps it, for dialogs and sheets), and draws
//! `NativeViews::placer` once over it, so native views are placed once a frame after everything has laid out. On top
//! goes the fade from the old theme after a switch (theme/fade.rs). It puts the theme and faces Settings saved in
//! force from the start, and as they change.

use super::intents::open_file::OpenFile;
use super::intents::reveal_file_folder::RevealFileFolder;
use crate::features::files::file_view::FileView;
use crate::features::files::files_pane::FilesPane;
use gpui_kit::component::Theme;
use gpui_kit::*;

use super::native_views::views::NativeViews;
use super::state::preferences::Preferences;
use super::theme::{self, theme_id::ThemeId};

pub struct WindowRoot {
    content: AnyView,
    /// The theme in force; the window opens in the default.
    theme: ThemeId,
    _preferences: Option<Subscription>,
    /// A theme file sets its own sizes, and the kit's folder watch re-applies one when it changes, so what Settings
    /// saved goes back on after any change to the theme. `apply_text` writes only a difference, so this settles.
    _theme: Subscription,
}

impl WindowRoot {
    pub fn new(content: impl Into<AnyView>, window: &mut Window, cx: &mut Context<Self>) -> Self {
        let _preferences = Preferences::try_global(cx).map(|preferences| {
            cx.observe_in(&preferences, window, |root, _, window, cx| {
                root.follow_preferences(window, cx)
            })
        });
        let _theme = cx.observe_global_in::<Theme>(window, |_, _, cx| theme::apply_text(cx));
        let mut root = Self {
            content: content.into(),
            theme: ThemeId::DEFAULT,
            _preferences,
            _theme,
        };
        // What arrived before the window was made.
        root.follow_preferences(window, cx);
        root
    }

    fn follow_preferences(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let saved = Preferences::read(cx, |appearance| appearance.theme());
        if saved != self.theme {
            self.theme = saved;
            theme::set(saved, window, cx);
        }
        theme::apply_text(cx);
    }
}

impl Render for WindowRoot {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        div()
            .on_action(|action: &OpenFile, window, cx| {
                FileView::open_at(
                    action.session_id.clone(),
                    action.path.clone(),
                    action.line,
                    action.column,
                    action.pinned,
                    action.expected_source.clone(),
                    window,
                    cx,
                )
            })
            .on_action(|action: &RevealFileFolder, window, cx| {
                FilesPane::reveal_folder(
                    &action.session_id,
                    &action.path,
                    action.expected_source.as_ref(),
                    window,
                    cx,
                )
            })
            .size_full()
            .child(self.content.clone())
            .child(NativeViews::placer())
            .children(theme::fade::layer(cx))
    }
}

#[cfg(test)]
#[path = "window_root_tests.rs"]
mod tests;
