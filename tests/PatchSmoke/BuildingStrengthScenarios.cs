using CoopBots.Building;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class BuildingStrengthScenarios
{
    // R2 2026-09-27, each mutation exit 1:
    // defence: Four starter Defends must still leave room for a defence-quality upgrade: block,engine,needs-draw
    // netdraw: Prepared draw/discard must not count as Backflip's hand advantage
    // drawgap: Prepared must not satisfy the deck's need for hand advantage: block,engine
    internal static void Run()
    {
        TemporalResourceScenarios.Run();
        CounterfactualDraftScenarios.Run();
        var player = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        player.MaxEnergy = 3;
        var combat = new CombatState(runState: RunState.CreateForTest([player], seed: "BUILD-STRENGTH"));
        player.ResetCombatState(); combat.AddPlayer(player);
        foreach (var card in player.Deck.Cards.ToArray()) player.Deck.RemoveInternal(card);
        for (var i = 0; i < 6; i++) player.Deck.AddInternal(combat.CreateCard<StrikeIronclad>(player));
        for (var i = 0; i < 4; i++) player.Deck.AddInternal(combat.CreateCard<DefendIronclad>(player));
        var shrug = combat.CreateCard<ShrugItOff>(player);
        var weak = BuildValue.Marginal(shrug, player);
        if (!weak.Reason.Contains("defence-quality"))
            throw new Exception("Four starter Defends must still leave room for a defence-quality upgrade: " + weak.Reason);
        var prepared = combat.CreateCard<Prepared>(player);
        prepared.UpgradeInternal(); prepared.FinalizeUpgradeInternal();
        var backflip = combat.CreateCard<Backflip>(player);
        if (CardProfile.NetDraw(prepared, CardProfile.Of(prepared)) != 0
            || CardProfile.NetDraw(backflip, CardProfile.Of(backflip)) <= 0)
            throw new Exception("Prepared draw/discard must not count as Backflip's hand advantage");
        player.Deck.AddInternal(prepared);
        var draw = BuildValue.Marginal(backflip, player);
        if (!draw.Reason.Contains("needs-hand-advantage"))
            throw new Exception("Prepared must not satisfy the deck's need for hand advantage: " + draw.Reason);
        // R2 v2 (all exit 1): Net draw must relieve addition size pressure in a Prepared-heavy deck
        // Defence-quality improvement must count as fit in the final addition scorer
        // Defence-quality improvement must reduce addition size pressure
        for (var i = 0; i < 9; i++) player.Deck.AddInternal(combat.CreateCard<Prepared>(player));
        var offeredDraw = BuildValue.AddDetailed(backflip, player);
        if (!offeredDraw.LegacyReason.Contains("relief:1.00"))
            throw new Exception("Net draw must relieve addition size pressure in a Prepared-heavy deck: " + offeredDraw.LegacyReason);
        var offeredBlock = BuildValue.AddDetailed(combat.CreateCard<FlameBarrier>(player), player);
        if (!offeredBlock.DeckFit)
            throw new Exception("Defence-quality improvement must count as fit in the final addition scorer: " + offeredBlock.LegacyReason);
        if (!offeredBlock.LegacyReason.Contains("relief:"))
            throw new Exception("Defence-quality improvement must reduce addition size pressure: " + offeredBlock.LegacyReason);
        Console.WriteLine($"PASS: quality gaps reach final addition/skip terms (draw={offeredDraw.Total:F2}, block={offeredBlock.Total:F2}).");
        Console.WriteLine("PASS: defence quality and net hand advantage reach the building scorer.");
    }
}
