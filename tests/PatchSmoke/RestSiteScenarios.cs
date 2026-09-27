using System.Reflection;
using CoopBots;
using CoopBots.Building;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Rest-site policy: team healing and the relic-granted options (Shovel dig,
// Girya lift, ...) must be valued, not silently fall through a default score.
internal static class RestSiteScenarios
{
    // R2 2026-09-27: 20 deliberate production faults each exited 1, then restored.
    // Commands/exits: work/strength-20260927-v8/r2-results.json. Failure text:
    // A final camp must not suppress an upgrade for 15 effective HP and 9 overflow at 65/80.
    // Camp choices must await the previous seat's healing.
    internal static void Run()
    {
        RestSiteEntryScenarios.Run();
        RestSiteValueScenarios.Run();
        var choose = typeof(BotBrain).Assembly.GetType("CoopBots.BotRestSitePatch")!
            .GetMethod("Choose", BindingFlags.Static | BindingFlags.NonPublic)!;
        // The chooser returns a CampPlan (index plus diagnostic patient/heal fields).
        // Healing is read from actual HP after each choice; nothing is reserved.
        int Pick(Player player, params RestSiteOption[] options)
        {
            var plan = choose.Invoke(null, new object[] { player, options })!;
            return (int)plan.GetType().GetProperty("Index")!.GetValue(plan)!;
        }

        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
        var teammate = Player.CreateForNewRun<Deprived>(UnlockState.all, 77UL);
        var run = RunState.CreateForTest(new[] { bot, teammate }, seed: "RESTSITE");
        bot.ResetCombatState(); teammate.ResetCombatState();
        bot.Creature.SetMaxHpInternal(80); bot.Creature.SetCurrentHpInternal(80);
        teammate.Creature.SetMaxHpInternal(80); teammate.Creature.SetCurrentHpInternal(16);

        // A badly hurt human teammate must pull the bot to Mend over self-heal.
        var mend = new MendRestSiteOption(bot);
        var heal = new HealRestSiteOption(bot);
        if (Pick(bot, heal, mend) != 1)
            throw new Exception("A badly hurt teammate must make Mend outrank self-heal.");
        Console.WriteLine("PASS: rest-site Mend outranks self-heal when a teammate is badly hurt.");

        // With nobody hurt, the relic options (Girya lift) must beat a smith.
        teammate.Creature.SetCurrentHpInternal(80);
        bot.AddRelicInternal(ModelDb.Relic<Girya>().ToMutable());
        bot.Deck.AddInternal(run.CreateCard<Inflame>(bot));
        var smith = new SmithRestSiteOption(bot);
        var lift = new LiftRestSiteOption(bot);
        if (Pick(bot, smith, lift) != 1)
            throw new Exception("A relic-granted Lift must outrank Smith when nobody needs healing.");
        Console.WriteLine("PASS: rest-site relic option (Girya lift) outranks Smith.");

        // Cook removes two cards AND grants +5 max HP, so it must be attractive
        // with a bloated basic deck and left alone when the worst cards are good.
        var junkBot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 2, 1));
        junkBot.ResetCombatState();
        junkBot.Creature.SetMaxHpInternal(80); junkBot.Creature.SetCurrentHpInternal(80);
        for (var i = 0; i < 12; i++) junkBot.Deck.AddInternal(run.CreateCard<StrikeIronclad>(junkBot));
        var junkSmith = new SmithRestSiteOption(junkBot);
        var junkCook = new CookRestSiteOption(junkBot);
        if (Pick(junkBot, junkSmith, junkCook) != 1)
            throw new Exception("Cook must be chosen to strip two basic cards (and gain max HP).");
        Console.WriteLine("PASS: rest-site Cook removes junk and gains max HP instead of costing it.");

        // "The worst cards are good" needs a deck of distinct quality cards.
        // Twelve copies of one card is a duplicate-heavy deck, where the surplus
        // copies genuinely are the worst cards and thinning them is right.
        var goodBot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 3, 1));
        goodBot.ResetCombatState();
        goodBot.Creature.SetMaxHpInternal(80); goodBot.Creature.SetCurrentHpInternal(80);
        foreach (var make in new Func<MegaCrit.Sts2.Core.Models.CardModel>[]
                 {
                     () => run.CreateCard<Bash>(goodBot), () => run.CreateCard<Anger>(goodBot),
                     () => run.CreateCard<TwinStrike>(goodBot), () => run.CreateCard<PommelStrike>(goodBot),
                     () => run.CreateCard<IronWave>(goodBot), () => run.CreateCard<ShrugItOff>(goodBot),
                     () => run.CreateCard<Inflame>(goodBot), () => run.CreateCard<Uppercut>(goodBot),
                     () => run.CreateCard<BodySlam>(goodBot), () => run.CreateCard<Breakthrough>(goodBot),
                 })
            goodBot.Deck.AddInternal(make());
        var goodSmith = new SmithRestSiteOption(goodBot);
        var goodCook = new CookRestSiteOption(goodBot);
        if (Pick(goodBot, goodSmith, goodCook) != 0)
            throw new Exception("Cook must not strip good cards just for the max HP.");
        Console.WriteLine("PASS: rest-site Cook is declined when the two worst cards are good.");

        // Adding a thirteenth copy is refused, but upgrading one of the copies
        // already in the deck is worth it: addition and upgrade are now separate
        // quantities, each measured against the deck as it stands.
        var dupeBot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 4, 1));
        dupeBot.ResetCombatState();
        dupeBot.Creature.SetMaxHpInternal(80); dupeBot.Creature.SetCurrentHpInternal(80);
        for (var i = 0; i < 12; i++) dupeBot.Deck.AddInternal(run.CreateCard<Inflame>(dupeBot));
        var sample = dupeBot.Deck.Cards.First();
        var addMore = BuildValue.Add(sample, dupeBot).Total;
        var upgradeOne = BuildValue.UpgradeDelta(sample, dupeBot).Total;
        if (!(addMore <= 0 && upgradeOne > 0))
            throw new Exception($"An existing card must be upgrade-worthy even when another copy is not "
                + $"(add={addMore:F1}, upgrade={upgradeOne:F1}).");
        Console.WriteLine("PASS: another duplicate is refused while upgrading an existing copy is worth it.");

        // Healing is worth a camp when the wound is big enough that the coming
        // fight could still kill; an ordinary scratch is still left alone so the
        // bot upgrades its deck instead.
        var reset = typeof(BotBrain).Assembly.GetType("CoopBots.BotRestSitePatch")!
            .GetMethod("ResetPlanning", BindingFlags.Static | BindingFlags.NonPublic)!;
        var healer = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 4, 1));
        var helper = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 5, 1));
        var patient = Player.CreateForNewRun<Deprived>(UnlockState.all, 78UL);
        var healRun = RunState.CreateForTest(new[] { healer, helper, patient }, seed: "RESTSITE-HEAL");
        foreach (var medic in new[] { healer, helper })
        {
            medic.ResetCombatState();
            medic.Creature.SetMaxHpInternal(80); medic.Creature.SetCurrentHpInternal(80);
            medic.Deck.AddInternal(healRun.CreateCard<Inflame>(medic));
        }
        patient.ResetCombatState();
        patient.Creature.SetMaxHpInternal(73);

        // Without a tent, aid competes with a real upgrade. Moderate wounds can
        // lose that comparison, while a larger useful heal still wins it.
        healRun.CurrentActIndex = 0;
        reset.Invoke(null, null);
        patient.Creature.SetCurrentHpInternal(45);
        if (Pick(healer, new SmithRestSiteOption(healer), new MendRestSiteOption(healer)) != 0)
            throw new Exception("Without a tent a healthy bot must smith, not top up a teammate (45/73).");
        reset.Invoke(null, null);
        patient.Creature.SetCurrentHpInternal(15);
        if (Pick(healer, new SmithRestSiteOption(healer), new MendRestSiteOption(healer)) != 1)
            throw new Exception("A teammate near death must still be mended without a tent (15/73).");
        Console.WriteLine("PASS: rest-site MEND competes with upgrades by the patient's useful recovery.");

        // A MiniatureTent is what makes mending free: its owner keeps every option
        // at the camp, so the smith and the mend are not competing for the same
        // pick. What the party must still not do is stack two mends onto one target.
        healer.AddRelicInternal(ModelDb.Relic<MiniatureTent>().ToMutable());
        helper.AddRelicInternal(ModelDb.Relic<MiniatureTent>().ToMutable());
        reset.Invoke(null, null);
        patient.Creature.SetCurrentHpInternal(45);
        if (Pick(healer, new MendRestSiteOption(healer)) != 0)
            throw new Exception("A tent owner must be able to mend.");
        patient.Creature.SetCurrentHpInternal(67); // first mend has actually settled
        if (Pick(helper, new SmithRestSiteOption(helper), new MendRestSiteOption(helper)) != 0)
            throw new Exception("A second bot must not stack another mend onto an already-healed teammate.");
        Console.WriteLine("PASS: rest-site mends do not stack; the second bot upgrades instead of overhealing.");

        // A severe wound should compete on useful recovery on either side of
        // the removed 25% threshold, without suppressing the upgrade score.
        reset.Invoke(null, null);
        healer.Creature.SetCurrentHpInternal(21);
        if (Pick(healer, new SmithRestSiteOption(healer), new HealRestSiteOption(healer)) != 1)
            throw new Exception("A large useful heal must compete with an upgrade without the old 25% cliff.");
        healer.Creature.SetCurrentHpInternal(19);
        if (Pick(healer, new SmithRestSiteOption(healer), new HealRestSiteOption(healer)) != 1)
            throw new Exception("Act 1 must rest below 25% HP.");
        healer.Creature.SetCurrentHpInternal(80);
        Console.WriteLine("PASS: a severe wound is healed on both sides of the old 25% cliff.");

        // A useful upgrade remains a competitor on both sides of the removed
        // 55% cliff. A larger wound or a boss next can still make healing win.
        healRun.CurrentActIndex = 2;
        reset.Invoke(null, null);
        healer.Creature.SetCurrentHpInternal(49);
        if (Pick(healer, new SmithRestSiteOption(healer), new HealRestSiteOption(healer)) != 0)
            throw new Exception("Act 3 must still smith at 61% HP away from the boss camp.");
        healer.Creature.SetCurrentHpInternal(43);
        if (Pick(healer, new SmithRestSiteOption(healer), new HealRestSiteOption(healer)) != 0)
            throw new Exception("A useful upgrade must remain competitive below the old 55% camp threshold.");
        healer.Creature.SetCurrentHpInternal(80);
        Console.WriteLine("PASS: act 3 compares actual upgrades and effective healing without a 55% cliff.");

        // R2 2026-09-27: restore the 25% boss-camp floor -> exit 1:
        // A half-health seat must heal at its act-1 boss camp instead of buying another upgrade.
        var bossCamp = new HarmonyLib.Harmony("coopbots.test.final-camp");
        var lastCampMethod = HarmonyLib.AccessTools.Method(typeof(RunDepth), "LastCampBeforeBoss");
        bossCamp.Patch(lastCampMethod, prefix: new HarmonyLib.HarmonyMethod(typeof(RestSiteScenarios), nameof(IsBossCamp)));
        try
        {
            healRun.CurrentActIndex = 0;
            healer.Creature.SetCurrentHpInternal(42);
            reset.Invoke(null, null);
            if (Pick(healer, new SmithRestSiteOption(healer), new HealRestSiteOption(healer)) != 1)
                throw new Exception("A half-health seat must heal at its act-1 boss camp instead of buying another upgrade.");
            healer.Creature.SetCurrentHpInternal(78);
            if (Pick(healer, new SmithRestSiteOption(healer), new HealRestSiteOption(healer)) != 0)
                throw new Exception("A nearly full-health seat must still upgrade at its boss camp.");
            healRun.CurrentActIndex = 2;
            healer.Creature.SetCurrentHpInternal(65);
            if (Pick(healer, new SmithRestSiteOption(healer), new HealRestSiteOption(healer)) != 0)
                throw new Exception("A final camp must not suppress an upgrade for 15 effective HP and 9 overflow at 65/80.");
            if (BotRestSitePatch.HealValue(24, 65, 80, .72) != BotRestSitePatch.HealValue(15, 65, 80, .72))
                throw new Exception("Overhealing must have exactly zero camp value.");
            Console.WriteLine("PASS: the last camp heals a half-health seat and upgrades a healthy one.");

            // V8c live floor 46: a prior MEND raised this pillow owner from
            // 16/80 to 55/80; it then self-healed with 14 overflow because the
            // 38/77 ally was above the old 25% aid gate. Reuse real native heal
            // amounts, not a stub: aid is 23.1 HP, self-heal is 25 of 39 HP.
            // R2: both faults exit 1, then restore; v8d/r2-results.json:
            // A useful MEND to a 38/77 ally must beat self-healing 55/80 with 14 overflow.
            // When both seats are nearly full, the real upgrade must beat either heal.
            var pillowOwner = Player.CreateForNewRun<Deprived>(UnlockState.all,
                BotRegistry.CreateId(BotDifficulty.Pro, 1, 8));
            var woundedAlly = Player.CreateForNewRun<Deprived>(UnlockState.all,
                BotRegistry.CreateId(BotDifficulty.Pro, 2, 8));
            var aidRun = RunState.CreateForTest(new[] { pillowOwner, woundedAlly }, seed: "CAMP-AID-OVERFLOW");
            aidRun.CurrentActIndex = 2;
            pillowOwner.ResetCombatState(); woundedAlly.ResetCombatState();
            pillowOwner.Creature.SetMaxHpInternal(80); pillowOwner.Creature.SetCurrentHpInternal(55);
            woundedAlly.Creature.SetMaxHpInternal(77); woundedAlly.Creature.SetCurrentHpInternal(38);
            pillowOwner.AddRelicInternal(ModelDb.Relic<RegalPillow>().ToMutable());
            pillowOwner.Deck.AddInternal(aidRun.CreateCard<Predator>(pillowOwner));
            if (Pick(pillowOwner, new SmithRestSiteOption(pillowOwner), new HealRestSiteOption(pillowOwner),
                    new MendRestSiteOption(pillowOwner)) != 2)
                throw new Exception("A useful MEND to a 38/77 ally must beat self-healing 55/80 with 14 overflow.");
            pillowOwner.Creature.SetCurrentHpInternal(78); woundedAlly.Creature.SetCurrentHpInternal(70);
            if (Pick(pillowOwner, new SmithRestSiteOption(pillowOwner), new HealRestSiteOption(pillowOwner),
                    new MendRestSiteOption(pillowOwner)) != 0)
                throw new Exception("When both seats are nearly full, the real upgrade must beat either heal.");
            Console.WriteLine("PASS: useful aid above 25% HP beats overhealing; small wounds still lose to an upgrade.");
        }
        finally
        {
            bossCamp.Unpatch(lastCampMethod, HarmonyLib.HarmonyPatchType.All, bossCamp.Id);
            healer.Creature.SetCurrentHpInternal(80);
        }

        // The camp is worth what comes after it. The reported wipe walked into the
        // act boss at 43-61% because the heal value asked "how hurt are you" with
        // no idea whether the next room was the boss or a shop. The multiplier is
        // pinned directly: the decision itself only differs when a map is present,
        // and a fabricated map is not worth trusting over this.
        var risk = typeof(BotBrain).Assembly.GetType("CoopBots.BotRestSitePatch")!
            .GetMethod("RiskFor", BindingFlags.Static | BindingFlags.NonPublic)!;
        double Risk(MegaCrit.Sts2.Core.Map.MapPointType type, int act)
            => (double)risk.Invoke(null, new object[] { type, act })!;
        var beforeBoss = Risk(MegaCrit.Sts2.Core.Map.MapPointType.Boss, 2);
        var beforeShop = Risk(MegaCrit.Sts2.Core.Map.MapPointType.Shop, 2);
        var beforeMonster = Risk(MegaCrit.Sts2.Core.Map.MapPointType.Monster, 1);
        if (!(beforeBoss > beforeMonster && beforeMonster > beforeShop))
            throw new Exception($"Risk must fall from boss to monster to shop: "
                + $"boss={beforeBoss:F2}, monster={beforeMonster:F2}, shop={beforeShop:F2}.");
        if (Risk(MegaCrit.Sts2.Core.Map.MapPointType.Elite, 2) <= Risk(MegaCrit.Sts2.Core.Map.MapPointType.Elite, 0))
            throw new Exception("A late-act elite must be riskier than an act-1 elite.");
        // The same boss wound is worth more in the last act, and the last camp
        // before the boss is the last chance to heal at all — the case the
        // reviewed party lost: three bots upgraded instead of resting there.
        var finalBoss = Risk(MegaCrit.Sts2.Core.Map.MapPointType.Boss, 2);
        var earlyBoss = Risk(MegaCrit.Sts2.Core.Map.MapPointType.Boss, 0);
        if (!(finalBoss > earlyBoss))
            throw new Exception($"The last act's boss must outweigh an earlier one: {finalBoss:F2} vs {earlyBoss:F2}.");
        // The last camp before a boss is priced as that act's boss camp, not as one
        // flat number for the whole run. A flat floor made an act-1 boss camp as
        // urgent as the act-3 one, which is exactly backwards for a deck that still
        // has two acts to grow into.
        var camp = typeof(BotBrain).Assembly.GetType("CoopBots.BotRestSitePatch")!
            .GetMethod("RiskForCamp", BindingFlags.Static | BindingFlags.NonPublic)!;
        double CampRisk(int act, MegaCrit.Sts2.Core.Map.MapPointType? next, bool last)
            => (double)camp.Invoke(null, new object?[] { act, next, last })!;
        const MegaCrit.Sts2.Core.Map.MapPointType monsterNext = MegaCrit.Sts2.Core.Map.MapPointType.Monster;
        if (CampRisk(0, monsterNext, true) != Risk(MegaCrit.Sts2.Core.Map.MapPointType.Boss, 0))
            throw new Exception("An act-1 boss camp must be priced as the act-1 boss, not the last act's.");
        if (!(CampRisk(2, monsterNext, true) > CampRisk(0, monsterNext, true)))
            throw new Exception("The final act's boss camp must still be the most urgent.");
        if (CampRisk(0, monsterNext, false) != Risk(MegaCrit.Sts2.Core.Map.MapPointType.Monster, 0))
            throw new Exception("A camp that is not the last must be priced by the room after it alone.");
        Console.WriteLine("PASS: the last camp is priced by its own act's boss risk.");
        // The point of the multiplier: the same wound that loses to an upgrade on a
        // quiet road must beat it when the boss is next. 55/73 was the pinned case
        // that "still loses to an upgrade"; the human mend weight is 0.4 * 1.2.
        var patch = typeof(BotBrain).Assembly.GetType("CoopBots.BotRestSitePatch")!;
        var healValue = patch.GetMethod("HealValue", BindingFlags.Static | BindingFlags.NonPublic)!;
        var mendAmount = (decimal)patch.GetMethod("HealAmount", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { patient, false })!;
        double Wounded(double risk)
            => (double)healValue.Invoke(null, new object[] { mendAmount, 55m, 73m, 0.4 * 1.2 * risk })!;
        var quiet = Wounded(beforeShop);
        var bossRoom = Wounded(beforeBoss);
        if (!(quiet < 16 && bossRoom > 16))
            throw new Exception($"The next room must decide the same wound: quiet={quiet:F1}, boss={bossRoom:F1}, upgrade=16.");
        Console.WriteLine($"PASS: rest-site healing is scaled by what comes next "
            + $"({quiet:F1} before a shop, {bossRoom:F1} before the boss).");

        // Greed is not recklessness: a teammate near death must still be saved,
        // and this runs in act 1 — the greediest act — precisely because that is
        // where the rule has to hold hardest.
        reset.Invoke(null, null);
        patient.Creature.SetCurrentHpInternal(7);
        if (Pick(healer, new SmithRestSiteOption(healer), new MendRestSiteOption(healer),
            new HealRestSiteOption(healer)) != 1)
            throw new Exception("A nearly dead teammate must be mended over Smith.");
        Console.WriteLine("PASS: rest-site Mend still rescues a nearly dead teammate, even in act 1.");

        // Exercise the same asynchronous sequencing helper as BeginRestSite.
        // The first heal is pending; the second seat may only decide after it.
        var release = new TaskCompletionSource();
        var enteredSecond = false;
        patient.Creature.SetCurrentHpInternal(8);
        var sequence = BotRestSitePatch.CompleteInOrder(new[] { healer, helper }, async p =>
        {
            if (p == healer) { await release.Task; patient.Creature.SetCurrentHpInternal(patient.Creature.MaxHp); }
            else { enteredSecond = true; if (patient.Creature.CurrentHp != patient.Creature.MaxHp)
                throw new Exception("The next camp seat must observe the completed heal."); }
        });
        if (enteredSecond) throw new Exception("Camp choices must await the previous seat's healing.");
        release.SetResult(); sequence.GetAwaiter().GetResult();
        if (!enteredSecond) throw new Exception("Camp sequencing must continue to the next seat.");
        Console.WriteLine("PASS: camp choices await actual healing and do not reserve it twice.");

        // MEND IS NOT OFFERED WHERE IT CANNOT RUN (strength-review §4.3). MendRestSiteOption
        // .OnSelect for LocalContext.IsMe(owner) drives the TARGETING UI and awaits a click a
        // handed-over seat can never make; that is the live NullReferenceException
        // `rest-site option MEND refused for 1: Object reference not set`, three times over five
        // A5 rounds. A driven LOCAL seat must fall back to a self-heal instead of offering it.
        //
        // CHECKED (R2): making MendRunnable always true turns this red, verbatim:
        //   System.Exception: a driven LOCAL seat must not be offered MEND: its OnSelect drives
        //   the targeting UI, which no click can answer; got index 1.
        var localSeat = Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 4, 4));
        var localMate = Player.CreateForNewRun<Deprived>(UnlockState.all, 78UL);
        var localRun = RunState.CreateForTest(new[] { localSeat, localMate }, seed: "RESTSITE-MEND-LOCAL");
        localSeat.ResetCombatState(); localMate.ResetCombatState();
        localSeat.Creature.SetMaxHpInternal(80); localSeat.Creature.SetCurrentHpInternal(80);
        localMate.Creature.SetMaxHpInternal(80); localMate.Creature.SetCurrentHpInternal(8);
        var savedLocalId = MegaCrit.Sts2.Core.Context.LocalContext.NetId;
        MegaCrit.Sts2.Core.Context.LocalContext.NetId = localSeat.NetId;
        try
        {
            reset.Invoke(null, null);
            if (!AutoPilot.Drives(localSeat.NetId)) AutoPilot.Set(localSeat.NetId, true);
            var picked = Pick(localSeat, new MendRestSiteOption(localSeat), new HealRestSiteOption(localSeat));
            if (picked != 1)
                throw new Exception("a driven LOCAL seat must not be offered MEND: its OnSelect drives "
                    + $"the targeting UI, which no click can answer; got index {picked}.");
            Console.WriteLine("PASS: a driven local seat is offered HEAL, never the unanswerable MEND.");
        }
        finally
        {
            MegaCrit.Sts2.Core.Context.LocalContext.NetId = savedLocalId;
        }
    }
    private static bool IsBossCamp(ref bool __result) { __result = true; return false; }

}
