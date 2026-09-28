//! Deleting to the Recycle Bin through the shell's `IFileOperation`, the way
//! Explorer does, silently: no dialogs, no confirmations. It runs on the
//! job's own thread, which joins a single-threaded COM apartment for the
//! job's lifetime.

use std::marker::PhantomData;

use windows::Win32::System::Com::{
    CLSCTX_ALL, COINIT_APARTMENTTHREADED, COINIT_DISABLE_OLE1DDE, CoCreateInstance, CoInitializeEx,
    CoUninitialize, IBindCtx,
};
use windows::Win32::UI::Shell::{
    FOF_ALLOWUNDO, FOF_NOCONFIRMATION, FOF_NOERRORUI, FOF_SILENT, FOFX_RECYCLEONDELETE,
    FileOperation, IFileOperation, IFileOperationProgressSink, IShellItem,
    SHCreateItemFromParsingName,
};
use windows::core::{IUnknown, PCWSTR};

use crate::win::{Code, code, code_of};

/// COM on the current thread, until dropped. Not `Send`: COM must be left on
/// the thread that entered it.
pub(crate) struct Apartment {
    _thread_bound: PhantomData<*const ()>,
}

impl Apartment {
    /// Joins a single-threaded apartment on this thread.
    pub(crate) fn enter() -> Result<Self, Code> {
        // SAFETY: a plain call; a success is balanced by CoUninitialize when
        // the apartment drops, on this same thread (the type is not Send).
        let result =
            unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE) };
        if result.is_ok() {
            Ok(Self {
                _thread_bound: PhantomData,
            })
        } else {
            Err(result.0.cast_unsigned())
        }
    }
}

impl Drop for Apartment {
    fn drop(&mut self) {
        // SAFETY: balances the successful CoInitializeEx of `enter` on this
        // thread; every COM object of the apartment is released by now,
        // because they only live inside `recycle`.
        unsafe { CoUninitialize() };
    }
}

/// Sends `path`, a file or a folder with everything in it, to the Recycle
/// Bin.
pub(crate) fn recycle(_apartment: &Apartment, path: &str) -> Result<(), Code> {
    // The shell parses plain paths; it does not take the `\\?\` form.
    let wide: Vec<u16> = path.encode_utf16().chain(Some(0)).collect();
    // SAFETY: COM is initialized on this thread (the apartment borrow proves
    // it), the class ID is the shell's FileOperation, and `wide` is
    // NUL-terminated and outlives every call below.
    unsafe {
        let operation: IFileOperation =
            CoCreateInstance(&FileOperation, None::<&IUnknown>, CLSCTX_ALL)
                .map_err(|error| code_of(&error))?;
        operation
            .SetOperationFlags(
                FOF_ALLOWUNDO
                    | FOF_NOCONFIRMATION
                    | FOF_NOERRORUI
                    | FOF_SILENT
                    | FOFX_RECYCLEONDELETE,
            )
            .map_err(|error| code_of(&error))?;
        let item: IShellItem =
            SHCreateItemFromParsingName(PCWSTR(wide.as_ptr()), None::<&IBindCtx>)
                .map_err(|error| code_of(&error))?;
        operation
            .DeleteItem(&item, None::<&IFileOperationProgressSink>)
            .map_err(|error| code_of(&error))?;
        operation
            .PerformOperations()
            .map_err(|error| code_of(&error))?;
        if operation
            .GetAnyOperationsAborted()
            .map_err(|error| code_of(&error))?
            .as_bool()
        {
            return Err(code::REQUEST_ABORTED);
        }
    }
    Ok(())
}
