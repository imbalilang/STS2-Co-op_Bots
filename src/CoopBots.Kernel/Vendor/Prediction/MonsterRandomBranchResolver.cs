using MegaCrit.Sts2.Core.MonsterMoves;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Random;

namespace CoopBots.Kernel.Vendor;

internal static class MonsterRandomBranchResolver
{
    public static string Pick(
        MonsterMoveStateMachine machine,
        RandomBranchState branch,
        IReadOnlyList<string> stateLog,
        Rng rng,
        Func<RandomBranchState.StateWeight, float>? baseWeight = null)
    {
        List<(RandomBranchState.StateWeight State, float Weight)> weighted = new(branch.States.Count);
        float total = 0f;
        foreach (RandomBranchState.StateWeight state in branch.States)
        {
            float weight = RepeatWeight(machine, state, stateLog)
                * (baseWeight?.Invoke(state) ?? state.GetWeight());
            weighted.Add((state, weight));
            total += weight;
        }

        // Vanilla intentionally still calls NextFloat(0). Its selection loop then returns the
        // first branch because the zero roll remains <= 0 after subtracting a zero weight.
        float roll = rng.NextFloat(total);
        foreach ((RandomBranchState.StateWeight state, float weight) in weighted)
        {
            roll -= weight;
            if (roll <= 0f)
                return state.stateId;
        }

        return weighted.LastOrDefault(item => item.Weight > 0f).State.stateId
            ?? throw new InvalidOperationException($"No valid state found in RandomBranchState {branch.Id}!");
    }

    private static float RepeatWeight(
        MonsterMoveStateMachine machine,
        RandomBranchState.StateWeight state,
        IReadOnlyList<string> stateLog)
    {
        if (state.repeatType == MoveRepeatType.UseOnlyOnce && stateLog.Contains(state.stateId))
            return 0f;

        if (state.repeatType == MoveRepeatType.CannotRepeat && stateLog.LastOrDefault() == state.stateId)
            return 0f;

        if (state.repeatType == MoveRepeatType.CanRepeatXTimes)
        {
            int consecutive = 0;
            for (int index = stateLog.Count - 1;
                 index >= 0 && stateLog[index] == state.stateId;
                 index--)
            {
                consecutive++;
            }
            if (consecutive >= state.maxTimes)
                return 0f;
        }

        if (state.cooldown > 0
            && stateLog
                .Where(id => machine.States.TryGetValue(id, out MonsterState? logged) && logged.IsMove)
                .Reverse()
                .Take(state.cooldown)
                .Contains(state.stateId))
        {
            return 0f;
        }

        return 1f;
    }
}
