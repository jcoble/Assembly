//! A provider as people read it: its first letter raised, so the engine's `claude` reads "Claude". A name that is
//! already a display name, such as the agent's own "Claude Code", reads as it is.

pub fn provider_name(provider: &str) -> String {
    let mut letters = provider.chars();
    let first = letters.next().map(|first| first.to_uppercase());
    first.into_iter().flatten().chain(letters).collect()
}
