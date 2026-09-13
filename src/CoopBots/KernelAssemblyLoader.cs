using System.Reflection;
using System.Runtime.Loader;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;

namespace CoopBots;

// No kernel types in this method's signature/body: load the sibling dependency
// before Harmony discovers runtime methods whose signatures reference it.
internal static class KernelAssemblyLoader
{
    internal static void Load()
    {
        var assembly = typeof(ModEntry).Assembly;
        var path = Path.Combine(Path.GetDirectoryName(assembly.Location)!, "CoopBots.Kernel.dll");
        // Crucial: load into the SAME load context as this mod. Loading into the
        // default context instead gives the kernel its own copy of the game
        // assemblies, so its CombatState/CardModel are a different type identity
        // than the ones the mod passes in. Every kernel call then fails at JIT
        // time with MissingMethodException before any try/catch can run, which
        // aborts the whole bot tick and leaves the bots idle for the fight.
        var context = AssemblyLoadContext.GetLoadContext(assembly) ?? AssemblyLoadContext.Default;
        Assembly kernel;
        try
        {
            kernel = context.Assemblies.FirstOrDefault(a => a.GetName().Name == "CoopBots.Kernel")
                ?? context.LoadFromAssemblyPath(path);
            Log.Info($"CoopBots kernel loaded into '{context.Name ?? "default"}': {path}");
        }
        catch (Exception error)
        {
            // Degraded but alive: the runtime wraps Poll in a try/catch, so a
            // mismatched kernel only costs the kernel planner, not the mod.
            Log.Warn($"CoopBots kernel load into '{context.Name ?? "default"}' failed; using default: {error.GetBaseException()}");
            kernel = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == "CoopBots.Kernel")
                ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        }
        // Diagnostic only: the exact signature the planner calls must exist. A
        // mismatch is logged here instead of surfacing as a mid-combat JIT crash.
        var session = kernel.GetType("CoopBots.Kernel.KernelSession");
        if (session?.GetMethod("CaptureLiveStamp", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(CombatState) }, null) is null)
            Log.Warn("CoopBots kernel is missing KernelSession.CaptureLiveStamp(CombatState); "
                + "the kernel planner will be skipped and the legacy planner used.");
    }
}
