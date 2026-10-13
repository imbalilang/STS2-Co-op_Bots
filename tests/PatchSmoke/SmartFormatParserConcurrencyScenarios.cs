using System.Reflection;
using System.Runtime.CompilerServices;
using CoopBots;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using SmartFormat;
using SmartFormat.Core.Parsing;
using SmartFormat.Core.Settings;

// Real installed Parser and game diff formatter; no TestEnvironment/blank text.
// Actual private RED1/exact restore GREEN0 for entry, exception release,
// per-instance key and registration: smartformat-live-hang-readonly/library-guard-r2-*.
// Full host gates and saved-entry Steam qualification remain separate.
internal static class SmartFormatParserConcurrencyScenarios
{
    private const string Template = "获得{Block:diff()}点[gold]格挡[/gold]。\n丢弃1张牌。";
    private static Parser? heldParser;
    private static int heldThread;
    private static ManualResetEventSlim? entered;
    private static ManualResetEventSlim? release;

    internal static void Run(string? caseName = null, bool installGuard = true)
    {
        if (caseName is not (null or "serialization" or "exception" or "independent" or "native"))
            throw new ArgumentException("Unknown SmartFormat parser case: " + caseName);
        Check(SmartSettings.IsThreadSafeMode, "fixture must start in the native game's thread-safe pool mode");
        var target = SmartFormatParserConcurrencyPatch.TargetMethod();
        Check(target == typeof(Parser).GetMethod(nameof(Parser.ParseFormat), [typeof(string)]),
            "host patch must bind the actual native Parser type");
        Console.WriteLine($"DIAG SmartFormat target={target.DeclaringType!.FullName}.{target.Name} "
            + $"assembly={target.Module.Assembly.GetName().Version} mvid={target.Module.ModuleVersionId}");
        var guard = new Harmony("coopbots.smartformat-guard-test." + Guid.NewGuid().ToString("N"));
        if (installGuard) guard.CreateClassProcessor(typeof(SmartFormatParserConcurrencyPatch)).Patch();
        var patches = Harmony.GetPatchInfo(target);
        Check(patches?.Prefixes.Count(p => p.PatchMethod.DeclaringType == typeof(SmartFormatParserConcurrencyPatch)) == 1
            && patches.Finalizers.Count(p => p.PatchMethod.DeclaringType == typeof(SmartFormatParserConcurrencyPatch)) == 1,
            /* R2 RED: SmartFormat guard: host PatchAll must install exactly one production prefix and finalizer */
            "host PatchAll must install exactly one production prefix and finalizer");
        var observer = new Harmony(guard.Id + ".observer");
        observer.Patch(target, prefix: new HarmonyMethod(typeof(SmartFormatParserConcurrencyScenarios), nameof(HoldAfterGuard))
        { priority = Priority.Last });
        try
        {
            if (caseName is null or "serialization") SerializedCalls();
            if (caseName is null or "exception") NativeException();
            if (caseName is null or "independent") IndependentParsers();
            if (caseName is null or "native") NativeFormatting();
        }
        finally
        {
            heldParser = null;
            entered = null;
            release = null;
            observer.UnpatchAll(observer.Id);
            guard.UnpatchAll(guard.Id);
        }
    }

    private static void SerializedCalls()
    {
        var parser = new SmartFormatter().Parser;
        using var aEntered = new ManualResetEventSlim();
        using var aRelease = new ManualResetEventSlim();
        using var bStarted = new ManualResetEventSlim();
        heldParser = parser; entered = aEntered; release = aRelease;
        Format? a = null, b = null;
        Exception? aError = null, bError = null;
        var first = new Thread(() =>
        {
            heldThread = Environment.CurrentManagedThreadId;
            try { a = parser.ParseFormat(Template); } catch (Exception ex) { aError = ex; }
        }) { IsBackground = true };
        var second = new Thread(() =>
        {
            bStarted.Set();
            try { b = parser.ParseFormat(Template); } catch (Exception ex) { bError = ex; }
        }) { IsBackground = true };
        first.Start();
        var earlyCompletion = false;
        var settled = false;
        try
        {
            Check(aEntered.Wait(3000), "first actual Parse must enter the held body");
            second.Start();
            Check(bStarted.Wait(3000), "second actual Parse caller must start");
            earlyCompletion = second.Join(300);
        }
        finally { settled = ReleaseAndJoin(aRelease, first, second); }
        Check(settled,
            "serialized actual parses must settle after releasing the observer");
        heldParser = null;
        entered = null; release = null;
        Check(aError == null && bError == null && a != null && b != null,
            "both serialized native parses must retain successful results");
        Check(!earlyCompletion && !ReferenceEquals(a, b),
            /* R2 RED: SmartFormat guard: same-parser concurrent calls must serialize before native body entry and own distinct results */
            "same-parser concurrent calls must serialize before native body entry and own distinct results");
        CheckTree(a!); CheckTree(b!); a!.Dispose(); b!.Dispose();
        Console.WriteLine("PASS: SmartFormat parser serialization");
    }

    private static void NativeException()
    {
        var formatter = new SmartFormatter();
        formatter.Settings.Parser.ErrorAction = ParseErrorAction.ThrowError;
        var parser = formatter.Parser;
        Exception? error = null;
        try { parser.ParseFormat("{Block:diff()"); }
        catch (Exception ex) { error = ex; }
        Check(error != null && error.GetType().Assembly == typeof(Parser).Assembly,
            "native malformed input must retain its real parsing exception");
        Format? next = null;
        Exception? nextError = null;
        var second = new Thread(() =>
        {
            try { next = parser.ParseFormat(Template); } catch (Exception ex) { nextError = ex; }
        }) { IsBackground = true };
        second.Start();
        Check(second.Join(3000) && nextError == null && next != null,
            /* R2 RED: SmartFormat guard: native parsing exception must release the guard for a different thread */
            "native parsing exception must release the guard for a different thread");
        CheckTree(next!); next!.Dispose();
        Console.WriteLine($"DIAG SmartFormat native exception={error!.GetType().FullName}: {error.Message}");
        Console.WriteLine("PASS: SmartFormat parser exception");
    }

    private static void IndependentParsers()
    {
        var aParser = new SmartFormatter().Parser;
        var bParser = new SmartFormatter().Parser;
        using var aEntered = new ManualResetEventSlim();
        using var aRelease = new ManualResetEventSlim();
        heldParser = aParser; entered = aEntered; release = aRelease;
        Format? a = null, b = null;
        Exception? aError = null, bError = null;
        var first = new Thread(() =>
        {
            heldThread = Environment.CurrentManagedThreadId;
            try { a = aParser.ParseFormat(Template); } catch (Exception ex) { aError = ex; }
        }) { IsBackground = true };
        var second = new Thread(() =>
        {
            try { b = bParser.ParseFormat(Template); } catch (Exception ex) { bError = ex; }
        }) { IsBackground = true };
        first.Start();
        bool independent = false;
        var settled = false;
        try
        {
            Check(aEntered.Wait(3000), "first independent Parser must enter its held body");
            second.Start();
            independent = second.Join(3000);
        }
        finally { settled = ReleaseAndJoin(aRelease, first, second); }
        Check(settled, "independent parses must settle");
        heldParser = null;
        entered = null; release = null;
        Check(independent && aError == null && bError == null && a != null && b != null,
            /* R2 RED: SmartFormat guard: different Parser instances must not share a global guard */
            "different Parser instances must not share a global guard");
        CheckTree(a!); CheckTree(b!); a!.Dispose(); b!.Dispose();
        Console.WriteLine("PASS: SmartFormat parser independent");
    }

    private static void NativeFormatting()
    {
        var field = typeof(LocManager).GetField("_smartFormatter", BindingFlags.NonPublic | BindingFlags.Static)!;
        var old = field.GetValue(null);
        var oldDefault = Smart.Default;
        try
        {
            typeof(LocManager).GetMethod("LoadLocFormatters", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(RuntimeHelpers.GetUninitializedObject(typeof(LocManager)), null);
            var formatter = (SmartFormatter)field.GetValue(null)!;
            var blockType = typeof(LocManager).Assembly.GetType("MegaCrit.Sts2.Core.Localization.DynamicVars.BlockVar", true)!;
            var valueProp = typeof(LocManager).Assembly.GetType("MegaCrit.Sts2.Core.ValueProps.ValueProp", true)!;
            var block = blockType.GetConstructor([typeof(decimal), valueProp])!
                .Invoke([(object)8m, Enum.ToObject(valueProp, 0)]);
            for (var i = 0; i < 64; i++)
            {
                var output = formatter.Format(Template, (IList<object?>)new object?[]
                    { new Dictionary<string, object> { ["Block"] = block } });
                Check(output == "获得8点[gold]格挡[/gold]。\n丢弃1张牌。",
                    "guarded native diff formatter must preserve the actual Chinese Survivor text and Block value");
            }
        }
        finally { field.SetValue(null, old); Smart.Default = oldDefault; }
        Console.WriteLine("PASS: SmartFormat parser native");
    }

    private static void HoldAfterGuard(Parser __instance)
    {
        if (!ReferenceEquals(__instance, heldParser) || Environment.CurrentManagedThreadId != heldThread) return;
        entered!.Set();
        if (!release!.Wait(8000)) throw new InvalidOperationException("SmartFormat observer timed out");
    }

    private static bool ReleaseAndJoin(ManualResetEventSlim signal, params Thread[] threads)
    {
        signal.Set();
        var settled = true;
        foreach (var thread in threads)
            if ((thread.ThreadState & System.Threading.ThreadState.Unstarted) == 0)
                settled &= thread.Join(3000);
        heldParser = null; entered = null; release = null;
        return settled;
    }

    private static void CheckTree(Format root)
    {
        var seen = new HashSet<Format>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<Format>(); pending.Push(root);
        while (pending.TryPop(out var f))
        {
            Check(seen.Add(f), "actual parsed Format tree must be acyclic with unique ownership");
            foreach (var item in f.Items)
                if (item is Placeholder p)
                {
                    Check(ReferenceEquals(p.Parent, f), "actual parsed placeholder must retain its parent");
                    if (p.Format != null)
                    {
                        Check(ReferenceEquals(p.Format.ParentPlaceholder, p), "actual child format must retain its placeholder");
                        pending.Push(p.Format);
                    }
                }
        }
        Check(root.ParentPlaceholder == null, "actual Parse must return its root Format");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("SmartFormat guard: " + message);
    }
}
