//! `AppAssets` owns the window's icons: the kit's default set, which its own components draw, and the few the window
//! draws that the kit doesn't embed by default, from the kit's Lucide catalog. Lucide is the old app's icon set
//! (@lucide/svelte), so the icons are the same: the round Home button's house is HomeButton.svelte's.

use std::borrow::Cow;

use gpui_kit::{AssetSource, Result, SharedString};

gpui_kit::assets::icon_assets!(
    Extra,
    [
        House,
        CircleQuestionMark,
        ShieldQuestionMark,
        Dot,
        ArrowUp,
        Square,
        Paperclip,
        X,
        Image,
        CircleAlert
    ]
);

pub struct AppAssets;

impl AssetSource for AppAssets {
    fn load(&self, path: &str) -> Result<Option<Cow<'static, [u8]>>> {
        match Extra.load(path)? {
            Some(svg) => Ok(Some(svg)),
            None => gpui_kit::assets::Assets.load(path),
        }
    }

    fn list(&self, path: &str) -> Result<Vec<SharedString>> {
        let mut listed = gpui_kit::assets::Assets.list(path)?;
        listed.extend(Extra.list(path)?);
        listed.sort();
        listed.dedup();
        Ok(listed)
    }
}
