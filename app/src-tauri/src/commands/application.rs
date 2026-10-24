use crate::backend_capabilities;

#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct InstalledFontFamilies {
    families: Vec<String>,
    monospaced_families: Vec<String>,
}

#[tauri::command]
pub(crate) async fn read_backend_capabilities() -> Result<Vec<String>, String> {
    Ok(backend_capabilities())
}

#[tauri::command]
pub(crate) async fn list_installed_font_families() -> InstalledFontFamilies {
    installed_font_families()
}

#[cfg(target_os = "macos")]
fn installed_font_families() -> InstalledFontFamilies {
    use objc2_core_foundation::{CFArray, CFRetained, CFString};
    use objc2_core_text::{
        CTFont, CTFontManagerCopyAvailableFontFamilyNames, CTFontSymbolicTraits,
    };
    use std::ptr;

    // CoreText returns a retained, UI-sorted array containing CFString values.
    let untyped = unsafe { CTFontManagerCopyAvailableFontFamilyNames() };
    let names = unsafe { CFRetained::cast_unchecked::<CFArray<CFString>>(untyped) };
    let mut families = Vec::with_capacity(names.len());
    let mut monospaced_families = Vec::new();

    for name in names.iter() {
        let family = name.to_string();
        let font = unsafe { CTFont::with_name(&name, 0.0, ptr::null()) };
        if unsafe { font.family_name() }.to_string() != family {
            continue;
        }
        if unsafe { font.symbolic_traits() }.contains(CTFontSymbolicTraits::TraitMonoSpace) {
            monospaced_families.push(family.clone());
        }
        families.push(family);
    }

    InstalledFontFamilies {
        families,
        monospaced_families,
    }
}

#[cfg(not(target_os = "macos"))]
fn installed_font_families() -> InstalledFontFamilies {
    InstalledFontFamilies {
        families: Vec::new(),
        monospaced_families: Vec::new(),
    }
}

#[cfg(all(test, target_os = "macos"))]
mod tests {
    use super::installed_font_families;

    #[test]
    fn installed_font_families_classify_menlo_as_monospaced() {
        let fonts = installed_font_families();
        assert!(fonts.families.iter().any(|family| family == "Menlo"));
        assert!(fonts
            .monospaced_families
            .iter()
            .any(|family| family == "Menlo"));
        assert!(fonts
            .monospaced_families
            .iter()
            .all(|family| fonts.families.contains(family)));
    }
}
