//! One folder per feature, `features/<feature>/`, which never uses another feature's folder. At start the shell's
//! `PlaceholderPane::register_all` runs before any feature registers its panes, because the kit's registry keeps the
//! last registration for a name. Pane names come from core/pane_names.rs.

pub mod meter;
pub mod palette;
