//! Tests for the window's icons (docs/behavior/g0-shape.md).

use gpui_kit::AssetSource as _;
use gpui_kit::assets::IconName;

use super::assets::AppAssets;

// Pins: g0-shape#6
#[test]
fn the_window_has_every_icon_it_draws() {
    // The kit's own ChevronRight, and the window's own: the Home button's house, a question's marks, an unfinished step.
    for icon in [
        IconName::ChevronRight,
        IconName::House,
        IconName::CircleQuestionMark,
        IconName::ShieldQuestionMark,
        IconName::Dot,
    ] {
        let path = icon.path();
        let svg = AppAssets.load(&path).expect("loads").expect("is there");
        assert!(svg.starts_with(b"<svg"), "{path} is an SVG");
    }
    let listed = AppAssets.list("icons/").expect("lists");
    assert!(listed.contains(&IconName::House.path()));
    assert!(listed.contains(&IconName::ChevronRight.path()));
}
