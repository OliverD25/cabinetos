using CabinetOS.Core.Ipc;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Tests.Support;

/// <summary>A core that answers with a function and remembers every request.</summary>
internal sealed class FakeChannel(Func<CoreRequest, CoreReply> answer) : ICoreChannel
{
    public List<CoreRequest> Requests { get; } = [];

    public Task<CoreReply> RequestAsync(CoreRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(request.Id))
        {
            request.Id = Core.Diagnostics.Ulid.NewId();
        }
        Requests.Add(request);
        return Task.FromResult(answer(request));
    }
}
