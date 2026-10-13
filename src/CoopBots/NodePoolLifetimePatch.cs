using System.Reflection;
using System.Reflection.Emit;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Pooling;
using SignalDictionary = Godot.Collections.Dictionary;

namespace CoopBots;

// Ported from CombatSolver 7129c6f, NodePoolSignalLifetimePatch (MIT).
// Upstream registers this in its mod initializer. Our embedded kernel excludes
// that initializer, so the protection must be installed by the host's PatchAll.
// Keep the original disconnect rules and pool bookkeeping; only temporary native
// wrappers are owned here. This addresses a finalizer-risk path, not a proven
// reproduction of the 2026-10-04 access violation (no crash dump was retained).
[HarmonyPatch(typeof(NodePool<NCard>), "DisconnectIncomingAndOutgoingSignals", [typeof(Node)])]
internal static class NodePoolLifetimePatch
{
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        foreach (string method in new[] { nameof(Node.GetSignalList), nameof(Node.GetSignalConnectionList), nameof(Node.GetIncomingConnections) })
            if (code.Count(i => i.operand is MethodInfo m && m.Name == method) != 1)
                throw new InvalidOperationException("Node pool signal traversal changed: " + method);
        return [new(OpCodes.Ldarg_1), new(OpCodes.Call, AccessTools.Method(typeof(NodePoolLifetimePatch), nameof(Disconnect))), new(OpCodes.Ret)];
    }

    // Defer native Variant creation until a real traversal. Installing the patch
    // and checking its IL must not need an initialized Godot native host.
    private static class Keys
    {
        internal static readonly Variant Name = Variant.CreateFrom("name");
        internal static readonly Variant Callable = Variant.CreateFrom("callable");
        internal static readonly Variant Signal = Variant.CreateFrom("signal");
    }

    internal static void Disconnect(Node node)
    {
        var signals = node.GetSignalList();
        using ((Godot.Collections.Array)signals)
        {
            foreach (SignalDictionary signal in signals)
            {
                using (signal)
                using (Variant nameValue = signal[Keys.Name])
                using (StringName name = nameValue.AsStringName())
                    DisconnectConnections(node.GetSignalConnectionList(name));
            }
        }
        DisconnectConnections(node.GetIncomingConnections());
        for (int i = 0; i < node.GetChildCount(); i++)
            Disconnect(node.GetChild(i));
    }

    private static void DisconnectConnections(Godot.Collections.Array<SignalDictionary> connections)
    {
        using var ownedArray = (Godot.Collections.Array)connections;
        foreach (SignalDictionary connection in connections)
        {
            using (connection)
            using (Variant callableValue = connection[Keys.Callable])
            using (Variant signalValue = connection[Keys.Signal])
            {
                Callable callable = callableValue.AsCallable();
                Signal signal = signalValue.AsSignal();
                // AsCallable/AsSignal marshal new owned StringName wrappers. The delegate
                // callable has no method name; neither target nor owner belongs to this scope.
                using (callable.Method)
                using (signal.Name)
                    DisconnectSignal(callable, signal);
            }
        }
    }

    private static void DisconnectSignal(Callable callable, Signal signal)
    {
        GodotObject target = callable.Target;
        if ((target == null && callable.Method == null && callable.Delegate == null)
            || (target != null && !GodotObject.IsInstanceValid(target))) return;
        Node? targetNode = target as Node;
        if (targetNode != null && !targetNode.IsInsideTree()) return;
        GodotObject owner = signal.Owner;
        if (GodotObject.IsInstanceValid(owner))
        {
            Node? ownerNode = owner as Node;
            if (targetNode != null && targetNode.HasSignal(signal.Name) && targetNode.IsConnected(signal.Name, callable))
                targetNode.Disconnect(signal.Name, callable);
            else if (ownerNode != null && ownerNode.HasSignal(signal.Name) && ownerNode.IsConnected(signal.Name, callable))
                ownerNode.Disconnect(signal.Name, callable);
        }
    }
}
