using System.Reflection;
using System.Reflection.Emit;
using CoopBots;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Pooling;

internal static class NativeLifetimeScenarios
{
    internal static void Run()
    {
        // This console host cannot reproduce a native Godot access violation.
        // Guard the production wiring and deterministic cleanup contract instead.
        // R2 (2026-10-04), remove the HarmonyPatch attribute, exit 1:
        // "System.InvalidOperationException: native pool cleanup must install the owned-wrapper traversal in the host"
        // Remove the signals-array using, exit 1:
        // "System.InvalidOperationException: native Disconnect must dispose its owned array in finally"
        var target = AccessTools.Method(typeof(NodePool<NCard>), "DisconnectIncomingAndOutgoingSignals");
        var replacement = AccessTools.Method(typeof(NodePoolLifetimePatch), nameof(NodePoolLifetimePatch.Disconnect));
        Check(Harmony.GetPatchInfo(target)?.Transpilers.Any(p =>
                p.PatchMethod.DeclaringType == typeof(NodePoolLifetimePatch)) == true
            && PatchProcessor.GetCurrentInstructions(target).Any(i => i.Calls(replacement)),
            "native pool cleanup must install the owned-wrapper traversal in the host");

        // No native wrappers may be initialized merely by installing the patch.
        Check(typeof(NodePoolLifetimePatch).TypeInitializer is null,
            "native keys must be deferred until a live traversal");
        foreach (var name in new[] { "Disconnect", "DisconnectConnections" })
        {
            var method = AccessTools.Method(typeof(NodePoolLifetimePatch), name);
            var code = PatchProcessor.GetOriginalInstructions(method);
            var disposed = DisposedLocalTypes(method, code);
            Check(method.GetMethodBody()!.ExceptionHandlingClauses.Any(c =>
                    c.Flags == ExceptionHandlingClauseOptions.Finally)
                && disposed.Contains(typeof(Godot.Collections.Array)),
                $"native {name} must dispose its owned array in finally");
            Check(disposed.Contains(typeof(Godot.Collections.Dictionary))
                && disposed.Contains(typeof(Variant)),
                $"native {name} must also release dictionary and Variant wrappers");
        }
        // Refuse a changed upstream method instead of silently replacing unknown
        // semantics. This deliberately malformed input is part of the contract.
        var original = PatchProcessor.GetOriginalInstructions(target);
        var changed = original.Where(i => i.operand is not MethodInfo m
            || m.Name != nameof(Node.GetIncomingConnections)).ToArray();
        bool rejected = false;
        try { _ = NodePoolLifetimePatch.Transpiler(changed).ToArray(); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "native pool patch must reject a changed traversal contract");
        Console.WriteLine("PASS: native pool cleanup is wired with scoped array/dictionary/Variant ownership.");

        // Include generated iterator/local-function bodies: Descendants and the
        // lobby Refresh callback used to hide allocations in those bodies.
        // R2 restore the old intent GetChildren loop, exit 1:
        // "System.InvalidOperationException: Bot UI traversals must not leave GetChildren arrays to finalizers: CoopBots.BotCooperation.IntentBubbles"
        var getChildren = AccessTools.Method(typeof(Node), nameof(Node.GetChildren), [typeof(bool)]);
        var roots = new[] { typeof(BotCooperation), typeof(BotRewardsScreenDriver), typeof(DraggablePanel), typeof(LobbyUi) };
        var allocating = typeof(ModEntry).Assembly.GetTypes()
            .Where(t => roots.Any(root => t == root || t.FullName!.StartsWith(root.FullName + "+")))
            .Select(t => t.IsGenericTypeDefinition ? t.MakeGenericType(typeof(Node)) : t)
            .SelectMany(t =>
                t.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Select(m => m.IsGenericMethodDefinition ? m.MakeGenericMethod(typeof(Node)) : m)
            .Where(m => m.GetMethodBody() is not null
                && PatchProcessor.GetOriginalInstructions(m).Any(i => i.Calls(getChildren)))
            .Select(m => m.DeclaringType!.FullName + "." + m.Name).ToArray();
        Check(allocating.Length == 0,
            "Bot UI traversals must not leave GetChildren arrays to finalizers: " + string.Join(",", allocating));
        Console.WriteLine("PASS: Bot UI traversals allocate no temporary GetChildren arrays.");
    }

    private static HashSet<Type> DisposedLocalTypes(MethodInfo method, List<CodeInstruction> code)
    {
        var types = new HashSet<Type>();
        var locals = method.GetMethodBody()!.LocalVariables;
        for (var i = 1; i < code.Count; i++)
        {
            if (code[i].operand is not MethodInfo called || called.Name != "Dispose") continue;
            var load = code[i - 1];
            if (load.opcode == OpCodes.Constrained && load.operand is Type constrained)
            {
                types.Add(constrained);
                continue;
            }
            var index = load.opcode == OpCodes.Ldloc_0 ? 0 : load.opcode == OpCodes.Ldloc_1 ? 1
                : load.opcode == OpCodes.Ldloc_2 ? 2 : load.opcode == OpCodes.Ldloc_3 ? 3
                : load.operand is LocalVariableInfo local ? local.LocalIndex : -1;
            if (index >= 0) types.Add(locals[index].LocalType);
        }
        return types;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
