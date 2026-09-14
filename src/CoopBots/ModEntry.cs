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

    internal const string FallbackVersion = "0.36.0";

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
            LobbyBotService.ValidateRuntimeSchema();
            var harmony = new Harmony("cn.xiwa.sts2.coopbots");
            harmony.PatchAll(typeof(ModEntry).Assembly);
            Log.Info($"CoopBots {Version} loaded.");
        }
        catch (Exception exception)
        {
            Log.Error($"CoopBots failed to initialize: {exception}");
            throw;
        }
    }
}


