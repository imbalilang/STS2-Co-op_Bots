using CoopBots.Kernel;

// Existing replay fixtures hand-constructed EndTurn actions before automatic
// next-turn answers became an explicit action contract. Record only their real
// End transitions; original card keys/choices and business assertions stay intact.
internal static class TurnStartFixtureCapture
{
    internal static KernelTeamSearch.Action[] Record(KernelSession root,
        IEnumerable<KernelTeamSearch.Action> inputs, int maxRounds = 80, bool allowInvalidSuffix = false)
    {
        var recorded = inputs.ToArray();
        var state = root.ForkForReplay();
        for (var index = 0; index < recorded.Length; index++)
        {
            if (state.HasTerminal || state.EnemyPhaseCompleted) break;
            var action = recorded[index];
            string boundary;
            if (action.EndTurn)
            {
                if (!state.EndTurn(action.Player, maxRounds, out boundary, out var resolved))
                    throw new InvalidOperationException($"Turn-start fixture recording refused End step={index}: {boundary}");
                if (resolved.TurnStartChoices is not { Complete: true })
                    throw new InvalidOperationException($"Turn-start fixture recording produced incomplete End step={index}: {boundary}");
                recorded[index] = action with { TurnStartChoices = resolved.TurnStartChoices };
            }
            else if (!state.ReplayPlanned(action, maxRounds, out boundary))
            {
                // ActTwoPriority intentionally supplies a missing-card suffix.
                // Keep that original bad input after recording its valid boundary.
                if (allowInvalidSuffix) break;
                throw new InvalidOperationException($"Turn-start fixture recording refused original input step={index}: {boundary}");
            }
        }
        return recorded;
    }
}
