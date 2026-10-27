//! `Endpoint` is where the backend listens and the token its `hello` carries. The launcher (g0-host) knows the port it
//! starts the backend child on and reads the token from the child's token file. The token is never printed, so `Debug` leaves it out.

use std::fmt;

pub struct Endpoint {
    pub port: u16,
    pub token: String,
}

impl fmt::Debug for Endpoint {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Endpoint")
            .field("port", &self.port)
            .finish_non_exhaustive()
    }
}
