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
    internal static void Run()
    {
        var choose = typeof(BotBrain).Assembly.GetType("CoopBots.BotRestSitePatch")!
            .GetMethod("Choose", BindingFlags.Static | BindingFlags.NonPublic)!;
        int Pick(Player player, params RestSiteOption[] options)
            => (int)choose.Invoke(null, new object[] { player, options })!;

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

        // Healing is only worth a permanent upgrade near death. Above half HP a
        // teammate needs no care at all, and the per-HP rate is low enough that
        // an ordinary wound is left alone so the bot upgrades its deck instead.
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

        // 20/73 is the reported case: one mend is fine, but the party must not
        // stack mends onto the same target. One bot mends, the next bot smiths.
        reset.Invoke(null, null);
        patient.Creature.SetCurrentHpInternal(20);
        if (Pick(healer, new SmithRestSiteOption(healer), new MendRestSiteOption(healer)) != 1)
            throw new Exception("A teammate well below half HP must still be mended once.");
        if (Pick(helper, new SmithRestSiteOption(helper), new MendRestSiteOption(helper)) != 0)
            throw new Exception("A second bot must not stack another mend onto an already-healed teammate.");
        Console.WriteLine("PASS: rest-site mends do not stack; the second bot upgrades instead of overhealing.");

        // Above half HP nobody needs care: the heal must score zero so the bot
        // takes the permanent upgrade.
        reset.Invoke(null, null);
        patient.Creature.SetCurrentHpInternal(41);
        if (Pick(healer, new SmithRestSiteOption(healer), new MendRestSiteOption(healer)) != 0)
            throw new Exception("A teammate above half HP must not pull a bot off Smith.");
        Console.WriteLine("PASS: rest-site healing is declined above half HP so the bot upgrades its deck.");

        // A teammate near death must still be saved.
        reset.Invoke(null, null);
        patient.Creature.SetCurrentHpInternal(7);
        if (Pick(healer, new SmithRestSiteOption(healer), new MendRestSiteOption(healer),
            new HealRestSiteOption(healer)) != 1)
            throw new Exception("A nearly dead teammate must be mended over Smith.");
        Console.WriteLine("PASS: rest-site Mend still rescues a nearly dead teammate.");
    }
}
