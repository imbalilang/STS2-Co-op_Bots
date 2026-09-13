namespace CoopBots.Kernel.Vendor;

// Host facade only. Deliberately has no ModInitializer, lifecycle subscriptions,
// online presence, auto-deployment, UI or persistent settings initialization.
internal static class Entry
{
    internal const string ModId = "CoopBots";
    internal static readonly MegaCrit.Sts2.Core.Logging.Logger Logger = new("CoopBots.Kernel", default);
}
