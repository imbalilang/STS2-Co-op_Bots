namespace CoopBots.Kernel.Vendor.Engine.Common;

internal static class EngineDiagnostics
{
    public static void Warn(string message)
        => global::CoopBots.Kernel.Vendor.Entry.Logger?.Warn(message);
}
