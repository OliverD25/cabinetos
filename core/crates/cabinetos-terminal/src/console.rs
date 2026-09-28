//! The client end of a session in a console window, for
//! `cabinetos-cli term`: raw mode, the window's size, and keys as VT text.

use std::io;

use windows::Win32::Foundation::HANDLE;
use windows::Win32::System::Console::{
    CONSOLE_MODE, CONSOLE_SCREEN_BUFFER_INFO, DISABLE_NEWLINE_AUTO_RETURN, ENABLE_ECHO_INPUT,
    ENABLE_LINE_INPUT, ENABLE_PROCESSED_INPUT, ENABLE_PROCESSED_OUTPUT,
    ENABLE_VIRTUAL_TERMINAL_INPUT, ENABLE_VIRTUAL_TERMINAL_PROCESSING, GetConsoleCP,
    GetConsoleMode, GetConsoleOutputCP, GetConsoleScreenBufferInfo, GetStdHandle, ReadConsoleW,
    STD_HANDLE, STD_INPUT_HANDLE, STD_OUTPUT_HANDLE, SetConsoleCP, SetConsoleMode,
    SetConsoleOutputCP,
};

/// The UTF-8 code page.
const UTF8: u32 = 65001;

fn std_handle(which: STD_HANDLE) -> Option<HANDLE> {
    // SAFETY: GetStdHandle only reads this process's standard handles.
    let handle = unsafe { GetStdHandle(which) }.ok()?;
    (!handle.is_invalid() && !handle.0.is_null()).then_some(handle)
}

fn mode(handle: HANDLE) -> Option<CONSOLE_MODE> {
    let mut mode = CONSOLE_MODE::default();
    // SAFETY: `handle` is one of this process's standard handles, and
    // `mode` is a valid output; the call fails for anything but a console.
    unsafe { GetConsoleMode(handle, &raw mut mode) }.ok()?;
    Some(mode)
}

fn set_mode(handle: HANDLE, mode: CONSOLE_MODE) -> io::Result<()> {
    // SAFETY: `handle` is one of this process's standard console handles.
    unsafe { SetConsoleMode(handle, mode) }?;
    Ok(())
}

/// Whether standard input is a console (not a pipe or a file).
#[must_use]
pub fn stdin_is_console() -> bool {
    std_handle(STD_INPUT_HANDLE).and_then(mode).is_some()
}

/// Whether standard output is a console (not a pipe or a file).
#[must_use]
pub fn stdout_is_console() -> bool {
    std_handle(STD_OUTPUT_HANDLE).and_then(mode).is_some()
}

/// The size of the console window standard output shows, in character
/// cells: `(columns, rows)`; `None` when standard output is not a console.
#[must_use]
pub fn size() -> Option<(u16, u16)> {
    let handle = std_handle(STD_OUTPUT_HANDLE)?;
    let mut info = CONSOLE_SCREEN_BUFFER_INFO::default();
    // SAFETY: `handle` is this process's standard output and `info` a
    // valid output; the call fails for anything but a console.
    unsafe { GetConsoleScreenBufferInfo(handle, &raw mut info) }.ok()?;
    let cols = i32::from(info.srWindow.Right) - i32::from(info.srWindow.Left) + 1;
    let rows = i32::from(info.srWindow.Bottom) - i32::from(info.srWindow.Top) + 1;
    Some((u16::try_from(cols).ok()?, u16::try_from(rows).ok()?))
}

/// The console in raw mode while this lives: keys arrive one by one as VT
/// text, with no echo and no line editing, and Ctrl+C is a key rather than
/// a signal; output is read as VT sequences and UTF-8. Dropping it restores
/// the modes and code pages it found. A standard stream that is not a
/// console is left alone.
#[derive(Debug)]
pub struct RawMode {
    input: Option<CONSOLE_MODE>,
    output: Option<CONSOLE_MODE>,
    input_code_page: u32,
    output_code_page: u32,
}

impl RawMode {
    /// Switches the console to raw mode.
    pub fn enable() -> io::Result<Self> {
        // SAFETY: both only read the console's code pages (0 without one).
        let (input_code_page, output_code_page) = unsafe { (GetConsoleCP(), GetConsoleOutputCP()) };
        // Built first, so that Drop undoes whatever was changed before a
        // failure.
        let mut raw = Self {
            input: None,
            output: None,
            input_code_page,
            output_code_page,
        };
        if let Some(handle) = std_handle(STD_INPUT_HANDLE)
            && let Some(old) = mode(handle)
        {
            let cooked = ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT | ENABLE_PROCESSED_INPUT;
            set_mode(handle, (old & !cooked) | ENABLE_VIRTUAL_TERMINAL_INPUT)?;
            raw.input = Some(old);
        }
        if let Some(handle) = std_handle(STD_OUTPUT_HANDLE)
            && let Some(old) = mode(handle)
        {
            set_mode(
                handle,
                old | ENABLE_PROCESSED_OUTPUT
                    | ENABLE_VIRTUAL_TERMINAL_PROCESSING
                    | DISABLE_NEWLINE_AUTO_RETURN,
            )?;
            raw.output = Some(old);
        }
        if input_code_page != 0 {
            // SAFETY: only changes the console's code pages, restored in
            // Drop. A failure leaves them as they were, which is harmless.
            let _ = unsafe { (SetConsoleCP(UTF8), SetConsoleOutputCP(UTF8)) };
        }
        Ok(raw)
    }
}

impl Drop for RawMode {
    fn drop(&mut self) {
        if let (Some(old), Some(handle)) = (self.input, std_handle(STD_INPUT_HANDLE)) {
            let _ = set_mode(handle, old);
        }
        if let (Some(old), Some(handle)) = (self.output, std_handle(STD_OUTPUT_HANDLE)) {
            let _ = set_mode(handle, old);
        }
        if self.input_code_page != 0 {
            // SAFETY: restores the code pages `enable` found.
            let _ = unsafe {
                (
                    SetConsoleCP(self.input_code_page),
                    SetConsoleOutputCP(self.output_code_page),
                )
            };
        }
    }
}

/// Keys from the console, as UTF-8 text. In raw mode, special keys arrive
/// as VT sequences, such as `ESC [ A` for Up.
#[derive(Debug, Default)]
pub struct ConsoleInput {
    /// The first half of a character that one read split from its second.
    high_surrogate: Option<u16>,
}

impl ConsoleInput {
    /// Reads keys from standard input.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Waits for at least one key; returns what arrived.
    pub fn read(&mut self) -> io::Result<Vec<u8>> {
        let handle = std_handle(STD_INPUT_HANDLE)
            .ok_or_else(|| io::Error::other("standard input is not open"))?;
        loop {
            let mut units = [0u16; 1024];
            let mut read = 0u32;
            // SAFETY: `units` is writable for its whole length in UTF-16
            // units, which is the count passed; `read` is a valid output; no
            // control structure is passed.
            unsafe {
                ReadConsoleW(
                    handle,
                    units.as_mut_ptr().cast(),
                    u32::try_from(units.len()).expect("a small buffer"),
                    &raw mut read,
                    None,
                )
            }?;
            let read = usize::try_from(read).expect("at most the buffer's length");
            let mut text: Vec<u16> = self.high_surrogate.take().into_iter().collect();
            text.extend_from_slice(&units[..read]);
            if let Some(&last) = text.last()
                && (0xD800..0xDC00).contains(&last)
            {
                self.high_surrogate = text.pop();
            }
            if !text.is_empty() {
                return Ok(String::from_utf16_lossy(&text).into_bytes());
            }
        }
    }
}
