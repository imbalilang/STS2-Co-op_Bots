using System.Reflection;
using CoopBots;
using CoopBots.Building;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Entities.RestSite;

internal static class ActOneFeedbackScenarios
{
    // R2 2026-09-30 outcome model: remove the subset chooser's Grand Finale
    // empty-pile condition -> exit 1; restored source -> exit 0.
    // System.Exception: Reported Grand Finale must not steal a camp upgrade from a usable block card.
    // work/deck-outcome-optimization/red-finale-play-window.log
    // R2 2026-09-30: removing hand-model Grand Finale availability exited 1.
    // System.Exception: Reported Grand Finale must not steal a camp upgrade from a usable block card.
    // work/unified-deck-strength/r2-additional.json; restored in final four gates.
    // R2 2026-09-28: 15 independent production mutations exited 1, all restored
    // byte-for-byte. Commands and raw output: work/act1-wipe-20260928/r2-results.json.
    // Reported Grand Finale must not steal a camp upgrade from a usable block card.
    // Reported deck must not value Grand Finale above its 148-gold shop price.
    // At 14/70 the actual boss camp must heal instead of upgrading the unusable Grand Finale.
    // A five-card Grand Finale deck must retain its empty-pile payoff.
    // Controllable draw must create a first-cycle Grand Finale window.
    // Reordering a deck list must not invent Grand Finale availability or upgrade payoff.
    // The hand-only model must not pretend Grand Finale is unconditional damage.
    // Every seat must use team health and avoid optional robots when an ally is wounded.
    // Healthy teams must retain the optional robot challenge.
    // A saved committed robot encounter must continue through its only FIGHT option.
    // Full belt must make room for Glowwater over a weaker temporary bottle.
    // Equal or weaker rewards must not churn the potion belt.
    // The synthetic reward driver must actually procure Glowwater after a native discard.
    // The local reward screen must keep a better full-belt potion pending.
    // A failed local replacement must not enqueue the same discard forever.
    // Forge must proactively upgrade two payable attacks with a full belt.
    // Forge must not invent attack upgrade value when no follow-up can be paid.
    private static readonly List<string> Failures = new();
    private static void Check(bool ok, string why) { if (!ok) Failures.Add(why); }
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static bool BossCamp(ref bool __result) { __result = true; return false; }
    internal static void Run()
    {
        Failures.Clear();
        new Harmony("coopbots.test.act-one-feedback").Patch(AccessTools.Method(typeof(LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(ActOneFeedbackScenarios), nameof(Format)));
        var party = Enumerable.Range(0, 4).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 38, i))).ToArray();
        var run = RunState.CreateForTest(party, seed: "ACT-ONE-FEEDBACK");
        var p = party[0];
        p.AddRelicInternal(ModelDb.Relic<MegaCrit.Sts2.Core.Models.Relics.RingOfTheSnake>().ToMutable());
        foreach (var member in party) { member.MaxEnergy = 3; member.Creature.SetMaxHpInternal(80); member.Creature.SetCurrentHpInternal(80); }
        CardModel Add<T>(bool upgraded = false) where T : CardModel, new()
        { var card = run.CreateCard<T>(p); if (upgraded) card.UpgradeInternal(); p.Deck.AddInternal(card); return card; }
        Add<AscendersBane>(); Add<CloakAndDagger>();
        for (var i = 0; i < 4; i++) Add<DefendSilent>();
        Add<EchoingSlash>(); Add<Footwork>(true); Add<Neutralize>(true); Add<Slice>();
        for (var i = 0; i < 4; i++) Add<StrikeSilent>();
        Add<Survivor>();
        var finale = run.CreateCard<GrandFinale>(p);
        var addition = BuildValue.Add(finale, p);
        var shopGold = (double)typeof(BotShopPlanner).GetMethod("CardGold", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [finale, p, null])!;
        p.Deck.AddInternal(finale);
        // The later cheese event adds two commons before the final camp.
        Add<Predator>(); Add<Slice>();
        var availability = GrandFinaleAvailability.Estimate(p.Deck.Cards, 3);
        var upgrade = BuildValue.UpgradeDelta(finale, p);
        var defend = BuildValue.UpgradeDelta(p.Deck.Cards.OfType<DefendSilent>().First(), p);
        Console.WriteLine($"PROBE reported Grand Finale availability={availability:F3} add={addition.Total:F2} shopGold={shopGold:F2} upgrade={upgrade.Total:F2} defend={defend.Total:F2}");
        Check(upgrade.Total < defend.Total, "Reported Grand Finale must not steal a camp upgrade from a usable block card.");
        Check(shopGold < 148, "Reported deck must not value Grand Finale above its 148-gold shop price.");
        var thin = new CardModel[] { finale, run.CreateCard<StrikeSilent>(p), run.CreateCard<DefendSilent>(p),
            run.CreateCard<DefendSilent>(p), run.CreateCard<StrikeSilent>(p) };
        Check(GrandFinaleAvailability.Estimate(thin, 3) == 1, "A five-card Grand Finale deck must retain its empty-pile payoff.");
        var drawDeck = Enumerable.Range(0, 8).Select(_ => (CardModel)run.CreateCard<Prepared>(p)).Append(finale).ToArray();
        Check(GrandFinaleAvailability.Estimate(drawDeck, 3) > 0,
            "Controllable draw must create a first-cycle Grand Finale window.");
        var plain = Enumerable.Range(0, 11).Select(_ => (CardModel)run.CreateCard<StrikeSilent>(p)).ToArray();
        Check(CounterfactualDraft.Compare(plain, finale, 3) is null,
            "The hand-only model must not pretend Grand Finale is unconditional damage.");
        var unordered = plain.Append(finale).ToArray();
        Check(Math.Abs(GrandFinaleAvailability.Estimate(unordered, 3) - GrandFinaleAvailability.Estimate(unordered.Reverse().ToArray(), 3)) < .00001,
            "Reordering a deck list must not invent Grand Finale availability or upgrade payoff.");
        var campHarmony = new Harmony("coopbots.test.act-one-camp");
        var lastCamp = AccessTools.Method(typeof(RunDepth), "LastCampBeforeBoss");
        campHarmony.Patch(lastCamp, prefix: new HarmonyMethod(typeof(ActOneFeedbackScenarios), nameof(BossCamp)));
        p.ResetCombatState(); p.Creature.SetMaxHpInternal(70); p.Creature.SetCurrentHpInternal(14);
        foreach (var ally in party.Skip(1)) ally.Creature.SetCurrentHpInternal(31);
        try
        {
            var campPlan = typeof(BotRestSitePatch).GetMethod("Choose", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [p, new RestSiteOption[] { new SmithRestSiteOption(p), new HealRestSiteOption(p) }])!;
            Check((int)campPlan.GetType().GetProperty("Index")!.GetValue(campPlan)! == 1,
                "At 14/70 the actual boss camp must heal instead of upgrading the unusable Grand Finale.");
        }
        finally { campHarmony.Unpatch(lastCamp, HarmonyPatchType.Prefix, campHarmony.Id); }
        foreach (var member in party) { member.Creature.SetMaxHpInternal(80); member.Creature.SetCurrentHpInternal(80); }
        Console.WriteLine("PASS: reported Grand Finale shop and upgrade value require real empty-pile windows; thin deck remains supported.");

        var punch = ModelDb.Event<PunchOff>().ToMutable();
        void Options(params string[] ids) => typeof(EventModel).GetField("_currentOptions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(punch, ids.Select(id => new EventOption(punch, () => Task.CompletedTask,
                new LocString("events", "test.title"), new LocString("events", "test.description"),
                "PUNCH_OFF.pages.INITIAL.options." + id, Array.Empty<MegaCrit.Sts2.Core.HoverTips.IHoverTip>())).ToList());
        var choose = typeof(BotEventDriver).GetMethod("ChooseEventOption", BindingFlags.Static | BindingFlags.NonPublic)!;
        int Choice(Player owner) => (int)choose.Invoke(null, [owner, punch])!;
        Options("I_CAN_TAKE_THEM", "NAB");
        party[3].Creature.SetCurrentHpInternal(40);
        Check(party.All(member => Choice(member) == 1), "Every seat must use team health and avoid optional robots when an ally is wounded.");
        party[3].Creature.SetCurrentHpInternal(80);
        Check(Choice(p) == 0, "Healthy teams must retain the optional robot challenge.");
        Options("FIGHT"); party[3].Creature.SetCurrentHpInternal(1);
        Check(Choice(p) == 0, "A saved committed robot encounter must continue through its only FIGHT option.");
        Console.WriteLine("PASS: PunchOff uses shared team reserve before commitment and continues an already committed fight.");

        var flex = ModelDb.Potion<FlexPotion>().ToMutable(); p.AddPotionInternal(flex, silent: true);
        var forge = ModelDb.Potion<BlessingOfTheForge>().ToMutable(); p.AddPotionInternal(forge, silent: true);
        while (p.Potions.Count() < p.MaxPotionCount) p.AddPotionInternal(ModelDb.Potion<FairyInABottle>().ToMutable(), silent: true);
        var glow = ModelDb.Potion<GlowwaterPotion>().ToMutable();
        Check(BotRewardsScreenDriver.CanTakeReward(new PotionReward(glow, p), p, false),
            "The local reward screen must keep a better full-belt potion pending.");
        Check(!BotRewardsScreenDriver.CanTakeReward(new PotionReward(glow, p), p, true),
            "A failed local replacement must not enqueue the same discard forever.");
        var replacement = BotPotionInventory.Replacement(p, glow);
        Console.WriteLine($"PROBE full belt replacement={replacement?.Id.Entry} flex={BotPotionInventory.ReserveValue(p,flex):F2} forge={BotPotionInventory.ReserveValue(p,forge):F2}");
        Check(replacement is FlexPotion or BlessingOfTheForge, "Full belt must make room for Glowwater over a weaker temporary bottle.");
        Check(BotPotionInventory.Replacement(p, flex) is null, "Equal or weaker rewards must not churn the potion belt.");
        // Exercise actual Offer -> native discard -> native reward selection,
        // not only an isolated inventory ranking that never reaches the driver.
        run.AppendToMapPointHistory(MegaCrit.Sts2.Core.Map.MapPointType.Unknown,
            MegaCrit.Sts2.Core.Rooms.RoomType.Event, ModelDb.Event<DrowningBeacon>().Id);
        var net = DispatchProxy.Create<INetHostGameService, ReconnectNetProxy>();
        using (var sync = new RewardsSetSynchronizer(new RunLocationTargetedMessageBuffer(net), net, run, 1))
        {
            var reward = new PotionReward(glow, p);
            var set = new RewardsSet(p, sync).WithCustomRewards([reward]);
            typeof(RewardsSet).GetField("_isGenerated", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(set, true);
            BotRewardDriver.Offer(set).GetAwaiter().GetResult();
            Check(reward.SuccessfullySelected && p.Potions.Contains(glow) && !p.Potions.Contains(replacement!),
                "The synthetic reward driver must actually procure Glowwater after a native discard.");
        }
        Console.WriteLine("PASS: full-belt Glowwater reward can replace a weaker bottle without discarding equal-value stock.");

        var combat = new CombatState(runState: run);
        foreach (var member in party) { member.ResetCombatState(); combat.AddPlayer(member); member.PlayerCombatState!.Phase = PlayerTurnPhase.Play; member.PlayerCombatState.GainEnergy(3); }
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "enemy");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(500); enemy.SetCurrentHpInternal(500);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        p.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<StrikeSilent>(p));
        p.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeSilent>(p));
        var root = KernelSession.Capture(combat);
        var benefit = KernelPotionOpportunity.Measure(root, forge, p.Creature, party);
        Console.WriteLine($"PROBE Forge full-belt benefit={benefit}");
        Check(benefit.FullValue && benefit.Benefit >= 6, "Forge must proactively upgrade two payable attacks with a full belt.");
        p.PlayerCombatState.LoseEnergy(3);
        Check(!KernelPotionOpportunity.Measure(KernelSession.Capture(combat), forge, p.Creature, party).FullValue,
            "Forge must not invent attack upgrade value when no follow-up can be paid.");
        Console.WriteLine("PASS: Forge full-belt opportunity prices payable upgraded attacks before an emergency.");
        if (Failures.Count > 0) throw new Exception(string.Join("\n", Failures));
    }
}
