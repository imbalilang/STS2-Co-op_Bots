using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;

namespace CoopBots;

// SmartFormat 3.3 keeps the active input/result in Parser instance fields even
// with IsThreadSafeMode enabled (that setting protects its pools). Two calls can
// return the same Format, which SmartFormatter then returns to the pool twice.
// Serialize only parsing, before custom formatters run, on each actual Parser.
[HarmonyPatch]
internal static class SmartFormatParserConcurrencyPatch
{
    private static readonly ConditionalWeakTable<object, object> Gates = new();

    internal static MethodBase TargetMethod()
    {
        // Resolve through the native field's type, retaining the game's actual
        // assembly identity rather than loading another copy of SmartFormat.
        var formatter = typeof(LocManager).GetField("_smartFormatter",
            BindingFlags.NonPublic | BindingFlags.Static)?.FieldType
            ?? throw new MissingFieldException(typeof(LocManager).FullName, "_smartFormatter");
        var parser = formatter.GetProperty("Parser", BindingFlags.Public | BindingFlags.Instance)?.PropertyType
            ?? throw new MissingMemberException(formatter.FullName, "Parser");
        return parser.GetMethod("ParseFormat", BindingFlags.Public | BindingFlags.Instance,
            binder: null, types: [typeof(string)], modifiers: null)
            ?? throw new MissingMethodException(parser.FullName, "ParseFormat(string)");
    }

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    internal static void Prefix(object __instance, out object? __state)
    {
        __state = null;
        var gate = Gates.GetValue(__instance, static _ => new object());
        Monitor.Enter(gate);
        __state = gate;
    }

    [HarmonyFinalizer]
    [HarmonyPriority(Priority.Last)]
    internal static Exception? Finalizer(Exception? __exception, object? __state)
    {
        if (__state != null) Monitor.Exit(__state);
        return __exception;
    }
}
