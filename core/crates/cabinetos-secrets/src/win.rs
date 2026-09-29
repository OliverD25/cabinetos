//! The Credential Manager calls: `CredWriteW`, `CredReadW`, `CredDeleteW`,
//! `CredEnumerateW` and `CredFree`, on generic credentials persisted for
//! the local machine.

use windows::Win32::Foundation::ERROR_NOT_FOUND;
use windows::Win32::Security::Credentials::{
    CRED_PERSIST_LOCAL_MACHINE, CRED_TYPE_GENERIC, CREDENTIALW, CredDeleteW, CredEnumerateW,
    CredFree, CredReadW, CredWriteW,
};
use windows::core::{PCWSTR, PWSTR};

/// The user name each credential carries; the Credential Manager's list
/// shows it.
const USER_NAME: &str = "CabinetOS";

fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(Some(0)).collect()
}

fn is_not_found(error: &windows::core::Error) -> bool {
    error.code() == ERROR_NOT_FOUND.to_hresult()
}

/// Writes `blob` as the credential `target`, replacing one that is there.
pub(crate) fn write(target: &str, blob: &[u8]) -> Result<(), String> {
    let mut target = wide(target);
    let mut user = wide(USER_NAME);
    let mut blob = blob.to_vec();
    let credential = CREDENTIALW {
        Type: CRED_TYPE_GENERIC,
        TargetName: PWSTR(target.as_mut_ptr()),
        CredentialBlobSize: u32::try_from(blob.len()).map_err(|error| error.to_string())?,
        CredentialBlob: blob.as_mut_ptr(),
        Persist: CRED_PERSIST_LOCAL_MACHINE,
        UserName: PWSTR(user.as_mut_ptr()),
        ..CREDENTIALW::default()
    };
    // SAFETY: every pointer in `credential` points into a local buffer
    // (`target`, `user`, `blob`) that outlives the call; the strings are
    // NUL-terminated and the blob's size is its length. Windows copies them.
    unsafe { CredWriteW(&raw const credential, 0) }.map_err(|error| error.message())
}

/// The blob of the credential `target`, or `None` when there is none.
pub(crate) fn read(target: &str) -> Result<Option<Vec<u8>>, String> {
    let target = wide(target);
    let mut found: *mut CREDENTIALW = std::ptr::null_mut();
    // SAFETY: `target` is NUL-terminated and outlives the call; `found`
    // receives a buffer Windows allocates, freed below with CredFree.
    let result = unsafe {
        CredReadW(
            PCWSTR(target.as_ptr()),
            CRED_TYPE_GENERIC,
            None,
            &raw mut found,
        )
    };
    match result {
        Ok(()) => {}
        Err(error) if is_not_found(&error) => return Ok(None),
        Err(error) => return Err(error.message()),
    }
    // SAFETY: CredReadW succeeded, so `found` points to a valid CREDENTIALW
    // whose blob holds CredentialBlobSize bytes (or is null when empty);
    // the bytes are copied before the buffer is freed, once.
    let blob = unsafe {
        let credential = &*found;
        let blob = if credential.CredentialBlob.is_null() {
            Vec::new()
        } else {
            std::slice::from_raw_parts(
                credential.CredentialBlob,
                credential.CredentialBlobSize as usize,
            )
            .to_vec()
        };
        CredFree(found.cast());
        blob
    };
    Ok(Some(blob))
}

/// Deletes the credential `target`. `false` when there was none.
pub(crate) fn delete(target: &str) -> Result<bool, String> {
    let target = wide(target);
    // SAFETY: `target` is NUL-terminated and outlives the call.
    match unsafe { CredDeleteW(PCWSTR(target.as_ptr()), CRED_TYPE_GENERIC, None) } {
        Ok(()) => Ok(true),
        Err(error) if is_not_found(&error) => Ok(false),
        Err(error) => Err(error.message()),
    }
}

/// The target names that match `filter` (a prefix followed by `*`).
pub(crate) fn enumerate(filter: &str) -> Result<Vec<String>, String> {
    let filter = wide(filter);
    let mut count = 0u32;
    let mut list: *mut *mut CREDENTIALW = std::ptr::null_mut();
    // SAFETY: `filter` is NUL-terminated and outlives the call; `count` and
    // `list` receive the number and an array Windows allocates, freed below
    // with CredFree.
    let result =
        unsafe { CredEnumerateW(PCWSTR(filter.as_ptr()), None, &raw mut count, &raw mut list) };
    match result {
        Ok(()) => {}
        Err(error) if is_not_found(&error) => return Ok(Vec::new()),
        Err(error) => return Err(error.message()),
    }
    // SAFETY: CredEnumerateW succeeded, so `list` holds `count` pointers to
    // valid CREDENTIALW records with NUL-terminated target names; the names
    // are copied before the array is freed, once.
    let names = unsafe {
        let names = std::slice::from_raw_parts(list, count as usize)
            .iter()
            .filter(|credential| !credential.is_null())
            .filter_map(|credential| (**credential).TargetName.to_string().ok())
            .collect();
        CredFree(list.cast());
        names
    };
    Ok(names)
}
