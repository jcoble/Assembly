use super::{open_external_url, BrowserErrorCode};

#[test]
fn only_web_addresses_reach_the_system_opener() {
    // macOS `open` runs a `.command` or `.terminal` file, so a local file address must never reach it.
    for url in ["file:///Users/me/Downloads/run.command", "file://localhost/tmp/setup.terminal", "about:blank"] {
        let error = open_external_url(url).expect_err(url);
        assert_eq!(error.code, BrowserErrorCode::InvalidUrl, "{url}");
    }
}
