//! The helper's key, kept in the login Keychain under this app's own name.
//!
//! Read and written through Apple's own `security` tool rather than
//! Security.framework, for the reason written down in `claude_quota.rs`: an
//! unsigned development binary is a new program to the Keychain after every
//! rebuild, so a direct read asks for permission every time and "Always Allow"
//! never sticks. An item written by `security` already trusts `security`, so
//! both directions stay silent.

use super::HelperError;
use std::process::{Command, Stdio};

/// The Keychain service every helper key is filed under. The account is the
/// vendor name.
const KEYCHAIN_SERVICE: &str = "Assembly Helper";

/// What `security` exits with when the item is not there.
const SECURITY_ITEM_NOT_FOUND: i32 = 44;

/// The stored key for one vendor, or `None` when there is not one.
pub fn read_key(account: &str) -> Result<Option<String>, HelperError> {
    crate::proof_mode::refuse("The secret store").map_err(HelperError::Settings)?;
    let output = Command::new("/usr/bin/security")
        .args([
            "find-generic-password",
            "-s",
            KEYCHAIN_SERVICE,
            "-a",
            account,
            "-w",
        ])
        .stdin(Stdio::null())
        .output()
        .map_err(|error| HelperError::Transport(error.to_string()))?;
    if output.status.code() == Some(SECURITY_ITEM_NOT_FOUND) {
        return Ok(None);
    }
    if !output.status.success() {
        return Err(HelperError::Transport(
            "the key could not be read from the Keychain".to_string(),
        ));
    }
    let key = String::from_utf8_lossy(&output.stdout).trim().to_string();
    if key.is_empty() {
        return Ok(None);
    }
    Ok(Some(key))
}

/// Stores the key, replacing whatever was there for this vendor.
///
/// The key goes on `security`'s command line because `add-generic-password`
/// takes it no other way: the only alternative, leaving `-w` bare at the end,
/// reads the terminal rather than standard input whenever there is one, so an
/// app with no terminal cannot use it. The key is therefore visible to a
/// process list for as long as the call takes. That is the same exposure as
/// typing it into any command, and the alternative, writing it to a file
/// first, is worse.
pub fn write_key(account: &str, key: &str) -> Result<(), HelperError> {
    crate::proof_mode::refuse("The secret store").map_err(HelperError::Settings)?;
    let output = Command::new("/usr/bin/security")
        .args([
            "add-generic-password",
            "-U",
            "-s",
            KEYCHAIN_SERVICE,
            "-a",
            account,
            "-w",
            key,
        ])
        .stdin(Stdio::null())
        .output()
        .map_err(|error| HelperError::Transport(error.to_string()))?;
    if !output.status.success() {
        return Err(HelperError::Transport(
            "the key could not be stored in the Keychain".to_string(),
        ));
    }
    Ok(())
}

/// Removes the key for one vendor. A key that is not there is not an error.
pub fn delete_key(account: &str) -> Result<(), HelperError> {
    crate::proof_mode::refuse("The secret store").map_err(HelperError::Settings)?;
    let output = Command::new("/usr/bin/security")
        .args([
            "delete-generic-password",
            "-s",
            KEYCHAIN_SERVICE,
            "-a",
            account,
        ])
        .stdin(Stdio::null())
        .output()
        .map_err(|error| HelperError::Transport(error.to_string()))?;
    if output.status.success() || output.status.code() == Some(SECURITY_ITEM_NOT_FOUND) {
        return Ok(());
    }
    Err(HelperError::Transport(
        "the key could not be removed from the Keychain".to_string(),
    ))
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Touches the real login Keychain, so it is not part of the ordinary run.
    /// It writes one throwaway item under the service name above and removes it
    /// again. Run it by hand with `-- --ignored helper::keychain`.
    #[test]
    #[ignore]
    fn a_key_can_be_written_read_back_and_removed() {
        let account = "helper-keychain-test";

        write_key(account, "test-value-not-a-real-key").expect("the key should store");
        assert_eq!(
            read_key(account).expect("the key should read back"),
            Some("test-value-not-a-real-key".to_string())
        );

        delete_key(account).expect("the key should be removable");
        assert_eq!(
            read_key(account).expect("a missing key is not an error"),
            None
        );

        // Removing what is already gone is not an error either.
        delete_key(account).expect("removing a key twice is not an error");
    }
}
