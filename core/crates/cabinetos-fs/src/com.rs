//! A COM apartment for the shell calls that want one: `ShellExecuteExW`
//! may hand a file to a COM server, and `SHGetFileInfoW` and the system
//! image lists need COM to be ready on the calling thread.

use std::marker::PhantomData;

use windows::Win32::System::Com::{
    COINIT_APARTMENTTHREADED, COINIT_DISABLE_OLE1DDE, CoInitializeEx, CoUninitialize,
};

/// A single-threaded COM apartment on this thread, left when dropped. Not
/// `Send`: COM must be left on the thread that entered it.
pub(crate) struct Apartment {
    entered: bool,
    _thread_bound: PhantomData<*const ()>,
}

impl Apartment {
    /// Joins an apartment. A thread already in another kind of apartment
    /// stays in it (the shell works there too), and nothing is left then.
    pub(crate) fn enter() -> Self {
        // SAFETY: a plain call; a success is balanced by CoUninitialize in
        // `drop`, on this same thread (the type is not Send).
        let result =
            unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE) };
        Self {
            entered: result.is_ok(),
            _thread_bound: PhantomData,
        }
    }
}

impl Drop for Apartment {
    fn drop(&mut self) {
        if self.entered {
            // SAFETY: balances the successful CoInitializeEx of `enter` on
            // this thread. Callers drop their COM objects before the
            // apartment: they are declared after it.
            unsafe { CoUninitialize() };
        }
    }
}
