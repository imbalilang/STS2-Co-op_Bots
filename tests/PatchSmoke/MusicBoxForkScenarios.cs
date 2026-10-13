using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Hooks.Card;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Relics;

// D7 (2026-09-30): the Music Box prediction state must stay forkable.
//
// Upstream refuses to fork it while a card play is in flight. In a single-player search
// that refusal only makes one prediction conservative; in the live four-seat A10 run it
// escaped `KernelCombatPlanner.Choose`, the whole tick was declined and the legacy planner
// took over — six consecutive round-1 decisions of AEONGLASS_BOSS (the fight that wiped
// the run), all of them on the two seats holding the relic.
internal static class MusicBoxForkScenarios
{
    internal static void Run()
    {
        TestEnvironment.Ensure();
        var state = new MusicBoxPredictionState((MusicBox)ModelDb.Relic<MusicBox>().ToMutable());

        // Mid card play: exactly the state upstream throws on.
        state.CardBeingPlayed = ModelDb.Card<StrikeIronclad>();
        try
        {
            state.AssertForkable();
        }
        catch (Exception error)
        {
            throw new Exception("D7: Music Box must stay forkable, but AssertForkable threw: "
                + error.Message);
        }

        // Still a fork boundary on purpose: that interface is what keeps this state out of
        // the manual card-choice continuation path. Only the fork refusal was lifted.
        if (state is not IPredictionForkBoundary)
            throw new Exception("D7: MusicBoxPredictionState must keep IPredictionForkBoundary.");

        // A fork taken mid-play has to carry this turn's "already used" flag.
        state.WasUsedThisTurn = true;
        var fork = (MusicBoxPredictionState)state.Fork(null!);
        if (!fork.WasUsedThisTurn)
            throw new Exception("D7: a Music Box fork must carry WasUsedThisTurn.");
        Console.WriteLine("PASS: Music Box prediction state forks instead of refusing (D7).");
    }
}
