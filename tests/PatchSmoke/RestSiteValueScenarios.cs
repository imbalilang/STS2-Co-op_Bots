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
        owner.Deck.AddInternal(run.CreateCard<StrikeIronclad>(owner));
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
    }
}

// Production dispatch identifies MendRestSiteOption's stack frame. This frame
// exercises that branch without creating Godot's targeting/animation nodes.
internal static class MendRestSiteOptionFixture
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static PlayerChoiceResult RemoteChoice(Player player) => BotRewardDriver.ChoiceForWait(player);
}
