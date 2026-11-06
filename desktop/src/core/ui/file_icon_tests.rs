use super::{Glyph, Tone, file_look};
use crate::core::assets::AppAssets;
use gpui_kit::AssetSource as _;

// Pins: old fileIcons.ts fileIconForName order.
#[test]
fn a_whole_name_wins_over_its_extension_and_a_leading_dot_is_not_an_extension() {
    assert_eq!(file_look("web/pnpm-lock.yaml").glyph, Glyph::Lock);
    assert_eq!(file_look("Cargo.toml").tone, Tone::Orange);
    assert_eq!(file_look("config/app.toml").tone, Tone::Muted);
    assert_eq!(file_look(".gitignore").glyph, Glyph::Sliders);
    assert_eq!(file_look(".hidden").glyph, Glyph::Page);
    assert_eq!(file_look("a.d.ts").label, "TypeScript");
    assert_eq!(file_look("no-extension").glyph, Glyph::Page);
}
#[test]
fn the_window_can_draw_every_file_glyph() {
    for glyph in Glyph::ALL {
        let path = glyph.icon().path();
        let svg = AppAssets.load(&path).expect("loads").expect("is embedded");
        assert!(svg.starts_with(b"<svg"), "{path} is an SVG");
    }
}
