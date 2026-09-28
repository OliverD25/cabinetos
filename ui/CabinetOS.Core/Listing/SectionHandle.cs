using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace CabinetOS.Core.Listing;

/// <summary>
/// A shared-memory section handle the core duplicated into this process
/// (docs/ipc.md, "Listing a directory"). This process owns it and closes it;
/// a handle nobody takes is still closed by the finalizer.
/// </summary>
public sealed class SectionHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Takes ownership of the handle value from a <c>listing_*</c> message.</summary>
    public SectionHandle(nint handle)
        : base(ownsHandle: true) => SetHandle(handle);

    /// <inheritdoc/>
    protected override bool ReleaseHandle() => PInvoke.CloseHandle(new HANDLE(handle));
}
