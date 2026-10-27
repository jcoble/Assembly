//! Loading the theme files and putting one in force. The kit's own folder watch re-reads a changed file and
//! re-applies it, so nothing here watches or re-reads. Replaces themeService.ts's apply().

use std::{path::PathBuf, time::Duration};

use gpui_kit::base::Spring;
use gpui_kit::component::{Theme, ThemeMode, ThemeRegistry};
use gpui_kit::{App, Window, WindowBackgroundAppearance};

use super::theme_id::ThemeId;

/// The theme files, read in place from the source tree. A packaged app's location comes with packaging.
const THEMES_DIR: &str = concat!(env!("CARGO_MANIFEST_DIR"), "/themes");

/// The design system's slowest motion, which the kit's slow step and move spring are brought down to.
const MOTION_CEILING: Duration = Duration::from_millis(180);

/// Watches the kit's theme folder and calls `on_ready` once the files have loaded (the kit calls it once). The window
/// opens in `on_ready` and calls `set` inside its open call, so the first frame is already the theme in force, never
/// the kit's built-in light one.
pub fn init(cx: &mut App, on_ready: impl Fn(&mut App) + 'static) {
    ThemeRegistry::watch_dir(PathBuf::from(THEMES_DIR), cx, on_ready)
        .expect("watch the theme folder");
}

/// Puts `id` in force: makes its file the kit's active theme, sets the window background to blurred for glass and
/// opaque for the rest, and brings the kit's 280 ms slow step and move spring down to 180 ms.
pub fn set(id: ThemeId, window: &mut Window, cx: &mut App) {
    let config = ThemeRegistry::global(cx)
        .themes()
        .get(id.theme_name())
        .cloned();
    Theme::update(cx, |theme| {
        if let Some(config) = config {
            theme.dark_theme = config;
        }
        theme.motion.duration_slow = MOTION_CEILING;
        // The kit's own spring_move, with the shorter response.
        theme.motion.spring_move = Spring::new(MOTION_CEILING)
            .with_damping(0.85)
            .with_epsilon(0.1);
    });
    // Loads the dark theme just swapped in; one update alone doesn't reload it (theme/mod.rs:288-327).
    Theme::change(ThemeMode::Dark, Some(window), cx);
    window.set_background_appearance(if id.is_glass() {
        WindowBackgroundAppearance::Blurred
    } else {
        WindowBackgroundAppearance::Opaque
    });
}

#[cfg(test)]
mod tests {
    use super::{init, set};
    use crate::core::theme::theme_id::ThemeId;
    use gpui_kit::component::{Theme, ThemeRegistry};
    use gpui_kit::{Context, IntoElement, Render, TestAppContext, Window, div, gpui};
    use std::{cell::Cell, rc::Rc, time::Duration};

    struct Blank;

    impl Render for Blank {
        fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
            div()
        }
    }

    fn load_themes(cx: &mut TestAppContext) -> Rc<Cell<u32>> {
        let ready = Rc::new(Cell::new(0));
        let count = ready.clone();
        cx.update(|cx| {
            gpui_kit::init(cx);
            init(cx, move |_| count.set(count.get() + 1));
        });
        cx.run_until_parked();
        ready
    }

    #[gpui::test]
    fn every_theme_file_is_loaded_before_the_window_opens(cx: &mut TestAppContext) {
        let ready = load_themes(cx);
        assert_eq!(ready.get(), 1);
        cx.update(|cx| {
            for id in ThemeId::ALL {
                assert!(
                    ThemeRegistry::global(cx)
                        .themes()
                        .contains_key(id.theme_name()),
                    "{}",
                    id.theme_name()
                );
            }
        });
    }

    #[gpui::test]
    fn set_puts_the_theme_in_force_and_keeps_motion_within_180_ms(cx: &mut TestAppContext) {
        load_themes(cx);
        let window = cx.add_window(|_, _| Blank);
        window
            .update(cx, |_, window, cx| set(ThemeId::Houston, window, cx))
            .expect("the test window is open");
        cx.update(|cx| {
            let theme = Theme::global(cx);
            assert_eq!(theme.theme_name().as_ref(), "Houston");
            assert!(theme.is_dark());
            assert!(theme.motion.duration_slow <= Duration::from_millis(180));
        });

        // The kit re-applies the theme in force by name whenever its registry changes, as a reload does.
        cx.update(|cx| {
            ThemeRegistry::global_mut(cx);
        });
        cx.run_until_parked();
        cx.update(|cx| assert_eq!(Theme::global(cx).theme_name().as_ref(), "Houston"));
    }
}
