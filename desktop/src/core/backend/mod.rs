//! The .NET backend as the window's child process: started at launch, restarted when it exits, stopped on quit. Its
//! ready line is the socket client's only reconnect signal.

pub mod backend_config;
pub mod launcher;
