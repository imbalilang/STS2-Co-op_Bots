using CoopBots;
using CoopBots.Building;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Live 2026-10-04: three seats took Pael's Wing at the Pael event, but the seat
// never used it — the card reward was answered with a card or a plain skip, and
// the SACRIFICE alternative was never selected. Owner request: with the Wing,
// replace skip with sacrifice and raise the take bar so marginal cards are
// skipped (and sacrificed) more often.
internal static class PaelWingScenarios
{
    private static void Check(bool ok, string why) { if (!ok) throw new Exception("Pael's Wing: " + why); }

    internal static void Run()
    {
        var player = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 71, 0));
        var run = RunState.CreateForTest([player], seed: "PAEL-WING");
        player.ResetCombatState();
        for (var i = 0; i < 10; i++) player.Deck.AddInternal(run.CreateCard<StrikeSilent>(player));
        for (var i = 0; i < 10; i++) player.Deck.AddInternal(run.CreateCard<DefendSilent>(player));

        var barWithout = BuildValue.RewardTakeBar(player);
        player.AddRelicInternal(ModelDb.Relic<PaelsWing>().ToMutable());
        var barWith = BuildValue.RewardTakeBar(player);
        Check(barWith >= barWithout + BuildValue.PaelsWingTakeBarBonus - 1e-9,
            $"holding the Wing must raise the reward take bar ({barWithout:F2}->{barWith:F2})");

        var decision = BotBrain.ChooseCardRewardDecision(player,
            [run.CreateCard<DefendSilent>(player)], [PaelsWing.sacrificeAlternativeKey]);
        Check(decision.Index < 0 && decision.AlternativeId is not null,
            $"a skipped reward with the Wing must become SACRIFICE (index={decision.Index})");

        var plain = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 72, 0));
        var plainRun = RunState.CreateForTest([plain], seed: "PAEL-WING-PLAIN");
        for (var i = 0; i < 20; i++) plain.Deck.AddInternal(plainRun.CreateCard<StrikeSilent>(plain));
        var plainDecision = BotBrain.ChooseCardRewardDecision(plain,
            [plainRun.CreateCard<StrikeSilent>(plain)], [PaelsWing.sacrificeAlternativeKey]);
        Check(plainDecision.AlternativeId is null,
            "without the Wing a skip must stay a plain skip, never a sacrifice");
        Console.WriteLine($"PASS: Pael's Wing raises the take bar ({barWithout:F2}->{barWith:F2}) and a skipped reward becomes SACRIFICE.");
    }
}
