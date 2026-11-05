//! The development control for updating machines: a palette command that opens one dialog. Its top shows what the update
//! check found behind and has the one button that updates all of it; `npm run deploy:remote` runs the same thing. Below it
//! is putting a local backend bundle on the owned test machine. The backend owns the check, the update, the install and
//! every check on them; this folder only asks for them and shows how they went. It's built to come out whole: this folder,
//! its line in `features/mod.rs` and its calls in `main.rs`. A packaged app doesn't list it. The real product install and
//! connect flow is separate work.

pub mod deploy_form;
mod deploy_view;
pub mod job;
pub mod updates;

use deploy_form::DeployForm;
use gpui_kit::component::WindowExt as _;
use gpui_kit::*;

use crate::core::commands::command_registry::CommandRegistry;

gpui_kit::actions!(remote_install, [OpenRemoteInstall]);

pub const TITLE: &str = "Deploy remote backend (development)";

/// Lists the command in the palette, and opens the dialog when it's picked.
pub fn init(cx: &mut App) {
    CommandRegistry::add(TITLE, Box::new(OpenRemoteInstall), cx);
    cx.on_action(|_: &OpenRemoteInstall, cx| {
        if let Some(window) = cx
            .active_window()
            .or_else(|| cx.windows().into_iter().next())
        {
            cx.defer(move |cx| {
                _ = window.update(cx, |_, window, cx| open(window, cx));
            });
        }
    });
}

fn open(window: &mut Window, cx: &mut App) {
    let form = cx.new(|cx| DeployForm::new(window, cx));
    window.open_dialog(cx, move |dialog, _, _| {
        dialog
            .title(TITLE)
            .w(px(600.))
            .footer(Empty)
            .child(form.clone())
    });
}
