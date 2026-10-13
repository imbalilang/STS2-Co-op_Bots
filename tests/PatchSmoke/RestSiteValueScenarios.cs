using System.Reflection;
using System.Runtime.CompilerServices;
using CoopBots;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class RestSiteValueScenarios
{
    internal static void Run()
    {
        // R2 2026-09-27: nine production mutations exit 1, then restore.
        // Commands/full logs: work/strength-20260927-v8e/r2-results.json.
        // MEND must compare useful native healing, not only the lowest HP percentage.
        // Camp option valuation must choose the same best MEND patient.
        // The actual remote MEND answer must match the patient used by valuation.
        // Tea Set room-entry energy must not bias the camp toward HEAL.
        // Fake Tea Set room-entry energy must not bias the camp toward HEAL.
        // Dream Catcher must retain its actual self-heal reward.
        // Independent self-heal reward relics must add their benefits.
        // Stone Humidifier max-HP growth must be counted for self-healing.
        // Stone Humidifier max-HP growth must also be counted when receiving MEND.
        var owner = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 9));
        var pillow = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 2, 9));
        var lower = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 3, 9));
        var run = RunState.CreateForTest(new[] { owner, pillow, lower }, seed: "CAMP-TARGET-VALUE");
        foreach (var p in run.Players) { p.ResetCombatState(); p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80); }
        pillow.Creature.SetCurrentHpInternal(25); lower.Creature.SetCurrentHpInternal(10);
        pillow.AddRelicInternal(ModelDb.Relic<RegalPillow>().ToMutable());
        // Use an ordinary deck so this patient-selection fixture is not also
        // a one-card-deck upgrade policy test.
        for (var i = 0; i < 5; i++) owner.Deck.AddInternal(run.CreateCard<StrikeIronclad>(owner));
        for (var i = 0; i < 4; i++) owner.Deck.AddInternal(run.CreateCard<DefendIronclad>(owner));
        var choose = typeof(BotRestSitePatch).GetMethod("Choose", BindingFlags.Static | BindingFlags.NonPublic)!;
        object Plan() => choose.Invoke(null, new object[] { owner, new RestSiteOption[]
            { new SmithRestSiteOption(owner), new HealRestSiteOption(owner), new MendRestSiteOption(owner) } })!;
        if (BotRestSitePatch.BestMendTarget(owner) != pillow)
            throw new Exception("MEND must compare useful native healing, not only the lowest HP percentage.");
        var plan = Plan();
        if ((int)plan.GetType().GetProperty("Index")!.GetValue(plan)! != 2
            || (ulong)plan.GetType().GetProperty("Patient")!.GetValue(plan)! != pillow.NetId)
            throw new Exception("Camp option valuation must choose the same best MEND patient.");
        if (MendRestSiteOptionFixture.RemoteChoice(owner).AsPlayerId() != pillow.NetId)
            throw new Exception("The actual remote MEND answer must match the patient used by valuation.");
        Console.WriteLine("PASS: camp valuation and remote MEND execution share the target with the best useful heal.");

        owner.AddRelicInternal(ModelDb.Relic<VenerableTeaSet>().ToMutable());
        if (BotRestSitePatch.SelfHealRewardValue(owner) != 0)
            throw new Exception("Tea Set room-entry energy must not bias the camp toward HEAL.");
        owner.AddRelicInternal(ModelDb.Relic<FakeVenerableTeaSet>().ToMutable());
        if (BotRestSitePatch.SelfHealRewardValue(owner) != 0)
            throw new Exception("Fake Tea Set room-entry energy must not bias the camp toward HEAL.");
        owner.AddRelicInternal(ModelDb.Relic<DreamCatcher>().ToMutable());
        if (BotRestSitePatch.SelfHealRewardValue(owner) != 12)
            throw new Exception("Dream Catcher must retain its actual self-heal reward.");
        owner.AddRelicInternal(ModelDb.Relic<TinyMailbox>().ToMutable());
        if (BotRestSitePatch.SelfHealRewardValue(owner) != 24)
            throw new Exception("Independent self-heal reward relics must add their benefits.");
        Console.WriteLine("PASS: entry relics do not bias HEAL; actual self-heal rewards remain additive.");

        owner.AddRelicInternal(ModelDb.Relic<StoneHumidifier>().ToMutable());
        if (BotRestSitePatch.SelfHealRewardValue(owner) != 34)
            throw new Exception("Stone Humidifier max-HP growth must be counted for self-healing.");
        lower.AddRelicInternal(ModelDb.Relic<StoneHumidifier>().ToMutable());
        pillow.Creature.SetCurrentHpInternal(80); lower.Creature.SetCurrentHpInternal(80);
        if (BotRestSitePatch.BestMendTarget(owner) != lower)
            throw new Exception("Stone Humidifier max-HP growth must also be counted when receiving MEND.");
        Console.WriteLine("PASS: shared after-heal max-HP growth applies to self-heal and MEND recipients.");

        // V37 (2026-09-30): a seat one hit from death, and the last camp before the act
        // boss, must both be worth more than the linear need curve said. Live case that
        // wiped the run: B3 at hp=7/62 (11%) chose SMITH over HEAL before AEONGLASS_BOSS
        // (HEAL:33.1 vs SMITH:47.3) and died in round 1 at 9 HP.
        //
        // 5/100: current 0.05 -> x1.857142857...; 50/100 sits above the band -> x1.
        if (Math.Abs(BotRestSitePatch.SurvivalPressure(5, 100) - 1.8571428571428572) > 0.0001)
            throw new Exception($"A near-death seat must carry the critical-HP multiplier "
                + $"(got {BotRestSitePatch.SurvivalPressure(5, 100):F4}).");
        if (Math.Abs(BotRestSitePatch.SurvivalPressure(50, 100) - 1.0) > 0.0001)
            throw new Exception("A mid-HP seat must be priced exactly as before the critical-HP band.");
        if (!(BotRestSitePatch.SurvivalPressure(7, 62) > BotRestSitePatch.SurvivalPressure(20, 62)))
            throw new Exception("The survival pressure must fall as the bar fills.");
        // The linear need curve itself is unchanged: 10 HP at 50/100 is exactly as before.
        if (Math.Abs(BotRestSitePatch.HealValue(10, 50, 100, 1.0) - 19.0) > 0.01)
            throw new Exception("The HEAL/MEND need curve must not move outside the critical band.");
        if (!(BotRestSitePatch.HealValue(30, 7, 62, 1.0) > BotRestSitePatch.HealValue(30, 20, 62, 1.0)))
            throw new Exception("Healing must still be worth more the closer the seat is to death.");
        // The last camp before the boss multiplies the recovery weight — but only for a
        // target that still needs the healing (below 75% HP).
        var normalWeight = BotRestSitePatch.CampHealWeight(owner, lastCampBeforeBoss: false, targetHpRatio: 0.5);
        var lastWeight = BotRestSitePatch.CampHealWeight(owner, lastCampBeforeBoss: true, targetHpRatio: 0.5);
        if (Math.Abs(lastWeight - normalWeight * 1.5) > 0.0001)
            throw new Exception($"The last camp before a boss must carry a 1.5x recovery weight for a hurt target "
                + $"({normalWeight:F4} -> {lastWeight:F4}).");
        // A nearly-full target is not "in need", so the bonus must not apply: measured
        // live 2026-10-01, a MEND for a teammate missing 10 of 24 outscored a real smith.
        var healthyLast = BotRestSitePatch.CampHealWeight(owner, lastCampBeforeBoss: true, targetHpRatio: 0.875);
        if (Math.Abs(healthyLast - normalWeight) > 0.0001)
            throw new Exception($"The last-camp bonus must NOT apply to a target at 87.5% HP "
                + $"({normalWeight:F4} vs {healthyLast:F4}).");
        Console.WriteLine("PASS: near-death HP and the last camp before a boss both raise the recovery weight.");
    }
}

// Production dispatch identifies MendRestSiteOption's stack frame. This frame
// exercises that branch without creating Godot's targeting/animation nodes.
internal static class MendRestSiteOptionFixture
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static PlayerChoiceResult RemoteChoice(Player player) => BotRewardDriver.ChoiceForWait(player);
}
