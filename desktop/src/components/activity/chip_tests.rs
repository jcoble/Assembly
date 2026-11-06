use gpui::TestAppContext;
use gpui_kit::SharedString;
use gpui_kit::component::ActiveTheme as _;

use super::Tone;
use crate::inline_field::ChoiceOption;

#[gpui::test]
fn tone_maps_to_the_theme_token(cx: &mut TestAppContext) {
    cx.update(gpui_kit::init);
    cx.update(|cx| {
        let theme = cx.theme().clone();
        assert_eq!(Tone::Neutral.color(cx), theme.muted_foreground);
        assert_eq!(Tone::Info.color(cx), theme.info);
        assert_eq!(Tone::Success.color(cx), theme.success);
        assert_eq!(Tone::Warning.color(cx), theme.warning);
        assert_eq!(Tone::Danger.color(cx), theme.danger);
        assert_eq!(Tone::Accent.color(cx), theme.caret);
    });
}

#[test]
fn unknown_choice_value_shows_itself_with_a_neutral_tone() {
    let options = [ChoiceOption::new("done", "Done").tone(Tone::Success)];
    let shown = |value| ChoiceOption::shown(&options, value);
    assert_eq!(
        shown(Some("done")),
        (SharedString::from("Done"), Tone::Success)
    );
    assert_eq!(
        shown(Some("Renamed")),
        (SharedString::from("Renamed"), Tone::Neutral)
    );
    assert_eq!(shown(None), (SharedString::from("Empty"), Tone::Neutral));
}
