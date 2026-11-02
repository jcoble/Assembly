//! Provider card hierarchy, using explicitly illustrative preview facts only.
use super::*;
use gpui_kit::component::Disableable as _;
use gpui_kit::component::collapsible::Collapsible;
use gpui_kit::component::tag::Tag;

struct ProviderExample {
    name: &'static str,
    adapter: &'static str,
    version: &'static str,
    models: &'static str,
    permissions: &'static str,
    separate_cli: bool,
}

const EXAMPLES: [ProviderExample; 3] = [
    ProviderExample {
        name: "Codex",
        separate_cli: true,
        adapter: "codex-acp",
        version: "2.5.0",
        models: "6.1 Sol · 6 Astra · 6 Sol · 6 Luna",
        permissions: "Tool approval uses the provider’s launch settings.",
    },
    ProviderExample {
        name: "Claude",
        separate_cli: true,
        adapter: "claude-agent-acp",
        version: "0.99.1",
        models: "Default · Opus · Sonnet · Haiku",
        permissions: "Tool approval uses the provider’s launch settings.",
    },
    ProviderExample {
        name: "Antigravity",
        separate_cli: false,
        adapter: "agy-acp",
        version: "1.4.5",
        models: "Gemini Flash",
        permissions: "Tool approval uses Assembly’s permission handler. No read-only mode in this example.",
    },
];

impl SettingsPreview {
    pub(super) fn provider_rows(
        &self,
        machine_id: u64,
        connected: bool,
        installed: bool,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        let mut rows = v_flex().gap_2().pt_3();
        for (index, example) in EXAMPLES.iter().enumerate() {
            let key = (machine_id, index);
            let open = self.provider_details.contains(&key);
            let control_id = machine_id * EXAMPLES.len() as u64 + index as u64;
            let mut summary = v_flex().gap_2().child(
                h_flex()
                    .justify_between()
                    .gap_3()
                    .child(
                        Button::new(("provider", control_id))
                            .ghost()
                            .small()
                            .label(format!("{} {}", if open { "⌄" } else { "›" }, example.name))
                            .on_click(cx.listener(move |s, _, _, cx| {
                                if !s.provider_details.remove(&key) {
                                    s.provider_details.insert(key);
                                }
                                cx.notify();
                            })),
                    )
                    .child(Tag::secondary().small().child(if installed {
                        "Not checked"
                    } else {
                        "Setup needed"
                    })),
            );
            if installed {
                summary = summary
                    .child(
                        Label::new(format!(
                            "{} {} · {}",
                            example.adapter,
                            example.version,
                            if connected {
                                "sample version"
                            } else {
                                "last-known sample version"
                            }
                        ))
                        .text_sm()
                        .text_color(cx.theme().muted_foreground),
                    )
                    .child(Label::new(format!("Sample models: {}", example.models)).text_sm())
                    .child(
                        Label::new(example.permissions)
                            .text_sm()
                            .text_color(cx.theme().muted_foreground),
                    );
            } else {
                summary = summary.child(
                    Label::new(
                        "Install and connect this machine to discover its providers and models.",
                    )
                    .text_sm()
                    .text_color(cx.theme().muted_foreground),
                );
            }
            let detail = v_flex().gap_3().pt_3()
                .child(Label::new("Updates for this provider on this machine")
                    .text_sm().text_color(cx.theme().muted_foreground))
                .child(Label::new(format!("Adapter · Installed: {} · Latest: unknown", if installed { example.version } else { "unknown" })).text_sm())
                .child(Label::new(if example.separate_cli { "CLI · Installed: unknown · Latest: not checked" } else { "Runtime updates with the Antigravity adapter." }).text_sm())
                .child(Label::new("Sample details · live checks are not connected in this preview.")
                    .text_sm().text_color(cx.theme().muted_foreground))
                .child(h_flex().gap_2().flex_wrap()
                    .child(Button::new(("signin", control_id)).outline().small().label("Sign in").disabled(true))
                    .child(Button::new(("models", control_id)).outline().small().label("Refresh models").disabled(true)))
                .child(Label::new("Use Update CLI's and Adapters above this connection to update its providers together. Items needing updates and their progress appear here.")
                    .text_sm().text_color(cx.theme().muted_foreground));
            rows = rows.child(
                div()
                    .p_3()
                    .rounded_lg()
                    .bg(cx.theme().muted)
                    .child(Collapsible::new().open(open).child(summary).content(detail)),
            );
        }
        rows.into_any_element()
    }
}
