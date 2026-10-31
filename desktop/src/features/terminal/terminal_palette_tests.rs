//! Which terminal colours each theme has.

use super::palette;
use crate::core::theme::theme_id::ThemeId;

#[test]
fn the_old_apps_themes_keep_their_terminal_colours() {
    let dracula = palette(ThemeId::Dracula).unwrap();
    assert_eq!(dracula.ansi[1], 0xff5555);
    assert_eq!(dracula.ansi[12], 0xcaa9fa);
    assert_eq!(dracula.foreground, 0xf8f8f2);
    assert_eq!(palette(ThemeId::Houston).unwrap().cursor, 0x4bf3c8);
    assert_eq!(palette(ThemeId::TokyoNight).unwrap().ansi[0], 0x15161e);
    assert_eq!(palette(ThemeId::Graphite).unwrap().ansi[3], 0xe8c268);
}

#[test]
fn the_assembly_shell_themes_share_its_colours_and_the_light_ones_have_none() {
    let assembly = palette(ThemeId::Assembly).unwrap();
    assert_eq!(assembly.cursor, 0x1ed760);
    for theme in [ThemeId::AssemblyGlass, ThemeId::Amy, ThemeId::Cobalt] {
        assert!(std::ptr::eq(palette(theme).unwrap(), assembly));
    }
    for theme in [
        ThemeId::AssemblyLight,
        ThemeId::AyuLight,
        ThemeId::RosePineDawn,
    ] {
        assert!(palette(theme).is_none());
    }
}
