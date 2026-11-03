//! The small pieces the Pull requests tab draws in many places.
use super::pr_activity::{PrTone, age_of, pr_state};
use gpui_kit::assets::IconName;
use gpui_kit::component::avatar::Avatar;
use gpui_kit::component::{ActiveTheme as _, Sizable as _, tooltip::Tooltip};
use gpui_kit::*;

pub fn tone_color(tone: PrTone, cx: &App) -> Hsla {
    let theme = cx.theme();
    match tone {
        PrTone::Good => theme.success,
        PrTone::Merged => theme.magenta,
        PrTone::Bad => theme.danger,
        PrTone::Idle => theme.muted_foreground,
    }
}
pub fn state_icon(state: &str, is_draft: bool) -> IconName {
    match pr_state(state, is_draft).1 {
        PrTone::Merged => IconName::GitMerge,
        PrTone::Bad => IconName::GitPullRequestClosed,
        _ if is_draft => IconName::GitPullRequestDraft,
        _ => IconName::GitPullRequest,
    }
}
/// A person's initials; the old tab's GitHub pictures need an HTTP client the desktop doesn't have.
pub fn avatar(login: &str) -> Option<impl IntoElement + use<>> {
    (!login.is_empty()).then(|| Avatar::new().name(login.to_string()).with_size(px(20.)))
}
pub fn now_millis() -> i64 {
    chrono::Utc::now().timestamp_millis()
}
/// "3h", with the exact stamp on hover.
pub fn age_label(id: SharedString, stamp: &str, suffix: &str) -> Option<Stateful<Div>> {
    let age = age_of(stamp, now_millis())?;
    let exact = chrono::DateTime::parse_from_rfc3339(stamp)
        .ok()?
        .with_timezone(&chrono::Local)
        .format("%b %-d, %Y, %-I:%M %p")
        .to_string();
    Some(
        div()
            .id(id)
            .flex_none()
            .tooltip(move |window, cx| Tooltip::new(exact.clone()).build(window, cx))
            .child(if age == "now" {
                age
            } else {
                format!("{age}{suffix}")
            }),
    )
}
