//! Shared-memory sections for the data channel (brief §4, PLAN §3).
//!
//! The core creates a page-file-backed section, maps it, writes a listing
//! (layouts in `cabinetos_protocol::shm`), and duplicates the section handle
//! into the UI process. The handle's numeric value travels over the pipe; the
//! UI maps the section and reads it by pointer, with no copy.
//!
//! Phase 1 provides only this helper. Nothing writes listings yet.

use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};

use windows::Win32::Foundation::{
    DUPLICATE_SAME_ACCESS, DuplicateHandle, HANDLE, INVALID_HANDLE_VALUE,
};
use windows::Win32::System::Memory::{
    CreateFileMappingW, FILE_MAP_ALL_ACCESS, MEMORY_MAPPED_VIEW_ADDRESS, MapViewOfFile,
    PAGE_READWRITE, UnmapViewOfFile,
};
use windows::Win32::System::Threading::{GetCurrentProcess, OpenProcess, PROCESS_DUP_HANDLE};
use windows::core::PCWSTR;

use crate::IpcError;

/// The numeric value of a handle that is valid in another process. It is
/// only a number here: this process must never use it as a handle.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct RawHandleValue(pub u64);

/// An unnamed, page-file-backed shared-memory section. The handle is closed
/// when the value drops; views keep their own reference to the memory.
#[derive(Debug)]
pub struct SharedSection {
    handle: OwnedHandle,
    size: usize,
}

impl SharedSection {
    /// Creates a section of `size` bytes, zero-filled. It has no name, so
    /// only processes that are given a handle can open it.
    pub fn create(size: usize) -> Result<Self, IpcError> {
        let size64 = u64::try_from(size).expect("usize fits in u64 on Windows");
        let high = u32::try_from(size64 >> 32).expect("the high half of a u64 fits in u32");
        let low = u32::try_from(size64 & 0xFFFF_FFFF).expect("the low half of a u64 fits in u32");
        // SAFETY: INVALID_HANDLE_VALUE asks for a section backed by the page
        // file; no security attributes means a default, non-inheritable
        // handle; a null name creates an unnamed section. None of the
        // arguments point to memory that must outlive the call.
        let raw = unsafe {
            CreateFileMappingW(
                INVALID_HANDLE_VALUE,
                None,
                PAGE_READWRITE,
                high,
                low,
                PCWSTR::null(),
            )
        }?;
        // SAFETY: the call succeeded and returned a new handle that nothing
        // else owns.
        let handle = unsafe { OwnedHandle::from_raw_handle(raw.0) };
        Ok(Self { handle, size })
    }

    /// The section's size in bytes.
    #[must_use]
    pub fn size(&self) -> usize {
        self.size
    }

    /// Maps the whole section into this process, readable and writable.
    pub fn map(&self) -> Result<MappedView, IpcError> {
        // SAFETY: the handle is a valid section handle while `self` lives,
        // and mapping `size` bytes from offset 0 stays inside the section.
        // The call does not touch Rust memory.
        let address = unsafe { MapViewOfFile(self.raw(), FILE_MAP_ALL_ACCESS, 0, 0, self.size) };
        if address.Value.is_null() {
            return Err(windows::core::Error::from_thread().into());
        }
        Ok(MappedView {
            address,
            len: self.size,
        })
    }

    /// Duplicates the section handle into process `pid` and returns the
    /// handle's value there, to be sent over the pipe. The target process owns
    /// the new handle and must close it.
    pub fn duplicate_for(&self, pid: u32) -> Result<RawHandleValue, IpcError> {
        // SAFETY: OpenProcess has no memory-safety preconditions; failure is
        // reported through the Result.
        let process = unsafe { OpenProcess(PROCESS_DUP_HANDLE, false, pid) }?;
        // SAFETY: OpenProcess succeeded, so this is a new handle we own;
        // OwnedHandle closes it when this function returns.
        let process = unsafe { OwnedHandle::from_raw_handle(process.0) };
        let mut duplicated = HANDLE::default();
        // SAFETY: the source process pseudo-handle, the section handle and the
        // target process handle are all valid for the duration of the call,
        // and `duplicated` is a valid output location. The new handle lives in
        // the target process; this process never closes or uses it.
        unsafe {
            DuplicateHandle(
                GetCurrentProcess(),
                self.raw(),
                HANDLE(process.as_raw_handle()),
                &raw mut duplicated,
                0,
                false,
                DUPLICATE_SAME_ACCESS,
            )
        }?;
        Ok(RawHandleValue(
            u64::try_from(duplicated.0.addr()).expect("usize fits in u64 on Windows"),
        ))
    }

    fn raw(&self) -> HANDLE {
        HANDLE(self.handle.as_raw_handle())
    }
}

/// A section mapped into this process. Unmapped when the value drops.
///
/// The same section can be mapped several times, in this process or in
/// another one, and a write through one view is visible through the others.
/// Rust cannot see that sharing: a caller must not change the bytes through
/// one view while it holds a slice from another. The listing protocol keeps
/// one writer at a time and signals new data through
/// `ListingHeader::generation`.
#[derive(Debug)]
pub struct MappedView {
    address: MEMORY_MAPPED_VIEW_ADDRESS,
    len: usize,
}

// SAFETY: the view is plain memory owned by this value until Drop unmaps it;
// no thread affinity is involved, so the owner may move to another thread.
unsafe impl Send for MappedView {}

// SAFETY: `&MappedView` only allows reading (`as_slice`); writing needs
// `&mut MappedView`, so sharing references between threads cannot race.
unsafe impl Sync for MappedView {}

impl MappedView {
    /// The mapped length in bytes.
    #[must_use]
    pub fn len(&self) -> usize {
        self.len
    }

    /// Whether the view is empty.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.len == 0
    }

    /// The mapped bytes.
    #[must_use]
    pub fn as_slice(&self) -> &[u8] {
        // SAFETY: MapViewOfFile mapped `len` readable bytes at `address`
        // (zero-filled at creation, so always initialized). The mapping lasts
        // until Drop, which needs `&mut self` and so cannot run while this
        // borrow lives. Writes through other views are the caller's contract
        // (see the type docs).
        unsafe { std::slice::from_raw_parts(self.address.Value.cast::<u8>(), self.len) }
    }

    /// The mapped bytes, writable.
    #[must_use]
    pub fn as_mut_slice(&mut self) -> &mut [u8] {
        // SAFETY: as in `as_slice`, and the view was mapped with write access.
        // `&mut self` guarantees no other slice of this view exists.
        unsafe { std::slice::from_raw_parts_mut(self.address.Value.cast::<u8>(), self.len) }
    }
}

impl Drop for MappedView {
    fn drop(&mut self) {
        // SAFETY: `address` came from MapViewOfFile and is unmapped only here;
        // no slice of it can outlive `self`.
        let _ = unsafe { UnmapViewOfFile(self.address) };
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const SIZE: usize = 64 * 1024;

    /// A byte pattern that does not repeat on page boundaries (251 is prime).
    fn pattern() -> Vec<u8> {
        (0..=250u8).cycle().take(SIZE).collect()
    }

    #[test]
    fn two_views_of_one_section_share_bytes() {
        let section = SharedSection::create(SIZE).unwrap();
        let mut writer = section.map().unwrap();
        let reader = section.map().unwrap();
        assert!(reader.as_slice().iter().all(|&byte| byte == 0));

        writer.as_mut_slice().copy_from_slice(&pattern());
        drop(writer);
        assert_eq!(reader.as_slice(), pattern().as_slice());
    }

    #[test]
    fn a_duplicated_handle_maps_the_same_bytes() {
        let section = SharedSection::create(SIZE).unwrap();
        section
            .map()
            .unwrap()
            .as_mut_slice()
            .copy_from_slice(&pattern());

        let value = section.duplicate_for(std::process::id()).unwrap();
        assert_ne!(value.0, 0);
        let raw = usize::try_from(value.0).unwrap();
        // SAFETY: `value` was duplicated into this very process, so it is a
        // valid section handle that nothing else owns; the new OwnedHandle
        // closes it when `copy` drops.
        let handle = unsafe { OwnedHandle::from_raw_handle(std::ptr::without_provenance_mut(raw)) };
        let copy = SharedSection { handle, size: SIZE };
        assert_eq!(copy.map().unwrap().as_slice(), pattern().as_slice());

        // The original handle still works after the duplicate is closed.
        drop(copy);
        assert_eq!(section.map().unwrap().as_slice(), pattern().as_slice());
    }

    #[test]
    fn duplicating_into_a_missing_process_fails() {
        let section = SharedSection::create(SIZE).unwrap();
        // PID 0 is the System Idle Process, which cannot be opened.
        assert!(section.duplicate_for(0).is_err());
    }
}
