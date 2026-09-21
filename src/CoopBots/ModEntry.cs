using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace CoopBots;

[ModInitializer("Initialize")]
public static class ModEntry
{
    /// <summary>
    /// Read from the manifest that ships next to this assembly, so the version in
    /// the log cannot drift from the version of the package. The compiled
    /// fallback only applies where no manifest is present (the test harness), and
    /// a regression keeps it equal to the manifest.
    /// </summary>
    public static readonly string Version = ResolveVersion();

    internal const string FallbackVersion = "0.38.0";

    private static string ResolveVersion()
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "mod_manifest.json");
            if (System.IO.File.Exists(path))
            {
                using var document = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("version", out var value)
                    && value.GetString() is { Length: > 0 } version)
                    return version;
            }
        }
        catch
        {
            // A manifest we cannot read is not a reason to fail to load.
        }
        return FallbackVersion;
    }

    public static void Initialize()
    {
        try
        {
            KernelAssemblyLoader.Load();
            RegisterKernelHooks();
            LobbyBotService.ValidateRuntimeSchema();
            var harmony = new Harmony("cn.xiwa.sts2.coopbots");
            harmony.PatchAll(typeof(ModEntry).Assembly);
            // 无人值守实机测试的开局入口。没设哨兵文件时 Attach 立刻返回，正式包零行为。
            // 放在 PatchAll 之后：它只挂一个 Godot 定时器，不依赖任何补丁。
            LiveTestMenuTicker.Attach();
            Log.Info($"CoopBots {Version} loaded.");
        }
        catch (Exception exception)
        {
            Log.Error($"CoopBots failed to initialize: {exception}");
            throw;
        }
    }

    /// <summary>
    /// The only place in this assembly that NAMES a CoopBots.Kernel type.
    /// </summary>
    /// <remarks>
    /// Must not be inlined — and for the same reason the call has to sit AFTER
    /// KernelAssemblyLoader.Load() rather than being written inline there.
    ///
    /// The kernel is not on the compile-time resolution path: it is loaded from this
    /// mod's own folder by reflection at the top of Initialize. The JIT resolves every
    /// type named in a method body when it compiles THAT method, so putting the
    /// reference directly in Initialize made the method demand CoopBots.Kernel before its
    /// own first statement ran. The game then failed to start with
    /// "Could not load file or assembly 'CoopBots.Kernel'" from the mod initializer.
    ///
    /// Registering the rule here also keeps the dependency one-way: the gold multiplier
    /// lives in BotRegistry (CoopBots), which the kernel must not reference, so the kernel
    /// exposes a resolver the CoopBots layer fills in.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterKernelHooks()
    {
        // The live game triples a Cheated bot's gold inside PlayerCmd.GainGold
        // (BotGoldCheatPatch). The simulator pays gold through its own ledger and never
        // passes that command, so it needs the same rule to predict the same numbers —
        // otherwise a Cheated seat's gold-gain card drifts on `field=gold` the instant it
        // resolves.
        CoopBots.Kernel.Vendor.SimulatedGoldGain.MultiplierResolver =
            static netId => BotRegistry.IsCheated(netId) ? 3 : 1;
    }
}


