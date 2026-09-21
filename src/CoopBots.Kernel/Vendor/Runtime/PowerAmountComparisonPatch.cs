using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;
using STS2RitsuLib.Patching.Models;

namespace CoopBots.Kernel.Vendor;

// The native routine boxes constants for two same-enum Equals calls. Replacing only
// those comparisons keeps virtual getters, decimal tests and their order intact.
internal sealed class PowerAmountComparisonPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_power_amount_enum_comparisons";
    public static string Description => "省去 Power 类型判断中的枚举装箱";
    public static bool IsCritical => false;
    internal static int RewrittenComparisons { get; private set; }

    public static ModPatchTarget[] GetTargets()
        => [new(typeof(PowerModel), nameof(PowerModel.GetTypeForAmount), [typeof(decimal)])];

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        CodeInstruction[] code = instructions.ToArray();
        RewrittenComparisons = 0;
        if (Enum.GetUnderlyingType(typeof(PowerStackType)) != typeof(int)
            || Enum.GetUnderlyingType(typeof(PowerType)) != typeof(int))
            return code;

        MethodInfo equals = typeof(object).GetMethod(nameof(Equals), [typeof(object)])!;
        List<int> matches = [];
        for (int index = 0; index + 4 < code.Length; index++)
        {
            if (code[index].opcode != OpCodes.Ldloca && code[index].opcode != OpCodes.Ldloca_S)
                continue;
            Type? enumType = code[index + 2].operand as Type;
            bool expectedConstant = enumType == typeof(PowerStackType)
                ? code[index + 1].opcode == OpCodes.Ldc_I4_1
                : enumType == typeof(PowerType) && code[index + 1].opcode == OpCodes.Ldc_I4_2;
            if (!expectedConstant || code[index + 2].opcode != OpCodes.Box
                || code[index + 3].opcode != OpCodes.Constrained
                || !Equals(code[index + 3].operand, enumType)
                || !code[index + 4].Calls(equals)
                || code[index + 4].opcode != OpCodes.Callvirt)
                continue;
            // A branch or exception boundary inside the sequence could have a different
            // incoming stack. Unknown rewritten bodies retain their original IL.
            if (Enumerable.Range(index + 1, 4).Any(i => code[i].labels.Count != 0 || code[i].blocks.Count != 0))
                return code;
            matches.Add(index);
        }
        if (matches.Count != 2
            || code[matches[0] + 2].operand as Type != typeof(PowerStackType)
            || code[matches[1] + 2].operand as Type != typeof(PowerType))
            return code;

        CodeInstruction[] result = code.Select(instruction => new CodeInstruction(instruction)).ToArray();
        foreach (int index in matches)
        {
            result[index].opcode = OpCodes.Ldloc;
            result[index + 2].opcode = OpCodes.Nop;
            result[index + 2].operand = null;
            result[index + 3].opcode = OpCodes.Nop;
            result[index + 3].operand = null;
            result[index + 4].opcode = OpCodes.Ceq;
            result[index + 4].operand = null;
        }
        RewrittenComparisons = matches.Count;
        return result;
    }
}
