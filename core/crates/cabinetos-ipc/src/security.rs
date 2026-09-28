//! The pipe's security descriptor: full access for the current user and for
//! nobody else (PLAN §3, "Security").
//!
//! Without it, a named pipe gets the default DACL, which also lets
//! Administrators, SYSTEM and (for reading) Everyone in. The descriptor below
//! is `D:P(A;;GA;;;<current user SID>)`: a protected DACL (nothing inherited)
//! with a single ACE that allows generic-all to the user running the process.

use std::ffi::c_void;
use std::io;
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};

use tokio::net::windows::named_pipe::{NamedPipeServer, ServerOptions};
use windows::Win32::Foundation::{HANDLE, HLOCAL, LocalFree};
use windows::Win32::Security::Authorization::{
    ConvertSidToStringSidW, ConvertStringSecurityDescriptorToSecurityDescriptorW, SDDL_REVISION_1,
};
use windows::Win32::Security::{
    GetTokenInformation, PSECURITY_DESCRIPTOR, SECURITY_ATTRIBUTES, TOKEN_QUERY, TOKEN_USER,
    TokenUser,
};
use windows::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};
use windows::core::{HSTRING, PWSTR};

use crate::IpcError;

/// The SDDL text of a DACL that allows only `sid`.
fn user_only_sddl(sid: &str) -> String {
    format!("D:P(A;;GA;;;{sid})")
}

/// A security descriptor that grants full access to the current user only.
#[derive(Debug)]
pub(crate) struct UserOnlySecurity {
    /// Allocated by `ConvertStringSecurityDescriptorToSecurityDescriptorW` with
    /// `LocalAlloc`; freed in `Drop`.
    descriptor: PSECURITY_DESCRIPTOR,
}

// SAFETY: `descriptor` points to heap memory owned by this value alone. It is
// never written after creation and is freed exactly once, in Drop, so moving
// the owner to another thread is sound.
unsafe impl Send for UserOnlySecurity {}

// SAFETY: shared references only ever read the descriptor (Windows copies it
// when creating a pipe instance), so concurrent use from threads is sound.
unsafe impl Sync for UserOnlySecurity {}

impl UserOnlySecurity {
    /// Builds the descriptor for the user this process runs as.
    pub(crate) fn for_current_user() -> Result<Self, IpcError> {
        let sddl = HSTRING::from(user_only_sddl(&current_user_sid()?));
        let mut descriptor = PSECURITY_DESCRIPTOR::default();
        // SAFETY: `sddl` is a valid NUL-terminated wide string that outlives
        // the call, and `descriptor` is a valid place for the output pointer.
        // On success the function allocates the descriptor with LocalAlloc;
        // `Self::drop` frees it.
        unsafe {
            ConvertStringSecurityDescriptorToSecurityDescriptorW(
                &sddl,
                SDDL_REVISION_1,
                &raw mut descriptor,
                None,
            )
        }?;
        Ok(Self { descriptor })
    }

    /// Creates one instance of the named pipe `name`, protected by this
    /// descriptor. Must run inside a Tokio runtime.
    pub(crate) fn create_pipe(
        &self,
        options: &ServerOptions,
        name: &str,
    ) -> io::Result<NamedPipeServer> {
        let mut attributes = SECURITY_ATTRIBUTES {
            nLength: u32::try_from(size_of::<SECURITY_ATTRIBUTES>())
                .expect("SECURITY_ATTRIBUTES is a few bytes long"),
            lpSecurityDescriptor: self.descriptor.0,
            bInheritHandle: false.into(),
        };
        // SAFETY: `attributes` is a valid SECURITY_ATTRIBUTES that lives for
        // the whole call, and its descriptor pointer is valid while `self` is
        // borrowed. CreateNamedPipeW copies the descriptor into the new kernel
        // object, so nothing refers to either after the call returns.
        unsafe {
            options
                .create_with_security_attributes_raw(name, (&raw mut attributes).cast::<c_void>())
        }
    }
}

impl Drop for UserOnlySecurity {
    fn drop(&mut self) {
        // SAFETY: the descriptor was allocated with LocalAlloc by
        // ConvertStringSecurityDescriptorToSecurityDescriptorW and is freed
        // only here.
        unsafe { LocalFree(Some(HLOCAL(self.descriptor.0))) };
    }
}

/// The current user's SID as text, for example `S-1-5-21-…-1001`.
pub(crate) fn current_user_sid() -> Result<String, IpcError> {
    let mut raw_token = HANDLE::default();
    // SAFETY: GetCurrentProcess returns a pseudo-handle that needs no closing,
    // and `raw_token` is a valid place for the output handle.
    unsafe { OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &raw mut raw_token) }?;
    // SAFETY: OpenProcessToken succeeded, so `raw_token` is a new handle that
    // this function owns; OwnedHandle closes it on every return path.
    let owned_token = unsafe { OwnedHandle::from_raw_handle(raw_token.0) };
    let token = HANDLE(owned_token.as_raw_handle());

    let mut needed = 0u32;
    // SAFETY: asks only for the required size (no buffer); `needed` is a valid
    // output location. This call fails with ERROR_INSUFFICIENT_BUFFER by
    // design, so its result is ignored.
    let _ = unsafe { GetTokenInformation(token, TokenUser, None, 0, &raw mut needed) };
    // u64 elements keep the buffer 8-byte aligned, as TOKEN_USER (which holds
    // a pointer) requires.
    let mut buffer = vec![0u64; usize::try_from(needed).unwrap_or(0).div_ceil(8)];
    // SAFETY: `buffer` is writable for at least `needed` bytes and aligned for
    // TOKEN_USER; `needed` is a valid output location.
    unsafe {
        GetTokenInformation(
            token,
            TokenUser,
            Some(buffer.as_mut_ptr().cast()),
            needed,
            &raw mut needed,
        )
    }?;
    // SAFETY: the call succeeded, so the buffer starts with an initialized
    // TOKEN_USER. Its SID pointer points into `buffer`, which outlives `user`.
    let user = unsafe { &*buffer.as_ptr().cast::<TOKEN_USER>() };

    let mut text = PWSTR::null();
    // SAFETY: `user.User.Sid` is a valid SID (see above), and `text` is a
    // valid output location. On success the string is allocated with
    // LocalAlloc and freed below.
    unsafe { ConvertSidToStringSidW(user.User.Sid, &raw mut text) }?;
    // SAFETY: `text` is a valid NUL-terminated wide string returned by
    // ConvertSidToStringSidW.
    let sid = unsafe { text.to_string() };
    // SAFETY: `text` was allocated with LocalAlloc by ConvertSidToStringSidW
    // and is not used after this line.
    unsafe { LocalFree(Some(HLOCAL(text.0.cast()))) };
    sid.map_err(|error| IpcError::Io(io::Error::other(error)))
}

#[cfg(test)]
mod tests {
    use windows::Win32::Security::Authorization::{
        ConvertSecurityDescriptorToStringSecurityDescriptorW, GetSecurityInfo, SE_KERNEL_OBJECT,
    };
    use windows::Win32::Security::DACL_SECURITY_INFORMATION;

    use super::*;

    #[test]
    fn builds_a_protected_single_ace_dacl() {
        assert_eq!(
            user_only_sddl("S-1-5-21-1-2-3-1001"),
            "D:P(A;;GA;;;S-1-5-21-1-2-3-1001)"
        );
    }

    #[test]
    fn finds_the_current_user_sid() {
        let sid = current_user_sid().unwrap();
        assert!(sid.starts_with("S-1-5-"), "{sid}");
    }

    /// Reads the DACL back from a real pipe instance, as Windows stored it.
    #[tokio::test]
    async fn the_pipe_admits_only_the_current_user() {
        let security = UserOnlySecurity::for_current_user().unwrap();
        let name = format!(
            r"\\.\pipe\cabinetos-test-dacl-{:016x}",
            rand::random::<u64>()
        );
        let mut options = ServerOptions::new();
        options.first_pipe_instance(true);
        let server = security.create_pipe(&options, &name).unwrap();

        let mut descriptor = PSECURITY_DESCRIPTOR::default();
        // SAFETY: the server handle is valid while `server` lives, and
        // `descriptor` is a valid output location. The descriptor is
        // LocalAlloc'd and freed below.
        unsafe {
            GetSecurityInfo(
                HANDLE(server.as_raw_handle()),
                SE_KERNEL_OBJECT,
                DACL_SECURITY_INFORMATION,
                None,
                None,
                None,
                None,
                Some(&raw mut descriptor),
            )
        }
        .ok()
        .unwrap();
        let mut text = PWSTR::null();
        // SAFETY: `descriptor` is valid (see above); `text` is a valid output
        // location, LocalAlloc'd on success and freed below.
        unsafe {
            ConvertSecurityDescriptorToStringSecurityDescriptorW(
                descriptor,
                SDDL_REVISION_1,
                DACL_SECURITY_INFORMATION,
                &raw mut text,
                None,
            )
        }
        .unwrap();
        // SAFETY: `text` is a valid NUL-terminated wide string.
        let sddl = unsafe { text.to_string() }.unwrap();
        // SAFETY: both were allocated with LocalAlloc and are not used again.
        unsafe {
            LocalFree(Some(HLOCAL(text.0.cast())));
            LocalFree(Some(HLOCAL(descriptor.0)));
        }

        // Windows maps generic-all (GA) to the pipe's full access (FA).
        let sid = current_user_sid().unwrap();
        assert_eq!(sddl, format!("D:P(A;;FA;;;{sid})"));
    }
}
