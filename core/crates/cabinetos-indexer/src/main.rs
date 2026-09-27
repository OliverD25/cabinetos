//! `cabinetos-indexer.exe`: the elevated process around `cabinetos-index`. It
//! reads the MFT, tails the USN Journal and answers read-only index queries from
//! the core over a named pipe. It never performs file operations (least
//! privilege, ADR 0002).
//!
//! Serves Constitution Article 1 (Zero-Compromise Performance: real-time
//! NT-level file indexing).
//!
//! Status: stub. Phase 6 of `docs/PLAN.md` fills it in.
#![forbid(unsafe_code)]

fn main() {
    println!("cabinetos-indexer: not implemented yet (Phase 6 of docs/PLAN.md)");
}
