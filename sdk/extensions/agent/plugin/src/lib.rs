//! CabinetOS Agent, a Core Plugin (Article 11's engine layer): it works the
//! file manager through the same command line a person has, and shows every
//! change as a preview before it happens. It never holds a key (the core
//! adds the stored secret to the model request), never runs a shell (the
//! model writes `cab` command lines that the command line's own definitions
//! parse), and reaches the disk only through the core (`core-request`), so
//! every change is a job in the undo journal.
//!
//! The logic is ordinary Rust over the [`host::Host`] trait and runs as
//! tests on this machine; `glue.rs` binds it to the host functions when
//! built for `wasm32-wasip2`.

pub mod agent;
pub mod audit;
pub mod cmdline;
pub mod handler;
pub mod host;
pub mod paths;
pub mod plan;
pub mod prompt;
pub mod provider;
pub mod settings;
pub mod state;
pub mod tier;
pub mod time;
pub mod tools;

#[cfg(test)]
mod testing;

#[cfg(target_arch = "wasm32")]
#[allow(
    clippy::too_many_arguments,
    reason = "the record types wit-bindgen writes"
)]
mod glue;
