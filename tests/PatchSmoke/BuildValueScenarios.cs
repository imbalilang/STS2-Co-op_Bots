using System.Reflection;
using CoopBots;
using CoopBots.Building;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Build valuation: reward picks, removals and upgrades all answer to one
// objective measured against the deck as it stands, so skipping a reward is a
// real candidate rather than a fallback.
internal static class BuildValueScenarios
{
    internal static void Run()
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var party = new[] { bot };
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "BUILDVALUE"));
        bot.ResetCombatState(); combat.AddPlayer(bot);
        void Clear() { foreach (var card in bot.Deck.Cards.ToArray()) bot.Deck.RemoveInternal(card); }
        void Give<T>(int count = 1) where T : CardModel
        {
            for (var i = 0; i < count; i++) bot.Deck.AddInternal(combat.CreateCard<T>(bot));
        }

        // A starter deck has no area damage, so the first one is worth taking.
        Clear(); Give<StrikeIronclad>(5); Give<DefendIronclad>(4);
        var cleave = combat.CreateCard<Breakthrough>(bot);
        var cleaveValue = BuildValue.Add(cleave, bot);
        if (cleaveValue.Total <= 0)
            throw new Exception($"The deck's first area attack must be worth taking: {cleaveValue.Total:F1} ({cleaveValue.Reason}).");
        if (!cleaveValue.Reason.Contains("needs-aoe"))
            throw new Exception($"Filling a missing role must be visible in the reason: {cleaveValue.Reason}");
        Console.WriteLine("PASS: a card that fills a missing role is worth taking.");

        // The same card stops being worth it once the role is saturated and the
        // deck is already large: this is what makes Skip a real candidate.
        Clear();
        for (var i = 0; i < 5; i++) Give<Breakthrough>();
        Give<StrikeIronclad>(20);
        var extraBreakthrough = BuildValue.Add(combat.CreateCard<Breakthrough>(bot), bot);
        if (extraBreakthrough.Total > 0)
            throw new Exception($"A saturated role in a bloated deck must not score positive: {extraBreakthrough.Total:F1} ({extraBreakthrough.Reason}).");
        Console.WriteLine("PASS: a saturated role in a bloated deck scores below skipping.");

        // Skipping must beat every candidate when nothing improves the deck.
        var bloated = new List<CardModel> { combat.CreateCard<Breakthrough>(bot), combat.CreateCard<Breakthrough>(bot) };
        if (BuildValue.BestReward(bot, bloated) != -1)
            throw new Exception("BestReward must return Skip when no candidate improves the deck.");
        Console.WriteLine("PASS: skipping is chosen when no candidate improves the deck.");

        // An upgrade that raises two things must beat one that only raises
        // damage, and neither may be priced as if the card were added again.
        Clear(); Give<StrikeIronclad>(5); Give<DefendIronclad>(4);
        var broad = combat.CreateCard<Bash>(bot);          // +2 damage and +1 Vulnerable
        var plain = combat.CreateCard<StrikeIronclad>(bot); // +3 damage only
        var broadGain = BuildValue.UpgradeDelta(broad, bot);
        var plainGain = BuildValue.UpgradeDelta(plain, bot);
        if (plainGain.Total <= 0)
            throw new Exception($"A damage upgrade must be worth something: {plainGain.Total:F1} ({plainGain.Reason}).");
        if (broadGain.Total <= plainGain.Total)
            throw new Exception($"An upgrade that also raises Vulnerable must beat a plain damage upgrade "
                + $"(broad={broadGain.Total:F1} {broadGain.Reason}, plain={plainGain.Total:F1}).");
        var addSame = BuildValue.Add(plain, bot).Total;
        if (addSame > plainGain.Total * 10)
            throw new Exception($"Upgrade and addition must be separate quantities (add={addSame:F1}, upgrade={plainGain.Total:F1}).");
        Console.WriteLine("PASS: upgrades are priced by the real difference they make.");

        // Removal: a diluting non-basic card is a target, but the only copy of a
        // role the deck depends on is protected.
        Clear(); Give<StrikeIronclad>(12);
        var filler = bot.Deck.Cards.First();
        var removal = BuildValue.Remove(filler, bot);
        if (removal.Total <= 0)
            throw new Exception($"A diluting basic card must be worth removing: {removal.Total:F1}.");
        // The only copy of a role the deck depends on must be *refused*, not
        // merely penalised: a deduction can always be outweighed.
        Clear(); Give<StrikeIronclad>(6); Give<DefendIronclad>();
        var lastDefend = bot.Deck.Cards.First(card => card.Type == CardType.Skill);
        var protectedRemoval = BuildValue.Remove(lastDefend, bot);
        if (protectedRemoval.Total != 0 || !protectedRemoval.Reason.Contains("protected"))
            throw new Exception($"Removing the only defensive card must be refused, got "
                + $"{protectedRemoval.Total:F1} ({protectedRemoval.Reason}).");
        if (BotShopPlanner.RemovalValue(lastDefend, bot) != 0)
            throw new Exception("A refused removal must also not be worth gold.");
        Console.WriteLine("PASS: removal targets diluting cards and refuses to remove the deck's only defence.");

        // An upgrade that removes Exhaust must score above zero: the card becomes
        // reusable, which the previous blanket lost-role penalty inverted.
        Clear(); Give<StrikeIronclad>(5); Give<DefendIronclad>(4);
        var hologram = combat.CreateCard<Hologram>(bot);
        if (BuildValue.UpgradeDelta(hologram, bot).Total <= 0)
            throw new Exception($"An upgrade that removes Exhaust must be worth taking, got "
                + $"{BuildValue.UpgradeDelta(hologram, bot).Total:F1} ({BuildValue.UpgradeDelta(hologram, bot).Reason}).");
        Console.WriteLine("PASS: an upgrade that drops Exhaust is valued as reusability, not as a lost tag.");


        // A route the deck is already on must make its payoff cards worth more:
        // the same card means different things in a Strength deck and elsewhere.
        var outsider = Player.CreateForNewRun<Deprived>(UnlockState.all, 2);
        var routeParty = new[] { outsider };
        var routeCombat = new CombatState(runState: RunState.CreateForTest(routeParty, seed: "BUILDVALUE-ROUTE"));
        outsider.ResetCombatState(); routeCombat.AddPlayer(outsider);
        void ClearOutsider() { foreach (var card in outsider.Deck.Cards.ToArray()) outsider.Deck.RemoveInternal(card); }
        void GiveOutsider<T>(int count) where T : CardModel
        {
            for (var i = 0; i < count; i++) outsider.Deck.AddInternal(routeCombat.CreateCard<T>(outsider));
        }

        ClearOutsider(); GiveOutsider<TwinStrike>(4);
        if (Archetypes.Detect(outsider.Deck.Cards.ToList()).Count != 0)
            throw new Exception("A deck with no scaling must not claim a route.");
        var twinInPlainDeck = BuildValue.Add(routeCombat.CreateCard<TwinStrike>(outsider), outsider).Total;

        ClearOutsider(); GiveOutsider<TwinStrike>(4); GiveOutsider<Inflame>(2);
        var routes = Archetypes.Detect(outsider.Deck.Cards.ToList());
        // Deprived is not a character the mined feed covers, so this exercises
        // the role routes: the multi-hit payoff must be what is recognised.
        if (routes.Count == 0 || !routes[0].Roles.Contains("multihit"))
            throw new Exception($"A multi-hit deck with Strength must be recognised: "
                + $"{string.Join(",", routes.Select(match => match.Name))}");
        var twinInStrengthDeck = BuildValue.Add(routeCombat.CreateCard<TwinStrike>(outsider), outsider).Total;
        if (twinInStrengthDeck <= twinInPlainDeck)
            throw new Exception($"A multi-hit payoff must be worth more once the deck scales Strength "
                + $"({twinInStrengthDeck:F1} vs {twinInPlainDeck:F1}).");
        Console.WriteLine("PASS: a card that feeds the deck's route is worth more than the same card elsewhere.");

        // Size pressure: past the forming stage the same card must be worth less,
        // and a mediocre one must go below zero. This is the lever that makes Skip
        // a real answer to "the card is fine but not what the deck needs".
        // Calibrated on real reward screens: at 19 cards the best candidate was
        // ~18-20 and should have been skipped.
        ClearOutsider(); GiveOutsider<StrikeIronclad>(5); GiveOutsider<DefendIronclad>(4);
        var smallDeck = outsider.Deck.Cards.ToList();
        var smallValue = BuildValue.Add(routeCombat.CreateCard<TwinStrike>(outsider), outsider, smallDeck).Total;
        ClearOutsider(); GiveOutsider<StrikeIronclad>(11); GiveOutsider<DefendIronclad>(9);
        var bigDeck = outsider.Deck.Cards.ToList();
        var bigValuation = BuildValue.Add(routeCombat.CreateCard<TwinStrike>(outsider), outsider, bigDeck);
        if (!(bigValuation.Total < smallValue))
            throw new Exception($"The same card must be worth less in a 20-card deck ({bigValuation.Total:F1}) "
                + $"than in a 9-card one ({smallValue:F1}).");
        if (bigValuation.Total >= 0)
            throw new Exception($"A filler card in a 20-card starter deck must score below skipping, "
                + $"got {bigValuation.Total:F1} ({bigValuation.Reason}).");
        if (!bigValuation.Reason.Contains("size:"))
            throw new Exception($"The size pressure must be visible in the reason: {bigValuation.Reason}");
        Console.WriteLine("PASS: deck size pressure makes a filler card in a large deck score below skipping.");

        // A payoff whose enabler is missing must not read as a plan. Rupture gains
        // Strength when you lose HP, and every card the mining pairs it with is a
        // self-damage card; without one its scaling credit drops and the reason
        // says why. This is the act-2 Ironclad that drafted Rupture with no way to
        // trigger it.
        Clear(); Give<StrikeIronclad>(5); Give<DefendIronclad>(4);
        var ruptureAlone = BuildValue.Marginal(combat.CreateCard<Rupture>(bot), bot);
        if (!ruptureAlone.Reason.Contains("scaling-unsupported"))
            throw new Exception($"Rupture without a self-damage source must be flagged, got: {ruptureAlone.Reason}");
        Give<Hemokinesis>(1);
        var ruptureWithEnabler = BuildValue.Marginal(combat.CreateCard<Rupture>(bot), bot);
        if (!(ruptureWithEnabler.Total > ruptureAlone.Total))
            throw new Exception($"Rupture must be worth more once its mined enabler is in the deck: "
                + $"{ruptureWithEnabler.Total:F1} vs {ruptureAlone.Total:F1}");
        Console.WriteLine("PASS: a scaling payoff without its mined enabler is flagged and valued lower.");

        // "On plan" must include the mined affinity, not only the recognised
        // route. Both decks are the same size, so the raw size pressure is the
        // same; only the partner card differs, and it is the mined data (not the
        // route table) that says a Deadly Poison belongs with it. At 21 cards the
        // raw pressure is (21-15)*5 = 30 and an on-plan card pays 40% = 12.
        Clear(); Give<StrikeSilent>(5); Give<DefendSilent>(15); Give<Outbreak>(1);
        var withAffinity = BuildValue.Add(combat.CreateCard<DeadlyPoison>(bot), bot);
        if (!withAffinity.Reason.Contains("size:12.0"))
            throw new Exception($"A card with mined affinity must pay the reduced size pressure: {withAffinity.Reason}");
        Clear(); Give<StrikeSilent>(5); Give<DefendSilent>(16);
        var withoutAffinity = BuildValue.Add(combat.CreateCard<DeadlyPoison>(bot), bot);
        if (!withoutAffinity.Reason.Contains("size:30.0"))
            throw new Exception($"A card with no partner must pay the full size pressure: {withoutAffinity.Reason}");
        if (!(withAffinity.Total > withoutAffinity.Total))
            throw new Exception($"Mined affinity must be worth real value: {withAffinity.Total:F1} vs {withoutAffinity.Total:F1}");
        Console.WriteLine("PASS: mined affinity counts as on-plan, so a partner card still earns the size relief.");

        // Thinning must get more valuable as dead draws accumulate, or the shop's
        // growing price (a second removal costs 150) can never be paid: a flat
        // 55 x 2.2 = 121 left every run with ten starters still in the deck.
        const double GoldPerDeckValue = 2.2;  // mirrors BotShopPlanner.GoldPerDeckValue
        Clear(); Give<StrikeIronclad>(5); Give<DefendIronclad>(5);
        for (var i = 0; i < 13; i++) Give<PommelStrike>(1);
        var heavyTarget = bot.Deck.Cards.First(card => card.Id.Entry == "STRIKE_IRONCLAD");
        var heavyRemoval = BotShopPlanner.RemovalValue(heavyTarget, bot);
        if (!(heavyRemoval > 150 / GoldPerDeckValue))
            throw new Exception($"Ten starters must make a 150-gold removal affordable, got {heavyRemoval:F1}.");
        Clear(); Give<StrikeIronclad>(2); Give<DefendIronclad>(2);
        for (var i = 0; i < 19; i++) Give<PommelStrike>(1);
        var leanTarget = bot.Deck.Cards.First(card => card.Id.Entry == "STRIKE_IRONCLAD");
        var leanRemoval = BotShopPlanner.RemovalValue(leanTarget, bot);
        if (!(heavyRemoval > leanRemoval))
            throw new Exception($"Removal value must rise with the number of dead draws ({heavyRemoval:F1} vs {leanRemoval:F1}).");
        Console.WriteLine($"PASS: starter removal scales with dead draws ({leanRemoval:F1} -> {heavyRemoval:F1}), so a 150-gold removal is payable.");

        // The reviewed Ironclad declined Blood Wall at -37.8 from a 23-card deck
        // whose own reason line said `needs-block`: the size penalty alone was
        // worth more than the card. A card that fills a function the deck is short
        // of must survive the size of the deck it is joining.
        ClearOutsider();
        GiveOutsider<StrikeIronclad>(9); GiveOutsider<DefendIronclad>(2); GiveOutsider<PommelStrike>(12);
        var bloodWall = BuildValue.Add(routeCombat.CreateCard<BloodWall>(outsider), outsider);
        if (!bloodWall.Reason.Contains("needs-block"))
            throw new Exception($"A deck with two block cards must be shown as short of block: {bloodWall.Reason}");
        if (bloodWall.Total <= 0)
            throw new Exception($"A block card the deck needs must beat skipping even at 23 cards: "
                + $"{bloodWall.Total:F1} ({bloodWall.Reason}).");
        Console.WriteLine($"PASS: a needed block card still gets in past the size pressure ({bloodWall.Total:F1}, {bloodWall.Reason}).");

        // A card nothing asked for costs more the later it is taken, and most
        // once no shop is left to remove it at — that dilution can never be
        // undone. Plan cards keep their relief, so a route can still be finished
        // in the last act rather than being taxed for arriving late.
        if (!(RunDepth.BloatFactor(2, shopAhead: false) > RunDepth.BloatFactor(2, shopAhead: true)
            && RunDepth.BloatFactor(2, shopAhead: true) > RunDepth.BloatFactor(0, shopAhead: true)))
            throw new Exception("The bloat factor must rise with the act and again once no shop is left ahead.");
        if (RunDepth.BloatFactor(0, shopAhead: true) != 1.0)
            throw new Exception("An act-1 deck with shops ahead must be priced exactly as before.");
        void SetAct(int act) => typeof(RunState)
            .GetField("_currentActIndex", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(bot.RunState, act);
        Clear(); Give<StrikeIronclad>(9); Give<DefendIronclad>(9);
        SetAct(0); var earlyFiller = BuildValue.Add(combat.CreateCard<TwinStrike>(bot), bot);
        SetAct(2); var lateFiller = BuildValue.Add(combat.CreateCard<TwinStrike>(bot), bot);
        if (!(lateFiller.Total < earlyFiller.Total))
            throw new Exception($"The same filler must be worth less in the last act: "
                + $"{lateFiller.Total:F1} vs {earlyFiller.Total:F1}.");
        if (!lateFiller.Reason.Contains("size:22.5"))
            throw new Exception($"An act-3 filler must pay the depth-weighted size pressure: {lateFiller.Reason}");
        Clear(); Give<StrikeIronclad>(18);
        SetAct(0); var earlyNeeded = BuildValue.Add(combat.CreateCard<BloodWall>(bot), bot);
        SetAct(2); var lateNeeded = BuildValue.Add(combat.CreateCard<BloodWall>(bot), bot);
        SetAct(0);
        if (Math.Abs(lateNeeded.Total - earlyNeeded.Total) > 0.01)
            throw new Exception($"A card the deck needs must not get dearer with depth: "
                + $"{lateNeeded.Total:F1} vs {earlyNeeded.Total:F1}.");
        if (!lateNeeded.Reason.Contains("size:6.0"))
            throw new Exception($"A needed card keeps its relief in the last act: {lateNeeded.Reason}");
        Console.WriteLine($"PASS: filler gets dearer with depth ({earlyFiller.Total:F1} -> {lateFiller.Total:F1}, "
            + $"{lateFiller.Reason}) while a card the deck needs does not.");

        // The reviewed Silent's Footwork was flagged `scaling-unsupported` in a
        // deck with plenty of block. Dexterity multiplies the block already
        // there, so it is not waiting on a mined partner.
        Clear(); Give<StrikeSilent>(5); Give<DefendSilent>(8);
        var footwork = BuildValue.Marginal(combat.CreateCard<Footwork>(bot), bot);
        if (footwork.Reason.Contains("scaling-unsupported"))
            throw new Exception($"Dexterity in a deck with eight block cards is supported, not a missing enabler: {footwork.Reason}");
        Clear(); Give<StrikeSilent>(8); Give<DefendSilent>(1);
        var footworkAlone = BuildValue.Marginal(combat.CreateCard<Footwork>(bot), bot);
        if (!(footwork.Total > footworkAlone.Total))
            throw new Exception($"Dexterity must be worth more with block to multiply: {footwork.Total:F1} vs {footworkAlone.Total:F1}");
        Console.WriteLine($"PASS: Dexterity scaling is valued against the block the deck already has ({footworkAlone.Total:F1} -> {footwork.Total:F1}).");

        // No card in the pool both retains and blocks, so a retained skill must
        // not read as a block build. The route is the Dexterity one.
        Clear(); Give<DefendSilent>(4); Give<StrikeSilent>(2); Give<Snakebite>(1);
        var retainNames = Archetypes.Detect(bot.Deck.Cards.ToList()).Select(match => match.Name).ToArray();
        if (retainNames.Contains("block-retain"))
            throw new Exception($"A deck whose only Retain card is an attack must not read as block-retain: {string.Join(",", retainNames)}");
        Clear(); Give<DefendSilent>(4); Give<StrikeSilent>(2); Give<Fade>(1);
        var dexterityNames = Archetypes.Detect(bot.Deck.Cards.ToList()).Select(match => match.Name).ToArray();
        if (!dexterityNames.Contains("block-dexterity"))
            throw new Exception($"A block deck with a Dexterity card must read as block-dexterity: {string.Join(",", dexterityNames)}");
        Console.WriteLine("PASS: the block route is driven by Dexterity, not by the Retain keyword.");

        // No recognised route must leave the valuation exactly as it was: this is
        // the guarantee that an unknown or modded deck cannot be made worse.
        ClearOutsider(); GiveOutsider<StrikeIronclad>(5); GiveOutsider<DefendIronclad>(4);
        var plainDeck = outsider.Deck.Cards.ToList();
        foreach (var card in plainDeck)
            if (Math.Abs(Archetypes.Bonus(CardProfile.Of(card), card, plainDeck, outsider)) > 0.01)
                throw new Exception("An unrecognised deck must receive no archetype bonus at all.");
        Console.WriteLine("PASS: an unrecognised deck receives no archetype bonus.");

        // A curse or status must never score as a card worth taking, and must
        // always be a removal target. The old card valuation had this guard;
        // the unified valuation has to keep it (caught by the Codex cross-check,
        // where unplayable cards read as cost 0 and thus "free and cheap").
        Clear(); Give<StrikeIronclad>(5); Give<DefendIronclad>(4);
        var curse = routeCombat.CreateCard<Decay>(outsider);
        outsider.Deck.AddInternal(curse);
        if (BuildValue.Marginal(curse, outsider).Total > -100)
            throw new Exception($"A curse must never be worth adding: {BuildValue.Marginal(curse, outsider).Total:F1}.");
        if (BuildValue.Add(curse, outsider).Total > -100)
            throw new Exception("A curse must never win a reward comparison.");
        var curseRemoval = BuildValue.Remove(curse, outsider);
        if (curseRemoval.Total < 100)
            throw new Exception($"A curse must always be worth removing: {curseRemoval.Total:F1}.");
        if (CardProfile.Of(curse).Roles.Contains("zerocost"))
            throw new Exception("An unplayable card must not be tagged as a free card.");
        // The shop compares everything in gold. A removal used to be compared in
        // raw deck-value units and so could never beat a real removal price.
        Clear(); Give<StrikeIronclad>(11); Give<DefendIronclad>();
        var strikeToRemove = bot.Deck.Cards.First(card => card.Id.Entry == "STRIKE_IRONCLAD");
        var removalGold = BotShopPlanner.RemovalValue(strikeToRemove, bot) * 2.2;
        if (removalGold <= 75)
            throw new Exception($"Thinning a starter must be worth more than the first removal price (75), got {removalGold:F0}.");
        Console.WriteLine("PASS: a removal is priced in the same gold unit as everything else the shop sells.");

        // A support card must keep its team value on the reward path, where it
        // used to be scored as if the deck were alone.
        var ally = Player.CreateForNewRun<Deprived>(UnlockState.all, 4);
        var mate = Player.CreateForNewRun<Deprived>(UnlockState.all, 5);
        var duo = new[] { ally, mate };
        var duoCombat = new CombatState(runState: RunState.CreateForTest(duo, seed: "BUILDVALUE-TEAM"));
        foreach (var member in duo) { member.ResetCombatState(); duoCombat.AddPlayer(member); }
        foreach (var card in ally.Deck.Cards.ToArray()) ally.Deck.RemoveInternal(card);
        for (var i = 0; i < 5; i++) ally.Deck.AddInternal(duoCombat.CreateCard<StrikeIronclad>(ally));
        var supportCard = (CardModel)duoCombat.CreateCard<Lift>(ally);
        if (TeamCoordinator.TeamBonus(supportCard, ally) <= 0)
            throw new Exception("An ally-targeted support card must carry a team bonus.");
        var withTeam = BuildValue.Marginal(supportCard, ally).Reason.Contains("team-fit");
        var alone = Player.CreateForNewRun<Deprived>(UnlockState.all, 6);
        var soloCombat = new CombatState(runState: RunState.CreateForTest(new[] { alone }, seed: "BUILDVALUE-SOLO"));
        alone.ResetCombatState(); soloCombat.AddPlayer(alone);
        if (BuildValue.Marginal(soloCombat.CreateCard<Lift>(alone), alone).Reason.Contains("team-fit"))
            throw new Exception("A solo deck must not get a team bonus.");
        if (!withTeam) throw new Exception("The reward path must carry the team bonus.");
        Console.WriteLine("PASS: the reward path carries the team bonus for support cards.");

        // The card the smith actually upgrades must be the one the upgrade
        // valuation ranks highest, not whatever the older deck-scoring picks. The
        // audit found Bash (5.4) losing to True Grit (2) because the two used
        // different algorithms.
        Clear(); Give<StrikeIronclad>(5); Give<DefendIronclad>(4);
        var bash = combat.CreateCard<Bash>(bot);
        var trueGrit = combat.CreateCard<TrueGrit>(bot);
        if (BuildValue.UpgradeDelta(bash, bot).Total <= BuildValue.UpgradeDelta(trueGrit, bot).Total)
            throw new Exception("This probe needs Bash to outrank True Grit on the upgrade valuation.");
        var upgraded = BotBrain.SelectCards(bot, new CardModel[] { trueGrit, bash }, 1, 1, "FromDeckForUpgrade");
        if (upgraded.Count != 1 || upgraded[0].Id.Entry != bash.Id.Entry)
            throw new Exception($"The smith must upgrade the card the valuation ranks highest, got "
                + $"{upgraded.FirstOrDefault()?.Id.Entry ?? "(none)"}.");
        Console.WriteLine("PASS: the smith upgrades the card the build valuation ranks highest.");

        // The baked clusters come from real runs, so every card they name must
        // exist in this game build. A patch that removes one is a silent
        // invalidation of a signature: fail loudly instead.
        var localIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var canonical in ModelDb.AllCards)
        {
            try { localIds.Add(canonical.Id.Entry); } catch { }
        }
        var unknown = BakedArchetypes.All
            .SelectMany(cluster => cluster.Cards)
            .Distinct(StringComparer.Ordinal)
            .Where(id => !localIds.Contains(id))
            .ToList();
        if (unknown.Count > 0)
            throw new Exception($"Baked archetype cards missing from this build: {string.Join(",", unknown)}");
        if (BakedArchetypes.All.Length < 40)
            throw new Exception($"The baked archetype table looks truncated: {BakedArchetypes.All.Length} clusters.");
        Console.WriteLine($"PASS: all {BakedArchetypes.All.Length} mined archetypes name cards that exist in this build.");

        // A deck holding a mined signature must be recognised by it, and the
        // cards that complete that signature must be worth more than they would
        // be in a deck going nowhere. Uses a real character, since clusters are
        // keyed by character.
        // The headless fixture cannot populate starting relics, so the character
        // is swapped in after creation (the same workaround the event tests use).
        var knight = Player.CreateForNewRun<Deprived>(UnlockState.all, 3);
        typeof(Player).GetField("<Character>k__BackingField", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!.SetValue(knight, ModelDb.Character<Ironclad>());
        var knightParty = new[] { knight };
        var knightCombat = new CombatState(runState: RunState.CreateForTest(knightParty, seed: "BUILDVALUE-CLUSTER"));
        knight.ResetCombatState(); knightCombat.AddPlayer(knight);
        foreach (var card in knight.Deck.Cards.ToArray()) knight.Deck.RemoveInternal(card);
        void GiveKnight<T>(int count) where T : CardModel
        {
            for (var i = 0; i < count; i++) knight.Deck.AddInternal(knightCombat.CreateCard<T>(knight));
        }

        GiveKnight<Bloodletting>(1); GiveKnight<Breakthrough>(1); GiveKnight<StrikeIronclad>(8);
        var clusterMatches = Archetypes.Detect(knight.Deck.Cards.ToList(), knight);
        if (clusterMatches.Count == 0 || !clusterMatches[0].Name.Contains("Bloodletting"))
            throw new Exception($"A deck holding a mined signature must be recognised: "
                + $"{string.Join(",", clusterMatches.Select(match => match.Name))}");
        if (clusterMatches[0].Signature.Count < 2)
            throw new Exception("A mined match must carry its signature cards.");
        var infernoInCluster = BuildValue.Add(knightCombat.CreateCard<Inferno>(knight), knight).Total;

        // The same card in a deck that is on no route.
        foreach (var card in knight.Deck.Cards.ToArray()) knight.Deck.RemoveInternal(card);
        GiveKnight<StrikeIronclad>(8); GiveKnight<DefendIronclad>(4);
        if (Archetypes.Detect(knight.Deck.Cards.ToList(), knight).Count != 0)
            throw new Exception("A starter deck must not match a mined cluster.");
        var infernoAlone = BuildValue.Add(knightCombat.CreateCard<Inferno>(knight), knight).Total;
        if (infernoInCluster <= infernoAlone)
            throw new Exception($"A card completing a mined signature must be worth more "
                + $"({infernoInCluster:F1} vs {infernoAlone:F1}).");
        Console.WriteLine("PASS: a mined signature is recognised and its completing cards are worth more.");

        // Card-to-card affinity comes from real picks, so every id it names must
        // exist in this build, and the floors the generator applied must still be
        // recorded (they are what makes the numbers worth trusting).
        CardModel? Local(string id)
        {
            var canonical = ModelDb.AllCards.FirstOrDefault(c => c.Id.Entry == id);
            return canonical is null ? null : knightCombat.CreateCard(canonical, knight);
        }
        var affinityIds = BakedCardAffinity.Pairs.Keys
            .Concat(BakedCardAffinity.Pairs.Values.SelectMany(pairs => pairs.Select(pair => pair.Card)))
            .Distinct(StringComparer.Ordinal).ToList();
        var affinityUnknown = affinityIds.Where(id => Local(id) is null).ToList();
        if (affinityUnknown.Count > 0)
            throw new Exception($"Baked affinity references cards this build does not have "
                + $"({string.Join(",", affinityUnknown.Take(10))}); the feed runs ahead of the game, so "
                + "re-run scripts/fetch-spire-codex-draft-recs.py then scripts/import-spire-codex-affinity.py.");
        if (BakedCardAffinity.MinOffers < 100 || BakedCardAffinity.MinPicks < 10)
            throw new Exception("Affinity sample floors are too low to trust the mined pairs.");
        Console.WriteLine($"PASS: all {affinityIds.Count} cards named by the {BakedCardAffinity.Pairs.Count} affinity entries exist in this build.");

        // A card drafted alongside another must be worth more when that partner is
        // already in the deck. Asserted on the reason, so the check does not
        // silently pass because some other term happened to move the same way.
        var firstPair = BakedCardAffinity.Pairs
            .Where(pair => pair.Value.Length > 0)
            .OrderByDescending(pair => pair.Value[0].Lift)
            .FirstOrDefault();
        if (firstPair.Key is not null && Local(firstPair.Key) is { } affinityCard && Local(firstPair.Value[0].Card) is { } partner)
        {
            foreach (var card in knight.Deck.Cards.ToArray()) knight.Deck.RemoveInternal(card);
            GiveKnight<StrikeIronclad>(5); GiveKnight<DefendIronclad>(4);
            if (BuildValue.Marginal(affinityCard, knight).Reason.Contains("drafted-together"))
                throw new Exception("Affinity must not fire on a card whose partner is absent.");
            knight.Deck.AddInternal(partner);
            if (!BuildValue.Marginal(affinityCard, knight).Reason.Contains("drafted-together"))
                throw new Exception($"Affinity must fire when {firstPair.Value[0].Card} is already in the deck.");
            Console.WriteLine($"PASS: affinity rewards drafting {firstPair.Key} alongside {firstPair.Value[0].Card}.");
        }
    }
}
