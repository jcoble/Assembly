//! Links the system hands the app from outside it.

pub mod deep_links;
#[cfg(test)]
mod deep_links_tests;
pub mod external_urls;
#[cfg(test)]
mod external_urls_shapes_tests;
#[cfg(test)]
mod external_urls_tests;
mod waiting_session_link;
