//! Operational sections show their real integration boundary; no fabricated version, sign-in or update result.
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use gpui_kit::component::button::Button;
use gpui_kit::component::label::Label;
use gpui_kit::component::setting::{SettingGroup, SettingItem};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Sizable as _, h_flex, v_flex};
use gpui_kit::*;

pub(super) fn bodies(section: &str) -> Vec<SettingGroup> {
    match section {
        "updates" => vec![operation(
            "Assembly desktop",
            "Application releases",
            "Check for a signed desktop release, then download, verify and restart this app. Remote components are managed in Connections.",
            &["Check for updates", "Install and restart"],
        )],
        "plugins" => vec![
            message(
                "API endpoints",
                "No API endpoints configured",
                "Endpoint configuration is provided by the backend; no credentials are stored in this preview.",
            ),
            message(
                "Plugins",
                "No plugins installed",
                "Installed extensions will appear here when the plugin service is connected.",
            ),
        ],
        "helper" => vec![
            operation(
                "Connection",
                "Model",
                "Models must come from the selected service. Connect the backend to load its model list.",
                &["Load models"],
            ),
            operation(
                "Connection",
                "API key",
                "API credentials belong in the system credential store. This preview does not collect or save keys.",
                &["Set API key", "Remove API key"],
            ),
            operation(
                "Connection",
                "Test connection",
                "Run one small request with the selected utility model.",
                &["Test connection"],
            ),
        ],
        _ => vec![],
    }
}
fn message(group: &'static str, title: &'static str, description: &'static str) -> SettingGroup {
    SettingGroup::new()
        .title(group)
        .w_full()
        .max_w(px(720.))
        .item(
            SettingItem::render(move |_, _, cx| {
                v_flex().gap_2().child(Label::new(title)).child(
                    Label::new(description)
                        .text_role(TextRole::Body, cx)
                        .text_color(cx.theme().muted_foreground),
                )
            })
            .keywords([group, title, description]),
        )
}
fn operation(
    group: &'static str,
    title: &'static str,
    description: &'static str,
    actions: &'static [&'static str],
) -> SettingGroup {
    SettingGroup::new()
        .title(group)
        .w_full()
        .max_w(px(720.))
        .item(
            SettingItem::render(move |_, _, cx| {
                v_flex()
                    .gap_2()
                    .child(Label::new(title))
                    .child(
                        Label::new(description)
                            .text_role(TextRole::Body, cx)
                            .text_color(cx.theme().muted_foreground),
                    )
                    .child(
                        h_flex()
                            .gap_2()
                            .flex_wrap()
                            .children(actions.iter().enumerate().map(|(index, label)| {
                                Button::new((title, index))
                                    .outline()
                                    .small()
                                    .label(*label)
                                    .disabled(true)
                            })),
                    )
                    .child(
                        Label::new("Requires a backend connection · unavailable in this preview")
                            .text_role(TextRole::Body, cx)
                            .text_color(cx.theme().muted_foreground),
                    )
            })
            .keywords([group, title, description]),
        )
}
