//! Loading the theme files and putting one in force. The kit's own folder watch re-reads a changed file and
//! re-applies it, so nothing here watches or re-reads. Replaces themeService.ts's apply().
//!
//! Where the watch runs (gpui-component 0.8.1 theme/registry.rs:186-230): a `notify` watcher, whose own OS thread
//! (inotify on Linux, FSEvents on macOS) sends "something changed" into a channel, and a GPUI task on the main thread
//! that waits on that channel and re-reads the folder. Both are made once, at `init`, and live for the app's life.

use std::{path::PathBuf, time::Duration};

use gpui_kit::base::Spring;
use gpui_kit::component::{Theme, ThemeMode, ThemeRegistry};
use gpui_kit::{App, Window, WindowBackgroundAppearance};

use super::theme_id::ThemeId;

/// The theme files, read in place from the source tree. A packaged app's location comes with packaging.
pub const THEMES_DIR: &str = concat!(env!("CARGO_MANIFEST_DIR"), "/themes");

/// The design system's slowest motion, which the kit's slow step and move spring are brought down to.
const MOTION_CEILING: Duration = Duration::from_millis(180);

/// Watches the theme folder `dir` and calls `on_ready` once the files have loaded. The kit calls it exactly once,
/// from the one-off load task (registry.rs:105-115); a later file change only reloads (registry.rs:224-227). The
/// window opens in `on_ready` and calls `set` inside its open call, so the first frame is already the theme in force,
/// never the kit's built-in light one. A folder that can't be watched or read still calls `on_ready`: the kit logs it
/// and loads what it can (registry.rs:105-115, 248-266), and `set` then leaves the kit's default in force.
pub fn init(dir: PathBuf, cx: &mut App, on_ready: impl Fn(&mut App) + 'static) {
    // Kit 0.8.1 returns Ok once the load task is scheduled; anything that goes wrong after that is logged by the kit.
    if let Err(error) = ThemeRegistry::watch_dir(dir, cx, on_ready) {
        eprintln!(
            "theme: the theme folder isn't watched, so the window opens in the kit's default: {error}"
        );
    }
}

/// Puts `id` in force: makes its file the kit's active theme, sets the window background to blurred for glass and
/// opaque for the rest, and brings the kit's 280 ms slow step and move spring down to 180 ms. Off macOS there's no
/// blur, so glass gives Assembly. A theme whose file didn't load leaves the theme in force as it is. Every theme file
/// is dark, so this fills `dark_theme` and asks for dark mode; a light file would need `light_theme` and
/// `ThemeMode::Light`.
pub fn set(id: ThemeId, window: &mut Window, cx: &mut App) {
    let id = if id.is_glass() && !cfg!(target_os = "macos") {
        ThemeId::Assembly
    } else {
        id
    };
    let Some(config) = ThemeRegistry::global(cx)
        .themes()
        .get(id.theme_name())
        .cloned()
    else {
        return;
    };
    Theme::update(cx, |theme| {
        theme.dark_theme = config;
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
    use super::{THEMES_DIR, init, set};
    use crate::core::theme::theme_id::ThemeId;
    use gpui_kit::component::{Theme, ThemeMode, ThemeRegistry};
    use gpui_kit::{TestAppContext, gpui};
    use std::{cell::Cell, fs, path::PathBuf, rc::Rc, time::Duration};

    fn load_themes_from(dir: PathBuf, cx: &mut TestAppContext) -> Rc<Cell<u32>> {
        let ready = Rc::new(Cell::new(0));
        let count = ready.clone();
        cx.update(|cx| {
            gpui_kit::init(cx);
            init(dir, cx, move |_| count.set(count.get() + 1));
        });
        cx.run_until_parked();
        ready
    }

    fn load_themes(cx: &mut TestAppContext) -> Rc<Cell<u32>> {
        let ready = Rc::new(Cell::new(0));
        let count = ready.clone();
        cx.update(|cx| {
            gpui_kit::init(cx);
            init(PathBuf::from(THEMES_DIR), cx, move |cx| {
                // Every file is in the registry by the time the window would open here.
                for id in ThemeId::ALL {
                    let name = id.theme_name();
                    assert!(
                        ThemeRegistry::global(cx).themes().contains_key(name),
                        "{name}"
                    );
                }
                count.set(count.get() + 1);
            });
        });
        cx.run_until_parked();
        ready
    }

    /// A copy of the theme folder of the test's own, so a test can change or break files.
    fn copy_of_themes(name: &str) -> PathBuf {
        let dir =
            std::env::temp_dir().join(format!("assembly-themes-{}-{name}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).expect("make the test's theme folder");
        for entry in fs::read_dir(THEMES_DIR).expect("read desktop/themes") {
            let path = entry.expect("a folder entry").path();
            if path.extension().is_some_and(|ext| ext == "json") {
                fs::copy(&path, dir.join(path.file_name().expect("a file name")))
                    .expect("copy a theme file");
            }
        }
        dir
    }

    fn set_in_new_window(id: ThemeId, cx: &mut TestAppContext) {
        cx.add_empty_window()
            .update(|window, cx| set(id, window, cx));
    }

    fn assert_motion_within_180_ms(cx: &mut TestAppContext) {
        cx.update(|cx| {
            // Spring has no public response to read, so the slow step stands for both.
            assert!(Theme::global(cx).motion.duration_slow <= Duration::from_millis(180));
        });
    }

    #[gpui::test]
    fn the_window_opens_once_every_theme_file_has_loaded(cx: &mut TestAppContext) {
        assert_eq!(load_themes(cx).get(), 1);
    }

    #[gpui::test]
    fn set_switches_between_themes_and_keeps_motion_within_180_ms(cx: &mut TestAppContext) {
        load_themes(cx);
        for id in [ThemeId::Houston, ThemeId::Dracula] {
            set_in_new_window(id, cx);
            cx.update(|cx| {
                let theme = Theme::global(cx);
                assert_eq!(theme.theme_name().as_ref(), id.theme_name());
                assert!(theme.is_dark());
            });
        }
        // A later change of mode, as the kit's registry observer makes on a reload, keeps the motion.
        cx.update(|cx| Theme::change(ThemeMode::Dark, None, cx));
        assert_motion_within_180_ms(cx);
    }

    #[gpui::test]
    fn glass_gives_assembly_off_macos(cx: &mut TestAppContext) {
        load_themes(cx);
        set_in_new_window(ThemeId::AssemblyGlass, cx);
        let expected = if cfg!(target_os = "macos") {
            ThemeId::AssemblyGlass
        } else {
            ThemeId::Assembly
        };
        cx.update(|cx| {
            assert_eq!(
                Theme::global(cx).theme_name().as_ref(),
                expected.theme_name()
            )
        });
    }

    #[gpui::test]
    fn a_reload_re_applies_the_theme_in_force_without_opening_another_window(
        cx: &mut TestAppContext,
    ) {
        let ready = load_themes_from(copy_of_themes("watched"), cx);
        set_in_new_window(ThemeId::Houston, cx);
        let before = cx.update(|cx| Theme::global(cx).radius);

        // The kit's watcher reports a change from its own OS thread, which the test scheduler can't take, so the
        // changed file goes in a second, unwatched copy and the kit is pointed at it: that runs the same reload
        // (registry.rs:96-117, 247-292) and the same re-apply of the theme in force (registry.rs:46-70).
        let changed = copy_of_themes("changed");
        let file = changed.join("houston.json");
        let text = fs::read_to_string(&file).expect("read houston.json");
        assert!(text.contains("\"radius\": 6,"));
        fs::write(&file, text.replace("\"radius\": 6,", "\"radius\": 3,"))
            .expect("save houston.json");
        cx.update(|cx| {
            ThemeRegistry::watch_dir(changed, cx, |_| {}).expect("reload the changed copy")
        });
        cx.run_until_parked();

        cx.update(|cx| {
            let theme = Theme::global(cx);
            assert_eq!(theme.theme_name().as_ref(), "Houston");
            assert_ne!(theme.radius, before, "the changed file wasn't re-applied");
        });
        assert_eq!(ready.get(), 1, "a reload called on_ready again");
        assert_motion_within_180_ms(cx);
    }

    #[gpui::test]
    fn a_broken_theme_file_still_opens_the_window_in_its_theme(cx: &mut TestAppContext) {
        let dir = copy_of_themes("broken");
        fs::write(dir.join("broken.json"), "{ not json").expect("write broken.json");
        let ready = load_themes_from(dir, cx);
        assert_eq!(ready.get(), 1);
        set_in_new_window(ThemeId::Assembly, cx);
        cx.update(|cx| assert_eq!(Theme::global(cx).theme_name().as_ref(), "Assembly"));
    }

    #[gpui::test]
    fn a_theme_folder_that_cant_be_made_opens_in_the_kit_default(cx: &mut TestAppContext) {
        // A folder under a file can't be created, so the kit finds no theme files.
        let ready = load_themes_from(PathBuf::from("/dev/null/themes"), cx);
        assert_eq!(ready.get(), 1);
        let before = cx.update(|cx| Theme::global(cx).theme_name().clone());
        set_in_new_window(ThemeId::Assembly, cx);
        cx.update(|cx| assert_eq!(Theme::global(cx).theme_name(), &before));
    }
}
