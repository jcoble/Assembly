//! The small pieces the Pull requests tab draws in many places.
use super::pr_activity::{PrTone, age_of, pr_state};
use super::pull_request::{PullRequestDetail, PullRequestSummary};
use crate::core::socket::picture_client::picture_url;
use gpui_kit::assets::IconName;
use gpui_kit::component::avatar::Avatar;
use crate::core::ui::spinner::Spinner;
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex, tooltip::Tooltip, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
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
/// A person's GitHub picture, with their initials while it loads or if it can't be read.
pub fn avatar(login: &str) -> Option<impl IntoElement + use<>> {
    let picture = picture_url(login)?;
    let name = login.to_owned();
    let initials = move || {
        Avatar::new()
            .name(name.clone())
            .with_size(px(20.))
            .into_any_element()
    };
    Some(
        img(picture)
            .id(SharedString::from(format!("avatar:{login}")))
            .flex_none()
            .size(px(20.))
            .rounded_full()
            .with_loading(initials.clone())
            .with_fallback(initials),
    )
}
/// What a pull request is, top of its page: from the row until its read arrives, so opening one shows it at once.
pub struct PrHeading<'a> {
    pub repository: &'a str,
    pub number: u64,
    pub state: &'a str,
    pub is_draft: bool,
    pub title: &'a str,
    pub author: &'a str,
    pub created_at: &'a str,
    pub head: &'a str,
    pub base: &'a str,
}
impl<'a> PrHeading<'a> {
    pub fn of_detail(detail: &'a PullRequestDetail) -> Self {
        Self {
            repository: &detail.repository,
            number: detail.number,
            state: &detail.state,
            is_draft: detail.is_draft,
            title: &detail.title,
            author: &detail.author,
            created_at: &detail.created_at,
            head: &detail.head_branch,
            base: &detail.base_branch,
        }
    }
    pub fn of_row(row: &'a PullRequestSummary) -> Self {
        Self {
            repository: &row.repository,
            number: row.number,
            state: &row.state,
            is_draft: row.is_draft,
            title: &row.title,
            author: &row.author,
            created_at: "",
            head: &row.head_branch,
            base: &row.base_branch,
        }
    }
    pub fn view(&self, cx: &App) -> Div {
        let muted = cx.theme().muted_foreground;
        let (state, tone) = pr_state(self.state, self.is_draft);
        let color = tone_color(tone, cx);
        let title = if self.title.is_empty() {
            format!("#{}", self.number)
        } else {
            self.title.to_string()
        };
        let pill = (!self.state.is_empty()).then(|| {
            h_flex()
                .gap_1()
                .px_2()
                .rounded_full()
                .border_1()
                .border_color(color)
                .text_color(color)
                .child(Icon::new(state_icon(self.state, self.is_draft)).size(px(13.)))
                .child(state)
        });
        let author = (!self.author.is_empty())
            .then(|| div().font_weight(FontWeight::SEMIBOLD).child(self.author.to_string()));
        v_flex()
            .gap_2()
            .child(
                h_flex().gap_2().text_sm().children(pill).child(
                    div()
                        .text_color(muted)
                        .child(format!("{} #{}", self.repository, self.number)),
                ),
            )
            .child(div().text_lg().font_weight(FontWeight::SEMIBOLD).child(title))
            .child(
                h_flex()
                    .gap_1()
                    .text_sm()
                    .text_color(muted)
                    .children(avatar(self.author))
                    .children(author)
                    .children(age_label("pr-opened-age".into(), self.created_at, " ago"))
                    .when(!self.head.is_empty(), |line| {
                        line.child("·").child(branches(self.head, self.base, cx))
                    }),
            )
    }
}
fn branches(head: &str, base: &str, cx: &App) -> impl IntoElement + use<> {
    let muted = cx.theme().muted_foreground;
    h_flex()
        .gap_1()
        .min_w_0()
        .child(div().truncate().child(head.to_string()))
        .child(Icon::new(IconName::ArrowRight).size(px(11.)).text_color(muted))
        .child(div().truncate().child(base.to_string()))
}
pub fn loading_line(cx: &App) -> Div {
    h_flex()
        .gap_2()
        .text_sm()
        .text_color(cx.theme().muted_foreground)
        .child(Spinner::new().small())
        .child("Loading pull request…")
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
