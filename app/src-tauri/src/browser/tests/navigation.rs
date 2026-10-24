//! Regression tests for the built-in browser's navigation rules, page-load handling,
//! external open, tab visibility and its one shared profile. A recording view stands in
//! for the native WebView; page loads arrive through the same callbacks the native view
//! calls. This file exists because `browser/mod.rs` is at its size baseline.

use super::super::*;
use super::{input, target};
use std::sync::atomic::{AtomicUsize, Ordering};

#[derive(Default)]
struct RecordingView {
    navigations: Mutex<Vec<(String, u64)>>,
    shows: AtomicUsize,
    hides: AtomicUsize,
    closes: AtomicUsize,
}

impl BrowserView for RecordingView {
    fn set_bounds(&self, _bounds: BrowserBounds) -> Result<(), BrowserCommandError> {
        Ok(())
    }
    fn show(&self) -> Result<(), BrowserCommandError> {
        self.shows.fetch_add(1, Ordering::SeqCst);
        Ok(())
    }
    fn hide(&self) -> Result<(), BrowserCommandError> {
        self.hides.fetch_add(1, Ordering::SeqCst);
        Ok(())
    }
    fn close(&self) -> Result<(), BrowserCommandError> {
        self.closes.fetch_add(1, Ordering::SeqCst);
        Ok(())
    }
    fn navigate(&self, url: &str, generation: u64) -> Result<(), BrowserCommandError> {
        self.navigations.lock().unwrap().push((url.to_string(), generation));
        Ok(())
    }
    fn reload(&self) -> Result<(), BrowserCommandError> {
        Ok(())
    }
    fn go_back(&self) -> Result<(), BrowserCommandError> {
        Ok(())
    }
    fn go_forward(&self) -> Result<(), BrowserCommandError> {
        Ok(())
    }
    fn eval(&self, _script: &str) -> Result<(), BrowserCommandError> {
        Ok(())
    }
    fn eval_with_callback(
        &self,
        _script: &str,
        _callback: Box<dyn Fn(String) + Send + 'static>,
    ) -> Result<(), BrowserCommandError> {
        Ok(())
    }
    fn open_devtools(&self) -> Result<(), BrowserCommandError> {
        Ok(())
    }
}

/// Keeps every view it makes, the callbacks it was handed, and the profile it was built with.
#[derive(Default)]
struct RecordingFactory {
    made: Mutex<Vec<(Arc<RecordingView>, BrowserViewCallbacks, BrowserProfile)>>,
}

impl BrowserViewFactory for RecordingFactory {
    fn create(
        &self,
        _app: Option<&tauri::AppHandle>,
        _input: &BrowserTabInput,
        profile: &BrowserProfile,
        callbacks: BrowserViewCallbacks,
    ) -> Result<Arc<dyn BrowserView>, BrowserCommandError> {
        let view = Arc::new(RecordingView::default());
        self.made.lock().unwrap().push((view.clone(), callbacks, profile.clone()));
        Ok(view)
    }
}

impl RecordingFactory {
    fn view(&self, index: usize) -> Arc<RecordingView> {
        self.made.lock().unwrap()[index].0.clone()
    }
    /// What the native view reports when a page starts or finishes loading.
    fn page_load(&self, index: usize, phase: BrowserLoadPhase, url: &str, generation: u64) {
        let on_page_load = self.made.lock().unwrap()[index].1.on_page_load.clone();
        on_page_load(NativePageLoad { phase, url: url.to_string(), generation });
    }
    fn title_changed(&self, index: usize, generation: u64, title: &str) {
        let on_title_changed = self.made.lock().unwrap()[index].1.on_title_changed.clone();
        on_title_changed(generation, title.to_string());
    }
}

#[derive(Default)]
struct RecordingSink {
    navigations: Mutex<Vec<BrowserTabNavigationEvent>>,
    loads: Mutex<Vec<BrowserTabLoadEvent>>,
}

impl BrowserEventSink for RecordingSink {
    fn navigation(&self, event: BrowserTabNavigationEvent) {
        self.navigations.lock().unwrap().push(event);
    }
    fn load(&self, event: BrowserTabLoadEvent) {
        self.loads.lock().unwrap().push(event);
    }
    fn element_selected(&self, _event: BrowserElementSelectedEvent) {}
    fn closed(&self, _event: BrowserTabClosedEvent) {}
}

fn browser_with_tab(url: &str) -> (BrowserRegistry, Arc<RecordingFactory>, Arc<RecordingSink>) {
    let factory = Arc::new(RecordingFactory::default());
    let registry = BrowserRegistry::with_factory(factory.clone());
    let sink = Arc::new(RecordingSink::default());
    registry.create_tab(None, input("workspace", "tab", 1, url), sink.clone()).unwrap();
    (registry, factory, sink)
}

fn navigation(generation: u64, url: &str) -> BrowserNavigationInput {
    BrowserNavigationInput {
        workspace_id: "workspace".to_string(),
        tab_id: "tab".to_string(),
        generation,
        url: url.to_string(),
    }
}

/// `(url, title, can_go_back, can_go_forward, generation)` of the registry's tab.
fn tab_state(registry: &BrowserRegistry) -> (String, String, bool, bool, u64) {
    let workspaces = registry.inner.workspaces.lock().unwrap();
    let tab = &workspaces["workspace"].tabs["tab"];
    (tab.url.clone(), tab.title.clone(), tab.can_go_back, tab.can_go_forward, tab.generation)
}

// Pins the URL boundary of 19c2cede3 for the address bar: a navigation must name an http,
// https or file page; blank, about:blank, scripts and data are refused before the view
// is asked to load anything, and the tab keeps its page.
#[test]
fn navigation_refuses_blank_and_non_web_addresses_without_touching_the_page() {
    let (registry, factory, _) = browser_with_tab("https://example.test/");
    for (generation, url) in [(2, ""), (3, "about:blank"), (4, "javascript:alert(1)"), (5, "data:text/html,hi"),
        (6, "ftp://example.test/file")]
    {
        let error = registry.navigate(navigation(generation, url)).unwrap_err();
        assert_eq!(error.code, BrowserErrorCode::InvalidUrl, "{url:?}");
    }
    assert!(factory.view(0).navigations.lock().unwrap().is_empty());
    assert_eq!(tab_state(&registry).0, "https://example.test/");
    assert_eq!(tab_state(&registry).4, 1, "a refused navigation does not spend a generation");
}

// A navigation loads the normalized address under its new generation and resets the tab's
// history buttons and an armed element picker, so a stale pick cannot land on the new page.
#[test]
fn navigation_loads_the_page_under_its_new_generation_and_clears_the_picker() {
    let (registry, factory, _) = browser_with_tab("https://example.test/");
    registry.inner.workspaces.lock().unwrap().get_mut("workspace").unwrap().tabs.get_mut("tab").unwrap().picker =
        Some(PickerState { generation: 1, epoch: 7 });

    registry.navigate(navigation(2, "  https://docs.example.test/guide  ")).unwrap();

    assert_eq!(*factory.view(0).navigations.lock().unwrap(), [("https://docs.example.test/guide".to_string(), 2)]);
    assert_eq!(
        tab_state(&registry),
        ("https://docs.example.test/guide".to_string(), "docs.example.test".to_string(), true, false, 2)
    );
    assert!(registry.inner.workspaces.lock().unwrap()["workspace"].tabs["tab"].picker.is_none());
    // The workspace generation moved with it, so a new tab cannot reuse generation 2.
    let error = registry
        .create_tab(None, input("workspace", "second", 2, "https://example.test/"), Arc::new(RecordingSink::default()))
        .unwrap_err();
    assert_eq!(error.code, BrowserErrorCode::GenerationNotMonotonic);
}

// When the page itself moves (a link, a redirect), the finished load updates the tab's address
// and title and tells the shell; a started load only reports progress; a load the address rules
// would refuse, or one from a page the tab has since left, is dropped.
#[test]
fn page_loads_update_the_tab_only_for_safe_addresses_of_the_current_generation() {
    let (registry, factory, sink) = browser_with_tab("https://example.test/");

    factory.page_load(0, BrowserLoadPhase::Started, "https://other.test/next", 1);
    assert_eq!(tab_state(&registry).0, "https://example.test/", "a started load does not move the tab");
    assert!(sink.navigations.lock().unwrap().is_empty());
    assert_eq!(sink.loads.lock().unwrap().len(), 1);

    factory.page_load(0, BrowserLoadPhase::Finished, "https://other.test/next", 1);
    assert_eq!(tab_state(&registry).0, "https://other.test/next");
    assert_eq!(tab_state(&registry).1, "other.test");
    let navigations = sink.navigations.lock().unwrap().clone();
    assert_eq!(navigations.len(), 1);
    assert_eq!((navigations[0].url.as_str(), navigations[0].title.as_str()), ("https://other.test/next", "other.test"));

    factory.page_load(0, BrowserLoadPhase::Finished, "javascript:alert(1)", 1);
    factory.page_load(0, BrowserLoadPhase::Finished, "https://late.test/", 0);
    assert_eq!(tab_state(&registry).0, "https://other.test/next");
    assert_eq!(sink.loads.lock().unwrap().len(), 2, "unsafe and stale loads are not reported");
    assert_eq!(sink.navigations.lock().unwrap().len(), 1);
}

// The page's own title wins; a blank title falls back to the host; a title from a page the tab
// has left is ignored.
#[test]
fn page_titles_fall_back_to_the_host_and_ignore_stale_pages() {
    let (registry, factory, _) = browser_with_tab("https://example.test/path");
    factory.title_changed(0, 1, "  Example Docs  ");
    assert_eq!(tab_state(&registry).1, "Example Docs");
    factory.title_changed(0, 0, "Old page");
    assert_eq!(tab_state(&registry).1, "Example Docs");
    factory.title_changed(0, 1, "   ");
    assert_eq!(tab_state(&registry).1, "example.test");
}

// Pins a5c3ad84d's external open guard: a tab with no web or file address (a blank tab) has
// nothing safe to hand the system browser, and a stale target is refused, both before any
// program is started.
#[test]
fn open_external_refuses_a_blank_tab_and_a_stale_target() {
    let (registry, _, _) = browser_with_tab("");
    let error = registry.open_external(target("workspace", "tab", 1)).unwrap_err();
    assert_eq!(error.code, BrowserErrorCode::InvalidUrl);

    let (registry, _, _) = browser_with_tab("https://example.test/");
    let error = registry.open_external(target("workspace", "tab", 2)).unwrap_err();
    assert_eq!(error.code, BrowserErrorCode::StaleGeneration);
}

// Only one page of a workspace is on screen: showing a tab hides its siblings, and hiding the
// workspace hides every tab and disarms their pickers.
#[test]
fn showing_a_tab_hides_its_siblings_and_hiding_the_workspace_hides_all() {
    let (registry, factory, sink) = browser_with_tab("https://example.test/a");
    registry.create_tab(None, input("workspace", "second", 2, "https://example.test/b"), sink).unwrap();
    let (first, second) = (factory.view(0), factory.view(1));
    let hides = |view: &RecordingView| view.hides.load(Ordering::SeqCst);

    registry.show_tab(target("workspace", "second", 2)).unwrap();
    assert_eq!((hides(&first), hides(&second)), (1, 0));
    assert_eq!(second.shows.load(Ordering::SeqCst), 2, "shown at creation and again now");

    registry.inner.workspaces.lock().unwrap().get_mut("workspace").unwrap().tabs.get_mut("tab").unwrap().picker =
        Some(PickerState { generation: 1, epoch: 3 });
    registry.hide_workspace(BrowserWorkspaceTarget { workspace_id: "workspace".to_string() }).unwrap();
    assert_eq!((hides(&first), hides(&second)), (2, 1));
    assert!(registry.inner.workspaces.lock().unwrap()["workspace"].tabs.values().all(|tab| tab.picker.is_none()));
}

// Pins 4d640500e: every browser workspace shares the app's one persistent website data store,
// so sign-ins survive a recreated view; and a workspace stays bound to the profile it opened with.
#[test]
fn every_workspace_shares_one_website_data_store_and_keeps_its_profile() {
    let factory = Arc::new(RecordingFactory::default());
    let registry = BrowserRegistry::with_factory(factory.clone());
    let sink = Arc::new(RecordingSink::default());
    registry.create_tab(None, input("one", "tab", 1, ""), sink.clone()).unwrap();
    let mut with_profile = input("two", "tab", 1, "");
    with_profile.profile_id = Some("project-profile".to_string());
    registry.create_tab(None, with_profile, sink.clone()).unwrap();
    let stores: Vec<[u8; 16]> =
        factory.made.lock().unwrap().iter().map(|(_, _, profile)| profile.store_identifier).collect();
    assert_eq!(stores, [*b"mcb-browser-main", *b"mcb-browser-main"]);

    let mut other_profile = input("two", "second", 2, "");
    other_profile.profile_id = Some("another-profile".to_string());
    assert_eq!(registry.create_tab(None, other_profile, sink.clone()).unwrap_err().code, BrowserErrorCode::InvalidProfile);
    for unsafe_profile in ["../escape", "a/b", "  "] {
        let mut request = input("three", "tab", 1, "");
        request.profile_id = Some(unsafe_profile.to_string());
        assert_eq!(registry.create_tab(None, request, sink.clone()).unwrap_err().code, BrowserErrorCode::InvalidProfile);
    }
}

// Clearing a workspace's data closes every one of its views and forgets the workspace, so its
// next tab starts from generation 1 again.
#[test]
fn clearing_workspace_data_closes_its_views_and_forgets_it() {
    let (registry, factory, sink) = browser_with_tab("https://example.test/a");
    registry.create_tab(None, input("workspace", "second", 2, "https://example.test/b"), sink.clone()).unwrap();

    registry.clear_workspace_data(BrowserWorkspaceTarget { workspace_id: "workspace".to_string() }).unwrap();
    assert_eq!(factory.view(0).closes.load(Ordering::SeqCst), 1);
    assert_eq!(factory.view(1).closes.load(Ordering::SeqCst), 1);
    assert_eq!(registry.live_view_count().unwrap(), 0);
    registry.create_tab(None, input("workspace", "tab", 1, "https://example.test/"), sink).unwrap();
}
