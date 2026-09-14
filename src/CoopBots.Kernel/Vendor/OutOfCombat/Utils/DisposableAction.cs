// Ported from Random Foreseer 0.13.14 (MIT, copyright (c) 2026 hotwords123).
// See THIRD_PARTY_NOTICES.md. Verbatim from upstream.

namespace CoopBots.Kernel.Vendor.RandomForeseer.Utils;

public sealed class DisposableAction(Action action) : IDisposable
{
    private Action? _action = action ?? throw new ArgumentNullException(nameof(action));

    public void Dispose()
    {
        var pending = Interlocked.Exchange(ref _action, null);
        pending?.Invoke();
    }
}
