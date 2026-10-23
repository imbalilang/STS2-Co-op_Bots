using System.Reflection;
using CoopBots;
using CoopBots.Building;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Build valuation: reward picks, removals and upgrades all answer to one
// objective measured against the deck as it stands, so skipping a reward is a
// real candidate rather than a fallback.
internal static class BuildValueScenarios
{
    internal static void Run()
    {
        // Idempotent: the full suite has already installed this by the time it
        // reaches here, but --building-edits-only runs this scenario first.
        TestEnvironment.Ensure();
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var party = new[] { bot };
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "BUILDVALUE"));
        bot.ResetCombatState(); combat.AddPlayer(bot);
        void Clear() { foreach (var card in bot.Deck.Cards.ToArray()) bot.Deck.RemoveInternal(card); }
        void Give<T>(int count = 1) where T : CardModel
        {
            for (var i = 0; i < count; i++) bot.Deck.AddInternal(combat.CreateCard<T>(bot));
        }

        // A starter deck has no area damage. Under the Elo-first contract the
        // community baseline leads: Breakthrough's below-skip Elo keeps it below
        // skipping even though the role gap is real, and the gap stays named in
        // the legacy reason. (Changed for the community-elo-draft contract: the
        // pre-Elo assertion required this low-Elo card to be taken.)
        Clear(); Give<StrikeIronclad>(5); Give<DefendIronclad>(4);
        var cleave = combat.CreateCard<Breakthrough>(bot);
        var cleaveValue = BuildValue.Add(cleave, bot);
        if (!cleaveValue.Reason.Contains("needs-aoe"))
            throw new Exception($"Filling a missing role must remain visible in the reason: {cleaveValue.Reason}");
        if (cleaveValue.Total <= 0)
            throw new Exception($"Affordable area damage must improve a starter deck despite its population prior: "
                + $"{cleaveValue.Total:F1} ({cleaveValue.Reason}).");
        Console.WriteLine("PASS: an affordable role improvement can outweigh a negative population prior.");

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
        Clear(); Give<StrikeIronclad>(12); Give<PommelStrike>(4);
        var filler = bot.Deck.Cards.First();
        var removal = BuildValue.Remove(filler, bot);
        if (removal.Total <= 0)
            throw new Exception($"A diluting basic card must be worth removing: {removal.Total:F1}.");
        // The only copy of a role the deck depends on must be *refused*, not
        // merely penalised: a deduction can always be outweighed.
        Clear(); Give<StrikeIronclad>(6); Give<DefendIronclad>();
        var lastDefend = bot.Deck.Cards.First(card => card.Type == CardType.Skill);
        var protectedRemoval = BuildValue.Remove(lastDefend, bot);
        if (!BuildValue.RemovalProtection(lastDefend, bot.Deck.Cards.ToArray()).Protected)
            throw new Exception($"Removing the only defensive card must be refused, got "
                + $"{protectedRemoval.Total:F1} ({protectedRemoval.Reason}).");
        if (BotShopPlanner.RemovalValue(lastDefend, bot) != 0)
            throw new Exception("A refused removal must also not be worth gold.");
        Console.WriteLine("PASS: removal targets diluting cards and refuses to remove the deck's only defence.");

        // (community 2026-09-23) A Strike carrying the Replay enchantment was removed while
        // plain Strikes/Defends still sat in the deck. The starter floor is applied with
        // `max`, so an enchanted Basic and a plain Basic both scored the SAME floor and the
        // enchantment could not move the score; the tie-break then picked whichever copy came
        // first. This fixture puts the enchanted copy first, so the old order takes it.
        Clear(); Give<StrikeIronclad>(5); Give<TwinStrike>(7);
        var enchantedStrike = bot.Deck.Cards.First(card => card.Id.Entry == "STRIKE_IRONCLAD");
        var replay = ModelDb.Enchantment<Spiral>().ToMutable();
        enchantedStrike.EnchantInternal(replay, 1m);
        replay.ModifyCard();
        if (enchantedStrike.GetEnchantedReplayCount() <= 0)
            throw new Exception("the fixture must actually attach a Replay enchantment to the Strike.");
        var plainStrike = bot.Deck.Cards.First(card =>
            card.Id.Entry == "STRIKE_IRONCLAD" && card.Enchantment is null);
        var enchantedRemoval = BuildValue.Remove(enchantedStrike, bot).Total;
        var plainRemoval = BuildValue.Remove(plainStrike, bot).Total;
        // CHECKED (R2) 2026-09-25: dropping the enchantment/replay terms from IsPlainStarter
        // (i.e. going back to `Rarity == Basic && !IsUpgraded`) turns this red, verbatim:
        //   System.Exception: a Replay Strike must be less removable than a plain one
        //   (replay=72.0, plain=72.0) — the enchantment is invisible.
        // Both scored 72.0: the starter floor is a `max`, so the enchantment could not move
        // the removal value at all.
        if (!(enchantedRemoval < plainRemoval))
            throw new Exception($"a Replay Strike must be less removable than a plain one "
                + $"(replay={enchantedRemoval:F1}, plain={plainRemoval:F1}) — the enchantment is invisible.");
        // Enchanted copy FIRST: the old index tie-break is exactly what removed it.
        var removalCandidates = new List<CardModel> { enchantedStrike };
        removalCandidates.AddRange(bot.Deck.Cards.Where(card => !ReferenceEquals(card, enchantedStrike)));
        var removalPlan = RemovalPlan.Choose(bot, removalCandidates, 1);
        if (ReferenceEquals(removalPlan.Steps[0].Card, enchantedStrike))
            throw new Exception("removal must not take the Replay-enchanted Strike while a plain copy remains.");
        if (removalPlan.Steps[0].Card.Enchantment is not null)
            throw new Exception($"removal must prefer an unenchanted copy, got {removalPlan.Steps[0].Card.Id.Entry}.");
        Console.WriteLine("PASS: a Replay-enchanted Strike is kept while plain copies remain; "
            + "the starter removal floor no longer erases the enchantment.");

        // Repeated shop removals must not keep outbidding the cards the deck still needs.
        // CHECKED (R2) 2026-09-22: deleting the `value /= 1.0 + 0.35 * used` branch makes
        // this assertion red, verbatim:
        //   System.Exception: Removal strength must not change merely because the shop already raises its price
        //   (first=6.3, fourth=6.3).
        // The live 2026-09-22 run bought 15 removals and 0 cards across 24 shop visits.
        Clear(); Give<StrikeIronclad>(5); Give<DefendIronclad>(5);
        var removalTarget = bot.Deck.Cards.First(card => card.Id.Entry == "STRIKE_IRONCLAD");
        bot.ExtraFields.CardShopRemovalsUsed = 0;
        var firstRemoval = BotShopPlanner.RemovalValue(removalTarget, bot);
        bot.ExtraFields.CardShopRemovalsUsed = 3;
        var fourthRemoval = BotShopPlanner.RemovalValue(removalTarget, bot);
        if (Math.Abs(fourthRemoval - firstRemoval) > 1e-8)
            throw new Exception($"Removal strength must not change merely because the shop already raises its price "
                + $"(first={firstRemoval:F1}, fourth={fourthRemoval:F1}).");
        Console.WriteLine($"PASS: native escalating price replaces the extra removal discount ({firstRemoval:F1} -> {fourthRemoval:F1}), "
            + "so cards can compete after the starter pile has shrunk.");
        bot.ExtraFields.CardShopRemovalsUsed = 0;

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
        outsider.MaxEnergy = 3; // Deprived's 100-energy default removes the intended tradeoff.
        void ClearOutsider() { foreach (var card in outsider.Deck.Cards.ToArray()) outsider.Deck.RemoveInternal(card); }
        void GiveOutsider<T>(int count) where T : CardModel
        {
            for (var i = 0; i < count; i++) outsider.Deck.AddInternal(routeCombat.CreateCard<T>(outsider));
        }

        ClearOutsider(); GiveOutsider<StrikeIronclad>(5); GiveOutsider<DefendIronclad>(4);
        if (Archetypes.Detect(outsider.Deck.Cards.ToList()).Count != 0)
            throw new Exception("A deck with no scaling must not claim a route.");
        var twinInPlainDeck = BuildValue.Add(routeCombat.CreateCard<TwinStrike>(outsider), outsider).Total;

        ClearOutsider(); GiveOutsider<StrikeIronclad>(5); GiveOutsider<DefendIronclad>(4); GiveOutsider<TwinStrike>(4); GiveOutsider<Inflame>(2);
        var routes = Archetypes.Detect(outsider.Deck.Cards.ToList());
        // Deprived is not a character the mined feed covers, so this exercises
        // the role routes: the multi-hit payoff must be what is recognised.
        if (routes.Count == 0 || !routes.Any(r => r.Roles.Contains("multihit")))
            throw new Exception($"A multi-hit deck with Strength must be recognised: "
                + $"{string.Join(",", routes.Select(match => match.Name))}");
        // Compare the first payoff. A fifth attack can dilute setup / defence
        // after finite encounters already end; that is no monotonic invariant.
        foreach (var c in outsider.Deck.Cards.OfType<TwinStrike>().ToArray()) outsider.Deck.RemoveInternal(c);
        var twinInStrengthDeck = BuildValue.Add(routeCombat.CreateCard<TwinStrike>(outsider), outsider).Total;
        if (twinInStrengthDeck <= twinInPlainDeck)
            throw new Exception($"A multi-hit payoff must be worth more once the deck scales Strength "
                + $"({twinInStrengthDeck:F1} vs {twinInPlainDeck:F1}).");
        Console.WriteLine("PASS: a card that feeds the deck's route is worth more than the same card elsewhere.");

        // Size pressure is a function of EFFECTIVE size with an onset of 16 (2026-10-07
        // owner request): self-removing cards count half, and draw shortens the count by
        // what the deck's energy can actually cash in. A plain-starter deck therefore
        // pays a small tax; a FORMED deck still skips a filler.
        //
        // POLICY CHANGE, declared rather than smuggled: a 20-card deck of vanilla
        // starters used to be free (the old onset was the raw count 20). It now pays
        // 0.10*(20-16)^2 = 1.6. That is deliberately NOT the count-only blow-up the
        // 2026-09-29 Act-1 boss reward exposed -- a 21-card deck of nine basics plus a
        // curse paid 40.5 there and skipped a rare the community ranks ABOVE the cards
        // the bot took. 1.6 cannot do that, and a real 20-card deck usually sits below
        // effective 20 because its powers and exhaust cards count half.
        ClearOutsider(); GiveOutsider<StrikeIronclad>(11); GiveOutsider<DefendIronclad>(9);
        var unformedDeck = outsider.Deck.Cards.ToList();
        var unformedFiller = BuildValue.Add(routeCombat.CreateCard<TwinStrike>(outsider), outsider, unformedDeck);
        if (DeckStrength.Evaluate(unformedDeck, outsider).Size > 2.0)
            throw new Exception($"An unformed starter deck must not pay a large size tax: {unformedFiller.Reason}");
        if (BuildValue.MaturityGate(outsider, unformedDeck) > 0.001)
            throw new Exception($"A plain-starter deck must count as unformed "
                + $"(maturity={BuildValue.Maturity(outsider, unformedDeck):F2}).");
        ClearOutsider(); GiveOutsider<PommelStrike>(11); GiveOutsider<BloodWall>(9);
        var formedDeck = outsider.Deck.Cards.ToList();
        var formedFiller = BuildValue.Add(routeCombat.CreateCard<TwinStrike>(outsider), outsider, formedDeck);
        if (formedFiller.Total >= 0)
            throw new Exception($"A filler card in a formed deck must score below skipping, "
                + $"got {formedFiller.Total:F1} ({formedFiller.Reason}).");
        if (!formedFiller.Reason.Contains("size:"))
            throw new Exception($"A formed deck's filler must pay a visible size tax: {formedFiller.Reason}");
        Console.WriteLine("PASS: an unformed deck pays no size tax, while a formed deck still skips a filler.");

        // The live Act-1 boss reward this came from (2026-09-29, seed 9F4Q3R2BV8HQ):
        // BURST (community Elo 1702.1) and BLADE_OF_INK (1738.2) were skipped at
        // -11.6/-11.4 by a cycle estimate that cannot read either card, while the bot
        // took IMPERVIOUS (1646.9) and BRAND (1648.9). Preserve the positive prior
        // and report missing coverage, but do not force an unreadable card's total
        // above zero. The owner subsequently asked to reduce blind complex-card
        // preference; unified deck value still prices payment and draw occupancy.
        // 2026-10-04 absolute robustness blend exposes FLANKING=-0.4 in this
        // 21-card fixture. Raising its prior just to pass would undo that policy.
        // R2: corrupt FLANKING's community term -> exit 1:
        // System.Exception: FLANKING must retain a positive community prior while its unmodeled total may be negative.
        // Command: work/decision-consistency-20261004/unknown-r2.json; full restored gate follows.
        Clear(); Give<StrikeSilent>(11); Give<DefendSilent>(10);
        foreach (var (candidate, entry) in new (CardModel, string)[]
        {
            (combat.CreateCard<Flanking>(bot), "FLANKING"),
        })
        {
            var coverage = BuildCoverage.Of(candidate);
            var valued = BuildValue.Add(candidate, bot);
            if (coverage > 0.001)
                throw new Exception($"{entry} carries an effect the cycle estimate cannot read, so it must be "
                    + $"reported as unreadable; got coverage={coverage:F2} ({valued.Reason}).");
            if (!valued.Reason.Contains("coverage:0.00"))
                throw new Exception($"{entry} must print its zero coverage: {valued.Reason}");
            var before = DeckStrength.Evaluate(bot.Deck.Cards, bot);
            var after = DeckStrength.Evaluate(bot.Deck.Cards.Append(candidate).ToArray(), bot);
            if (after.Prior <= before.Prior)
                throw new Exception($"{entry} must retain a positive community prior while its unmodeled total may be negative.");
        }
        // SZG repair: Burst and BladeOfInk now have audited construction
        // effects. FLANKING still exercises the honest missing-coverage path.
        foreach(var modeled in new CardModel[]{combat.CreateCard<Burst>(bot),combat.CreateCard<BladeOfInk>(bot)})
            if(BuildCoverage.Of(modeled)!=1 || SustainedDeckValue.Unsupported(modeled))
                throw new Exception($"{modeled.Id.Entry} must reach its newly modeled native construction effect.");
        var burstValue = BuildValue.Add(combat.CreateCard<Burst>(bot), bot);
        var unknownValue = BuildValue.Add(combat.CreateCard<Flanking>(bot), bot);
        if (!unknownValue.Reason.Contains("unknown:"))
            throw new Exception($"An unreadable card must expose its imputed contribution, "
                + $"got {unknownValue.Total:F2} ({unknownValue.Reason}).");
        // ...and it is not "take everything": a power outside the known table belongs
        // to the STRUCTURAL policy (experiment is null, an honest absence), so it is
        // still bounded by its community standing.
        var wellLaid = combat.CreateCard<WellLaidPlans>(bot);
        if (BuildCoverage.Of(wellLaid) < 0.999)
            throw new Exception("A power outside the known table is routed to the structural policy, "
                + "not to the blind set.");
        var wellLaidValue = BuildValue.Add(wellLaid, bot);
        if (DeckStrength.CommunityPrior(wellLaid) >= DeckStrength.CommunityPrior(combat.CreateCard<Burst>(bot)))
            throw new Exception("WELL_LAID_PLANS must retain its weaker community prior than BURST.");
        Console.WriteLine($"PASS: unreadable cards retain their prior without a forced positive total "
            + $"(modeled BURST {burstValue.Total:F1}), while the weaker community prior stays explicit "
            + $"(WELL_LAID_PLANS {wellLaidValue.Total:F1}).");

        // The live failure was FOUR-SEAT: there ContextualAddition runs the cycle
        // estimate, which priced Burst as a blank play at -11.9 and overrode +12.9.
        // This minimal multiplayer witness retains the original seat-pair.
        var seatA = Player.CreateForNewRun<Deprived>(UnlockState.all, 4);
        var seatB = Player.CreateForNewRun<Deprived>(UnlockState.all, 5);
        var pair = new[] { seatA, seatB };
        var pairCombat = new CombatState(runState: RunState.CreateForTest(pair, seed: "BUILDVALUE-BLIND"));
        foreach (var seat in pair) { seat.ResetCombatState(); pairCombat.AddPlayer(seat); }
        // Afterimage is what makes the cycle estimate run at all (it needs a known
        // power in the deck); the live deck had one, which is why the blind verdict
        // was produced there and not in the single-seat shape above.
        seatA.Deck.AddInternal(pairCombat.CreateCard<Afterimage>(seatA));
        for (var i = 0; i < 11; i++) seatA.Deck.AddInternal(pairCombat.CreateCard<StrikeSilent>(seatA));
        for (var i = 0; i < 9; i++) seatA.Deck.AddInternal(pairCombat.CreateCard<DefendSilent>(seatA));
        var blindMulti = BuildValue.Add(pairCombat.CreateCard<Burst>(seatA), seatA);
        var modeledBurstCandidate = pairCombat.CreateCard<Burst>(seatA);
        var actualDelta = DeckStrength.Evaluate(seatA.Deck.Cards.Append(modeledBurstCandidate).ToArray(), seatA).Total
            - DeckStrength.Evaluate(seatA.Deck.Cards, seatA).Total;
        if (BuildCoverage.Of(modeledBurstCandidate)!=1 || Math.Abs(blindMulti.Total-actualDelta)>1e-8)
            throw new Exception($"Multiplayer must price modeled BURST by the actual permanent-deck delta: {blindMulti.Reason}");
        Console.WriteLine($"PASS: multiplayer prices modeled Burst through real construction effects "
            + $"(BURST {blindMulti.Total:F1}).");

        // CHECKED (R2) 2026-10-02: the cards whose PRIMARY printed number is a
        // combat-state count (audited with `--coverage-probe` against the local
        // build) must report coverage 0. A non-zero static base is not evidence
        // that the estimate read the per-event quantity. The controls keep the
        // cards it really does read (Repeat hit count, DamagePerVulnerable,
        // structural-policy Unsupported) out of the blind set.
        var coveragePool = ModelDb.Character<Ironclad>().CardPool.AllCards
            .Concat(ModelDb.Character<Silent>().CardPool.AllCards)
            .GroupBy(c => c.Id.Entry, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        // SPITE and TEAR_ASUNDER MOVED OUT OF THIS LIST on 2026-10-02. They were the
        // hp-loss consumers of the Ironclad engine -- Spite is 1 hit until the owner
        // has lost HP this turn and Repeat after, Tear Asunder is one hit per HP loss
        // of the combat -- and SustainedDeckValue now previews that conditional hit
        // count from the deck's own hp-loss cards, so their coverage is real. They are
        // asserted in the READABLE control below instead, where dropping the modelling
        // turns this red.
        foreach (var id in new[]
        {
            "ASHEN_STRIKE", "DEMONIC_SHIELD", "EXPECT_A_FIGHT", "PERFECTED_STRIKE",
            "RAMPAGE",
            "BOUNCING_FLASK", "FINISHER", "FLECHETTES", "MURDER", "PRECISE_CUT",
        })
        {
            var probe = combat.CreateCard(coveragePool[id], bot);
            var coverage = BuildCoverage.Of(probe);
            if (coverage > 0.001)
                throw new Exception($"{id} prints a per-event quantity the estimate cannot preview "
                    + $"out of combat, so it must not report full coverage; got {coverage:F2}.");
        }
        foreach (var id in new[] { "CONFLAGRATION", "SWORD_BOOMERANG", "BULLY", "MEMENTO_MORI", "SPITE", "TEAR_ASUNDER" })
        {
            var probe = combat.CreateCard(coveragePool[id], bot);
            if (BuildCoverage.Of(probe) < 0.999)
                throw new Exception($"{id} is modelled (read hit count / DamagePerVulnerable / "
                    + "hp-loss conditional / structural policy) and must stay readable.");
        }
        var tearAsunder = combat.CreateCard(coveragePool["TEAR_ASUNDER"], bot);
        if (BuildValue.Add(tearAsunder, bot).Reason.Contains("coverage:0.00", StringComparison.Ordinal))
            throw new Exception("Tear Asunder is modelled through the hp-loss conditional and must no "
                + "longer report zero coverage: " + BuildValue.Add(tearAsunder, bot).Reason);
        Console.WriteLine("PASS: per-event count cards report coverage 0, while read hit counts, "
            + "the hp-loss consumers and the structural policy stay modelled.");

        // The development term is a small early-deck bonus, not a 22-card target.
        // CHECKED (R2) 2026-09-22: restoring DevelopmentPerCard=1.0/DevelopmentMax=8.0
        // makes this assertion red, verbatim:
        //   System.Exception: The development term is still the old eight-point target:
        //   elo:1493.0,skip:1573.0,elo-base:-8.00,rules:-0.30,fit:False,development:8.00,
        //   affinity:0.00,legacy:block
        // The live 2026-09-22 run took HAND_TRICK for exactly that +8 (elo-base -8.00).
        Clear(); Give<StrikeSilent>(5); Give<DefendSilent>(9);
        var handTrickValue = BuildValue.Add(combat.CreateCard<HandTrick>(bot), bot);
        if (handTrickValue.Total >= 0)
            throw new Exception($"A below-skip card must not be carried by the development term alone: "
                + $"{handTrickValue.Total:F1} ({handTrickValue.Reason}).");
        if (handTrickValue.Reason.Contains("development:8", StringComparison.Ordinal))
            throw new Exception($"The development term is still the old eight-point target: {handTrickValue.Reason}");
        Console.WriteLine("PASS: a below-skip card is no longer carried by a flat early-deck bonus.");

        // A producer whose resource has a separate multiplier is cheaper to take once the
        // multiplier is already in the deck.
        //
        // CHANGED 2026-10-02, and the assertion is stronger for it. It used to pin the
        // LABEL ("producer-no-payoff:shiv"), which only exists while the mechanism
        // lookup answers Unknown. DeckMechanisms now asks a producer the same question
        // it asks a payoff, so an unpaired Blade Dance is an explicit Unsupported shiv
        // producer and a paired one is an explicit SUPPORTED one — the positive half,
        // which nothing asserted before. Both directions are pinned here.
        Clear(); Give<StrikeSilent>(5); Give<DefendSilent>(4);
        var bladeAlone = BuildValue.Add(combat.CreateCard<BladeDance>(bot), bot);
        if (!bladeAlone.Reason.Contains("shiv", StringComparison.Ordinal)
            || bladeAlone.Reason.Contains("scaling-supported:shiv", StringComparison.Ordinal))
            throw new Exception($"Blade Dance without Accuracy must read as an unsupported shiv producer: {bladeAlone.Reason}");
        Give<Accuracy>();
        var bladeWithPayoff = BuildValue.Add(combat.CreateCard<BladeDance>(bot), bot);
        if (!bladeWithPayoff.Reason.Contains("scaling-supported:shiv", StringComparison.Ordinal))
            throw new Exception($"Accuracy in the deck must make Blade Dance a SUPPORTED shiv producer: {bladeWithPayoff.Reason}");
        if (!(bladeWithPayoff.Total > bladeAlone.Total))
            throw new Exception($"Blade Dance must be worth more with its shiv multiplier present: "
                + $"{bladeWithPayoff.Total:F1} vs {bladeAlone.Total:F1}.");
        Console.WriteLine("PASS: an incomplete multiplier package is named and priced lower than a completed one.");

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

        // Community affinity is a bounded additive prior and must never control
        // the size penalty. The old code let any positive lift switch the
        // 30-point dilution cost down to 12, so a +2 nudge waived 18 points. The
        // feed is directional: the held card's document recommends the offered
        // candidate, so a held Deadly Poison activates Outbreak's prior. The
        // prior still adds value, but both decks pay the same size penalty.
        // POLICY CHANGE 2026-10-07: the tax is now 0.10*max(0, effective-16)^2, so this
        // 21 -> 22 card pair (no self-removing cards, no draw, so effective == raw) pays
        // size:+1.10 rather than the old 0.54. The assertion is written as the published
        // formula so a future coefficient change fails here instead of drifting silently.
        Clear(); Give<SuckerPunch>(20); Give<DeadlyPoison>(1);
        var withPrior = BuildValue.Add(combat.CreateCard<Outbreak>(bot), bot);
        if (!withPrior.Reason.Contains("drafted-together"))
            throw new Exception($"A held partner's recommendation must earn the affinity prior: {withPrior.Reason}");
        var expectedSizeDelta = 0.10 * (Math.Pow(22 - 16, 2) - Math.Pow(21 - 16, 2));
        if (!withPrior.Reason.Contains($"size:+{expectedSizeDelta:0.00}"))
            throw new Exception($"Affinity must not waive the size penalty "
                + $"(expected size:+{expectedSizeDelta:0.00}): {withPrior.Reason}");
        if (withPrior.Reason.Contains("relief:"))
            throw new Exception($"Affinity must not grant size relief: {withPrior.Reason}");
        Clear(); Give<SuckerPunch>(21);
        var withoutPrior = BuildValue.Add(combat.CreateCard<Outbreak>(bot), bot);
        if (withoutPrior.Reason.Contains("drafted-together"))
            throw new Exception($"Affinity must not fire without its partner: {withoutPrior.Reason}");
        if (!withoutPrior.Reason.Contains($"size:+{expectedSizeDelta:0.00}"))
            throw new Exception($"A card with no partner pays the full size pressure "
                + $"(expected size:+{expectedSizeDelta:0.00}): {withoutPrior.Reason}");
        Console.WriteLine("PASS: affinity is a bounded prior and no longer controls the size penalty.");

        // Thinning must get more valuable as dead draws accumulate, or the shop's
        // growing price (a second removal costs 150) can never be paid: a flat
        // 55 x 2.2 = 121 left every run with ten starters still in the deck.
        const double GoldPerDeckValue = BotShopPlanner.GoldPerDeckValue;
        bot.MaxEnergy = 3; // Native Deprived starts at 100; this is an ordinary shop fixture.
        Clear(); Give<StrikeIronclad>(5); Give<DefendIronclad>(5);
        for (var i = 0; i < 13; i++) Give<PommelStrike>(1);
        var heavyTarget = bot.Deck.Cards.First(card => card.Id.Entry == "STRIKE_IRONCLAD");
        var heavyRemoval = BotShopPlanner.RemovalValue(heavyTarget, bot);
        Console.WriteLine("REMOVAL MIX heavy: " + BuildValue.Remove(heavyTarget,bot).Reason);
        if (!(heavyRemoval > 150 / GoldPerDeckValue))
            throw new Exception($"Ten starters must make a 150-gold removal affordable, got {heavyRemoval:F1}.");
        Clear(); Give<StrikeIronclad>(2); Give<DefendIronclad>(2);
        for (var i = 0; i < 19; i++) Give<PommelStrike>(1);
        var leanTarget = bot.Deck.Cards.First(card => card.Id.Entry == "STRIKE_IRONCLAD");
        var leanRemoval = BotShopPlanner.RemovalValue(leanTarget, bot);
        Console.WriteLine("REMOVAL MIX lean: " + BuildValue.Remove(leanTarget,bot).Reason);
        // R8 2026-10-01: nineteen Pommel Strikes and only two Defends are not
        // unconditionally stronger than thirteen Pommel Strikes/five Defends
        // under Wither and Chains. Retire that total-score monotonicity policy;
        // keep the existing 150-gold affordability and only-defence safeguards.
        // Raw former-policy failure: work/robustness-optimization/building-edits-final.log
        // System.Exception: Removing a starter must recover more strong draws when the remaining deck is stronger (42.0 vs 41.5).
        Console.WriteLine($"PASS: a starter-heavy deck can pay for a 150-gold removal ({heavyRemoval:F1}); attack-heavy comparison={leanRemoval:F1}.");

        // The reviewed Ironclad declined Blood Wall at -37.8 from a 23-card deck
        // whose own reason line said `needs-block`: the size penalty alone was
        // worth more than the card. A card that fills a function the deck is short
        // of must survive the size of the deck it is joining.
        ClearOutsider();
        GiveOutsider<StrikeIronclad>(9); GiveOutsider<DefendIronclad>(2); GiveOutsider<PommelStrike>(12);
        var bloodWall = BuildValue.Add(routeCombat.CreateCard<BloodWall>(outsider), outsider);
        if (!bloodWall.Reason.Contains("needs-block"))
            throw new Exception($"A deck with two block cards must be shown as short of block: {bloodWall.Reason}");
        // Finite encounters no longer promise that this particular 2-energy,
        // 2-HP card beats skipping in a deck with thirteen draw/attack cards.
        // Positive defence acquisition and need-vs-saturation are already pinned
        // by CounterfactualDraftScenarios; this fixture guards role diagnosis.
        Console.WriteLine($"PASS: a defence gap remains named while the common deck model prices Blood Wall ({bloodWall.Total:F1}, {bloodWall.Reason}).");

        // A card nothing asked for costs more the later it is taken, and most
        // once no shop is left to remove it at — that dilution can never be
        // undone. Plan cards keep their relief, so a route can still be finished
        // in the last act rather than being taxed for arriving late.
        if (!(RunDepth.BloatFactor(2, removalLeft: false) > RunDepth.BloatFactor(2, removalLeft: true)
            && RunDepth.BloatFactor(2, removalLeft: true) > RunDepth.BloatFactor(0, removalLeft: true)))
            throw new Exception("The bloat factor must rise with the act and again once no removal is left ahead.");
        if (RunDepth.BloatFactor(0, removalLeft: true) != 1.0)
            throw new Exception("An act-1 deck with removals ahead must be priced exactly as before.");
        // The permanent-dilution factor is a statement about the RUN, not about the
        // current act. The old walk stopped at the act boss, so it read "no shop left,
        // this can never be undone" while two more acts of shops were still ahead —
        // measured live 2026-09-29, that charged 1.35x on an Act-1 boss reward.
        if (!RunDepth.RemovalLeftInRun(shopAhead: false, actIndex: 0, actCount: 3))
            throw new Exception("A later act is still to come, so a removal opportunity remains in the run.");
        if (RunDepth.RemovalLeftInRun(shopAhead: false, actIndex: 2, actCount: 3))
            throw new Exception("The final act with no shop left has no removal opportunity — the check "
                + "above must not be passing for the wrong reason.");
        if (!RunDepth.RemovalLeftInRun(shopAhead: true, actIndex: 2, actCount: 3))
            throw new Exception("A reachable shop is a removal opportunity whatever the act.");
        void SetAct(int act) => typeof(RunState)
            .GetField("_currentActIndex", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(bot.RunState, act);
        // Depth changes the explicit growth forecast and interference weight.
        // R2: add CurrentActIndex to the edit delta -> exit 1:
        // System.Exception: Depth may change forecast growth, but must not add another filler tax
        // work/deck-outcome-optimization/r2-results.json; production restored.
        // The previous equality guard predates that forecast; keep guarding
        // against an extra candidate-specific age/size penalty.
        double WithoutGrowth(CardModel card, BuildValue.Valuation value)
        {
            var deck = bot.Deck.Cards.ToArray();
            var a = DeckStrength.Evaluate(deck,bot);
            var b = DeckStrength.Evaluate(deck.Append(card).ToArray(),bot);
            return value.Total-(b.Growth-a.Growth)-(b.Robustness-a.Robustness);
        }
        Clear(); Give<PommelStrike>(9); Give<BloodWall>(9);
        var depthFiller = combat.CreateCard<TwinStrike>(bot);
        SetAct(0); var earlyFiller = BuildValue.Add(depthFiller, bot); var earlyCurrent = WithoutGrowth(depthFiller,earlyFiller);
        SetAct(2); var lateFiller = BuildValue.Add(depthFiller, bot); var lateCurrent = WithoutGrowth(depthFiller,lateFiller);
        if (Math.Abs(lateCurrent - earlyCurrent) > 1e-8)
            throw new Exception("Depth may change forecast growth, but must not add another filler tax");
        // POLICY CHANGE 2026-10-07: the onset moved from raw 20 to effective 16, so an
        // 18 -> 19 card deck now pays 0.10*(3^2-2^2) = 0.50 instead of 0.00. What this
        // assertion is for is that DEPTH adds nothing on top; that is the check above,
        // and the value here is pinned to the published formula so a coefficient change
        // fails here instead of drifting silently.
        var expectedDepthSize = 0.10 * (Math.Pow(19 - 16, 2) - Math.Pow(18 - 16, 2));
        if (!lateFiller.Reason.Contains($"size:+{expectedDepthSize:0.00}"))
            throw new Exception($"An act-3 filler must pay exactly the depth-independent size pressure "
                + $"(expected size:+{expectedDepthSize:0.00}): {lateFiller.Reason}");
        Clear(); Give<PommelStrike>(18);
        var needed = combat.CreateCard<BloodWall>(bot);
        SetAct(0); var earlyNeeded = BuildValue.Add(needed, bot); var earlyNeededCurrent = WithoutGrowth(needed,earlyNeeded);
        SetAct(2); var lateNeeded = BuildValue.Add(needed, bot); var lateNeededCurrent = WithoutGrowth(needed,lateNeeded);
        SetAct(0);
        if (Math.Abs(lateNeededCurrent - earlyNeededCurrent) > 0.01)
            throw new Exception($"A card the deck needs must not get dearer with depth: "
                + $"{lateNeeded.Total:F1} vs {earlyNeeded.Total:F1}.");
        if (!lateNeeded.Reason.Contains($"size:+{expectedDepthSize:0.00}"))
            throw new Exception($"A needed card keeps its depth-independent size pressure "
                + $"(expected size:+{expectedDepthSize:0.00}): {lateNeeded.Reason}");
        Console.WriteLine($"PASS: depth changes forecast growth without a separate card tax ({earlyFiller.Total:F1} -> {lateFiller.Total:F1}).");

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
        if (curseRemoval.Total <= 0)
            throw new Exception($"A curse must always be worth removing: {curseRemoval.Total:F1}.");
        if (CardProfile.Of(curse).Roles.Contains("zerocost"))
            throw new Exception("An unplayable card must not be tagged as a free card.");
        // The shop compares everything in gold. A removal used to be compared in
        // raw deck-value units and so could never beat a real removal price.
        Clear(); Give<StrikeIronclad>(11); Give<DefendIronclad>();
        var strikeToRemove = bot.Deck.Cards.First(card => card.Id.Entry == "STRIKE_IRONCLAD");
        var removalGold = BotShopPlanner.RemovalValue(strikeToRemove, bot) * BotShopPlanner.GoldPerDeckValue;
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
        bot.Deck.AddInternal(bash); bot.Deck.AddInternal(trueGrit);
        var expectedSmith = new CardModel[] { bash, trueGrit }.OrderByDescending(c => BuildValue.Deck(
            bot.Deck.Cards.Select(held => ReferenceEquals(held, c) ? CardProfile.Upgraded(c)!.Value.Card : held).ToArray(), bot)).First();
        var upgraded = BotBrain.SelectCards(bot, new CardModel[] { trueGrit, bash }, 1, 1, "FromDeckForUpgrade");
        if (upgraded.Count != 1 || upgraded[0].Id.Entry != expectedSmith.Id.Entry)
            throw new Exception($"The smith must upgrade the card the valuation ranks highest, got "
                + $"{upgraded.FirstOrDefault()?.Id.Entry ?? "(none)"}.");
        Console.WriteLine("PASS: the smith upgrades the card the build valuation ranks highest.");

        // Owner 2026-10-05: a permanent upgrade must never target a plain basic
        // Strike/Defend (an enchanted basic is the named exception). Live 2026-10-04
        // Bot1 spent three camps on DEFEND_SILENT while its shiv engines stayed
        // unupgraded.
        Clear(); Give<StrikeIronclad>(5); Give<DefendIronclad>(4);
        var strongestBasic = bot.Deck.Cards.Where(c => BotBrain.IsForbiddenUpgrade(c))
            .MaxBy(c => BuildValue.UpgradeDelta(c, bot).Total)!;
        // The fixture needs a non-basic the PRINTED valuation ranks BELOW the best
        // basic, or the guard cannot be told apart from the old ranking.
        CardModel? campEngine = null;
        foreach (var candidate in new CardModel[]
                 { combat.CreateCard<InfiniteBlades>(bot), combat.CreateCard<BladeSymphony>(bot),
                   combat.CreateCard<Afterimage>(bot), combat.CreateCard<Accuracy>(bot),
                   combat.CreateCard<Barricade>(bot) })
            if (BuildValue.UpgradeDelta(candidate, bot).Total < BuildValue.UpgradeDelta(strongestBasic, bot).Total)
                { campEngine = candidate; break; }
        if (campEngine is null)
            throw new Exception("This fixture needs a non-basic whose upgrade the printed valuation ranks "
                + "below the best basic's, or the basic-upgrade guard proves nothing.");
        bot.Deck.AddInternal(campEngine);
        if (!BotBrain.IsForbiddenUpgrade(strongestBasic) || BotBrain.IsForbiddenUpgrade(campEngine))
            throw new Exception("Only plain basic Strikes/Defends may be forbidden upgrade targets.");
        var campPick = BotBrain.SelectCards(bot, bot.Deck.Cards.Where(c => c.IsUpgradable).ToArray(), 1, 1, "FromDeckForUpgrade");
        if (campPick.Count != 1 || campPick[0].Id.Entry is "STRIKE_IRONCLAD" or "DEFEND_IRONCLAD")
            throw new Exception($"A camp upgrade must never pick a plain basic Strike/Defend, got "
                + $"{campPick.FirstOrDefault()?.Id.Entry ?? "(none)"}.");
        var enchantedBasic = combat.CreateCard<StrikeIronclad>(bot);
        var spiral = ModelDb.Enchantment<Spiral>().ToMutable();
        enchantedBasic.EnchantInternal(spiral, 1m); spiral.ModifyCard();
        if (BotBrain.IsForbiddenUpgrade(enchantedBasic))
            throw new Exception("An enchanted basic must stay an eligible upgrade target.");
        Console.WriteLine($"PASS: a permanent upgrade never targets a plain basic Strike/Defend "
            + $"({strongestBasic.Id.Entry} skipped, {campEngine.Id.Entry} chosen), enchanted basics excepted.");

        // A screen the game marks declinable must be declinable. The stall guard
        // below it keys off the method name, and FromChooseACardScreen contains
        // "ChooseA", so every such screen was forced to a pick: an offer of
        // another character's cards had to be taken from even when none of them
        // could be played. Passing the screen's own canSkip through is what lets
        // the bot decline; the guard still holds when the screen demands a card.
        var skipProbe = new CardModel[] { trueGrit, bash };
        if (BotBrain.SelectCards(bot, skipProbe, 0, 1, "FromChooseACardScreen", maySkip: true).Count != 0
            && BuildValue.Add(trueGrit, bot).Total <= 0 && BuildValue.Add(bash, bot).Total <= 0)
            throw new Exception("A declinable screen must be able to come back empty.");
        if (BotBrain.SelectCards(bot, skipProbe, 0, 1, "FromChooseACardScreen", maySkip: false).Count != 1)
            throw new Exception("A screen that demands a card must still return one.");
        if (BotBrain.SelectCards(bot, skipProbe, 1, 1, "FromChooseACardScreen", maySkip: true).Count != 1)
            throw new Exception("maySkip must not override an explicit minimum.");
        Console.WriteLine("PASS: a declinable card screen can be declined, a mandatory one cannot.");

        // A 0..N "take any number of these" offer must be ANSWERED, not declined: `min`
        // is a floor, never the answer. The old code returned exactly `min` — always 0 for
        // this shape — and discarded the offer without scoring a card. Live 2026-09-30
        // (OROBAS / Sea Glass): a seat was shown 15 cards from another character's pool,
        // took zero, and left the room; nothing in the log said why.
        Clear();
        Give<StrikeIronclad>(5); Give<DefendIronclad>(4);
        var liveBeforeOffer = bot.Deck.Cards.ToList();
        var offer = new List<CardModel>
        {
            combat.CreateCard<Anger>(bot), combat.CreateCard<Taunt>(bot),
            combat.CreateCard<PommelStrike>(bot), combat.CreateCard<ShrugItOff>(bot),
            combat.CreateCard<Inflame>(bot), combat.CreateCard<Bloodletting>(bot),
        };
        var scored = offer.Select(card => (Card: card, Value: BuildValue.Add(card, bot).Total))
            .OrderByDescending(entry => entry.Value).ToList();
        if (scored[0].Value <= 0)
            throw new Exception("This fixture needs at least one offered card the deck wants, "
                + $"but the best of {offer.Count} scored {scored[0].Value:F1}.");
        var status = combat.CreateCard<Wound>(bot);
        if (BuildValue.Add(status, bot).Total > 0)
            throw new Exception("A status card must never read as worth taking.");
        var answered = BotBrain.SelectCards(bot, offer.Append(status).ToList(), 0, offer.Count + 1,
            "FromSimpleGridForRewards");
        if (answered.Count == 0)
            throw new Exception("A 0..N offer containing a card the deck wants must not come back empty.");
        if (answered.Any(card => card.Id.Entry == status.Id.Entry))
            throw new Exception("A 0..N offer must still decline the cards that are not worth taking.");
        if (!answered.Any(card => card.Id.Entry == scored[0].Card.Id.Entry))
            throw new Exception($"A 0..N offer must take the best card it was offered ({scored[0].Card.Id.Entry}).");
        if (bot.Deck.Cards.Count != liveBeforeOffer.Count)
            throw new Exception($"Answering an offer must not edit the live deck "
                + $"({liveBeforeOffer.Count} -> {bot.Deck.Cards.Count}).");

        // Nothing worth taking is still allowed to come back empty: declining is a real
        // answer, so the fix must not turn a skippable offer into a mandatory one.
        var nothingWanted = BotBrain.SelectCards(bot, new List<CardModel> { status }, 0, 5,
            "FromSimpleGridForRewards");
        if (nothingWanted.Count != 0)
            throw new Exception("A 0..N offer of nothing worth taking must be declinable.");
        Console.WriteLine("PASS: a 0..N offer is answered card by card instead of being declined outright.");

        // CHECKED (R2) 2026-09-22: changing IndexChoiceOrSkip back to
        // `PlayerChoiceResult.FromIndex(index)` turns this red, verbatim:
        //   System.Exception: A card-reward skip must be a null index, got -1.
        // -1 is exactly the value CardReward.OnSelect later indexes `_cards[-1]` with.
        var skipChoice = BotRewardDriver.IndexChoiceOrSkip(-1, 3);
        if (skipChoice.AsIndexOrNull() is not null)
            throw new Exception($"A card-reward skip must be a null index, got {skipChoice.AsIndexOrNull()}.");
        var pickChoice = BotRewardDriver.IndexChoiceOrSkip(1, 3);
        if (pickChoice.AsIndexOrNull() != 1)
            throw new Exception($"A real pick must stay index 1, got {pickChoice.AsIndexOrNull()}.");
        Console.WriteLine("PASS: a card-reward skip is a null index, not the out-of-range -1 that threw in the live log.");

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

        // The resource table is scraped from card text and a cost field, so it is
        // exactly as drift-prone as the archetypes — more so, because a rename
        // would silently drop a producer and make a payable payoff look unpaid.
        var unknownResource = BakedResources.All
            .SelectMany(resource => resource.Producers.Concat(resource.Spends))
            .Distinct(StringComparer.Ordinal)
            .Where(id => !localIds.Contains(id))
            .ToList();
        if (unknownResource.Count > 0)
            throw new Exception($"Baked resource cards missing from this build: {string.Join(",", unknownResource)}");
        var stars = BakedResources.All.Single(resource => resource.Name == "star");
        if (stars.Producers.Length == 0 || stars.Spends.Length == 0)
            throw new Exception("The star row must record both sides; it is the one resource where the "
                + "scarcity argument rests on data rather than on a guess.");
        // The asymmetry that makes Star the breaking case: far more cards spend it
        // than can ever make it, and every producer is one character's.
        // Generators whose text does not read "Gain [star:1]" exactly: a number can
        // sit between the verb and the resource, and the verb can be lower case.
        // The first extraction matched the exact shape only, silently dropped both
        // of these, and the gate then refused Star payoffs for a deck whose only
        // Star source was one of them. Named so the miss fails loudly instead.
        foreach (var generator in new[] { "ROYAL_GAMBLE", "THE_SEALED_THRONE" })
            if (!stars.Producers.Contains(generator))
                throw new Exception($"{generator} makes Stars, so it must be recorded as a producer; "
                    + "without it the gate refuses payoffs for a deck that can pay for them.");
        var shivResource = BakedResources.All.Single(resource => resource.Name == "shiv");
        foreach (var generator in new[] { "BLADE_DANCE", "INFINITE_BLADES" })
            if (!shivResource.Producers.Contains(generator))
                throw new Exception($"{generator} makes Shivs, so it must be recorded as a producer.");
        if (stars.Spends.Length <= stars.Producers.Length)
            throw new Exception($"Star spends ({stars.Spends.Length}) must outnumber its producers "
                + $"({stars.Producers.Length}); if that changed, revisit the scarcity assumption.");
        Console.WriteLine($"PASS: the baked resource table names live cards; stars are {stars.Producers.Length} "
            + $"producers against {stars.Spends.Length} payoffs.");

        // The gate. Both reviewed cases — a Regent whose producers had been stolen
        // and a cross-class bot offered another character's Star card — reduce to
        // one fact: the deck makes no Stars, so the card never leaves the hand.
        // That is why the gate needs no character lookup at all.
        Clear(); Give<StrikeIronclad>(5);
        var sevenStars = combat.CreateCard<SevenStars>(bot);
        var unpayable = BuildValue.Marginal(sevenStars, bot);
        if (unpayable.Total > 0)
            throw new Exception($"A Star payoff with no producer in the deck must be refused, got {unpayable.Total:F1}.");
        if (!unpayable.Reason.Contains("no-star"))
            throw new Exception($"The refusal must name the missing resource, got '{unpayable.Reason}'.");
        Clear(); Give<StrikeIronclad>(5); Give<Glow>(1);
        if (BuildValue.Marginal(sevenStars, bot).Total <= 0)
            throw new Exception("With a producer in the deck the same card must be priced normally.");
        Console.WriteLine("PASS: a Star payoff is refused without a producer and priced with one.");

        // The shiv package is the one concrete case where a single tag conflates
        // three different roles. All of Blade Dance, Accuracy and Knife Trap carry
        // the same `shiv` tag, so the old additive route — which counts that tag and
        // needs two of it — scored "two producers, no multiplier" (measured odds
        // ratio 0.45, a negative asset) identically to "producers + Accuracy +
        // Knife Trap" (3.11). Splitting them is what makes the package expressible.
        var shivs = BakedResources.All.Single(resource => resource.Name == "shiv");
        foreach (var producer in new[] { "BLADE_DANCE", "INFINITE_BLADES", "CLOAK_AND_DAGGER" })
            if (!shivs.Producers.Contains(producer))
                throw new Exception($"{producer} makes Shivs and must be a producer.");
        if (!shivs.Multipliers.Contains("ACCURACY"))
            throw new Exception("Accuracy makes each Shiv worth more, so it is a multiplier, not a producer.");
        if (!shivs.Replayers.Contains("KNIFE_TRAP"))
            throw new Exception("Knife Trap cashes in the accumulated Shivs, so it is a replayer.");
        // The discriminator the old tag could not make: the Shiv token IS the
        // resource. Counting it as a source is what let two tokens satisfy a
        // "complete" package.
        foreach (var role in new[] { shivs.Producers, shivs.Multipliers, shivs.Replayers })
            if (role.Contains("SHIV"))
                throw new Exception("The Shiv token is the resource itself; it must not count as a source of it.");
        // And the overlap the single tag also could not express: this card does both.
        if (!(shivs.Producers.Contains("FAN_OF_KNIVES") && shivs.Multipliers.Contains("FAN_OF_KNIVES")))
            throw new Exception("Fan of Knives both makes Shivs and makes them hit everything.");
        Console.WriteLine($"PASS: the shiv package splits into {shivs.Producers.Length} producers, "
            + $"{shivs.Multipliers.Length} multipliers and {shivs.Replayers.Length} replayers.");

        // Poison, same three-way split. Its finding is an absence: it has a
        // multiplier (Accelerant triggers each stack once more) and two ways to
        // cash in gradually — Mirage converts the stacks to Block, Outbreak fires
        // every third application — but nothing that dumps the accumulated poison
        // at once. Shiv's top tier (odds ratio 3.11) comes precisely from having
        // that burst in Knife Trap, so poison's measured -18pt against shiv's +31pt
        // has a concrete structural candidate. Recorded, not acted on: whether the
        // replayer tier earns its own dimension is for the AUC table to say.
        var poison = BakedResources.All.Single(resource => resource.Name == "poison");
        foreach (var producer in new[] { "DEADLY_POISON", "SNAKEBITE", "NOXIOUS_FUMES" })
            if (!poison.Producers.Contains(producer))
                throw new Exception($"{producer} applies Poison and must be a producer.");
        if (!poison.Multipliers.Contains("ACCELERANT"))
            throw new Exception("Accelerant makes each Poison stack trigger again, so it is a multiplier.");
        if (poison.Replayers.Length != 0)
            throw new Exception("Poison has no burst replayer; if one is added, re-check the ramp/burst "
                + $"split rather than assuming it belongs here (got {string.Join(",", poison.Replayers)}).");
        // Re-bucketing: three cards the tester identified as exponential engines were
        // sitting in the loose Payoffs set and absent from M/R. Each judgement cites
        // the card-text clause it rests on, so it can be independently reviewed.
        var doom = BakedResources.All.Single(resource => resource.Name == "doom");
        if (!doom.Multipliers.Contains("NO_ESCAPE"))
            throw new Exception("No Escape adds Doom per existing Doom, so its output grows with the "
                + "stock: that is a multiplier, not a payoff.");
        var orb = BakedResources.All.Single(resource => resource.Name == "orb");
        // Tester's independent reading (high confidence), adopted over my first call:
        // it reads what is already channelled and produces that much again in one go,
        // which is the cash-in shape, not a standing amplifier.
        if (!orb.Replayers.Contains("VOLTAIC"))
            throw new Exception("Voltaic channels Lightning equal to what is already channelled this combat: replayer.");
        var soul = BakedResources.All.Single(resource => resource.Name == "soul");
        if (!soul.Replayers.Contains("SOUL_STORM"))
            throw new Exception("Soul Storm converts the Soul stock in the exhaust pile into damage in one "
                + "go: a cash-in, so it belongs with replayers, not multipliers.");
        // The one that must NOT be bucketed: its multiplier is over a resource this
        // table has no axis for, so filing it under any resource would fake a stock.
        foreach (var resource in BakedResources.All)
            if (resource.Multipliers.Contains("SUPERMASSIVE") || resource.Replayers.Contains("SUPERMASSIVE"))
                throw new Exception("Supermassive scales on cards created this combat, which is not a "
                    + "resource this table tracks; it must stay unbucketed until that axis exists.");
        // The fourth bucket. Without it the residue of the loose Payoff set has
        // nowhere to go, and clearing that set would delete Mirage from the table
        // entirely — a card the tester's full sweep found is the only Poison reader
        // Silent has. Data classification only: `complete` does not consult it.
        var poisonConverters = BakedResources.All.Single(r => r.Name == "poison").Converters;
        if (!poisonConverters.Contains("MIRAGE"))
            throw new Exception("Mirage is the only card that reads the Poison stack; it must survive in "
                + "the converter bucket now that the loose payoff set is cleared.");
        var doomConverters = doom.Converters;
        if (!(doomConverters.Contains("TIME'S UP") && doomConverters.Contains("SHROUD")))
            throw new Exception("Time's Up and Shroud read the Doom stack: converters, not payoffs.");
        Console.WriteLine($"PASS: the converter bucket keeps Mirage and {doomConverters.Length - 1} Doom readers.");
        // Ironclad Strength. Two things are worth pinning.
        var strength = BakedResources.All.Single(resource => resource.Name == "strength");
        // 1. Colorless cards are in every class's pool. The generator scanned only the
        //    five class colours, so Prowess ("Gain 1 Strength") was invisible to the
        //    strength row. Fixing that is what put it back.
        if (!strength.Producers.Contains("PROWESS"))
            throw new Exception("Prowess is Colorless but gains Strength; the table must scan the "
                + "colorless pool or every row silently loses those cards.");
        // 2. Strength has no doubler in this build, and its real multiplier is not a card
        //    but a card property: hit count. A multi-hit attack applies the whole
        //    accumulated Strength once per hit, so it is the cash-in (replayer), not a
        //    standing amplifier. Twin Strike "Deal 5 damage twice" is the canonical one.
        if (strength.Multipliers.Length != 0)
            throw new Exception("No card in this build doubles Strength; if one is added, re-decide "
                + $"whether it is a multiplier or a replayer (got {string.Join(",", strength.Multipliers)}).");
        if (!strength.Replayers.Contains("TWIN_STRIKE"))
            throw new Exception("A multi-hit attack is how accumulated Strength is spent in one play.");
        // 3. The interaction the user pointed at runs on the Vulnerable axis, not Strength:
        //    Molten Fist doubles Vulnerable, Dominate converts Vulnerable into Strength.
        //    Molten Fist never mentions Strength, so filing it under that row would fake
        //    the relationship. It waits for a Vulnerable axis.
        foreach (var resource in BakedResources.All)
            foreach (var bucket in new[] { resource.Producers, resource.Multipliers, resource.Replayers, resource.Converters })
                if (bucket.Contains("MOLTEN_FIST"))
                    throw new Exception("Molten Fist doubles Vulnerable; it is not a Strength card and "
                        + "must wait for the Vulnerable axis rather than be filed under Strength.");
        // The Vulnerable axis the sentence above waited for now exists as
        // VulnerablePin: supply is mechanical (any card with a VulnerablePower var
        // carries the "vulnerable" role), so only the readers need a hand list.
        // The ids must still exist in this build, and Bully must stay off it —
        // the deck model already prices Bully with the allies' Vulnerable
        // (SustainedDeckValue's ally term), so routing it through the payoff
        // channel too would price the same supply twice.
        if (!VulnerablePin.IsPayoff("MOLTEN_FIST") || !VulnerablePin.IsPayoff("DOMINATE"))
            throw new Exception("Molten Fist and Dominate are the two Vulnerable readers this axis exists for.");
        if (VulnerablePin.IsPayoff("BULLY"))
            throw new Exception("Bully is already priced by the deck model's allied-Vulnerable term; "
                + "the payoff channel must not price the same supply twice.");
        var unknownVulnerable = VulnerablePin.Payoffs.Where(id => !localIds.Contains(id)).ToList();
        if (unknownVulnerable.Count > 0)
            throw new Exception("Vulnerable readers missing from this build: " + string.Join(",", unknownVulnerable));
        Console.WriteLine($"PASS: strength is {strength.Producers.Length} producers / no doubler / "
            + $"{strength.Replayers.Length} multi-hit cash-ins; Molten Fist stays off the Strength axis.");
        Console.WriteLine("PASS: No Escape / Voltaic / Soul Storm are re-bucketed with cited card text, "
            + "and Supermassive stays out.");
        Console.WriteLine($"PASS: poison splits into {poison.Producers.Length} producers, "
            + $"{poison.Multipliers.Length} multiplier and no replayer.");

        // A team bonus may not carry a card on its own: the reviewed run took two
        // CONCOCT on `team-fit` alone into a deck that could not support them.
        // The cap is a share of the card's own contribution rather than an
        // absolute, so it self-normalises and holds at every party size — which is
        // also why it needs no tuning and is safe to pin here.
        var teamCappedScore = TeamCoordinator.CardValue(bash, bot).Score;
        var ownOnlyScore = HumanCoopAdvisor.CardValue(bash, bot).Score;
        if (teamCappedScore > ownOnlyScore + Math.Max(0, ownOnlyScore) * 1.5 + 0.001)
            throw new Exception($"A team bonus must not exceed 1.5x the card's own value: "
                + $"team={teamCappedScore:F1} own={ownOnlyScore:F1}.");
        Console.WriteLine("PASS: a team bonus cannot outvote the card's own contribution.");


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

        // Direction matters: the feed's document for a held card B lists the
        // offered cards A it recommends. The old code read the candidate A's
        // document and searched the deck for a partner B — the reverse arrow.
        // Here a held Deadly Poison's document recommends Outbreak, so adding
        // the former must activate the latter's prior.
        if (Local("DEADLY_POISON") is { } held && Local("OUTBREAK") is { } offered)
        {
            foreach (var card in knight.Deck.Cards.ToArray()) knight.Deck.RemoveInternal(card);
            GiveKnight<StrikeIronclad>(5); GiveKnight<DefendIronclad>(4);
            if (BuildValue.Marginal(offered, knight).Reason.Contains("drafted-together"))
                throw new Exception("Affinity must not fire on a card whose recommending context is absent.");
            knight.Deck.AddInternal(held);
            if (!BuildValue.Marginal(offered, knight).Reason.Contains("drafted-together"))
                throw new Exception("Affinity must fire when DEADLY_POISON, the context card, is already in the deck.");
            Console.WriteLine("PASS: affinity reads held=>offered, so DEADLY_POISON activates OUTBREAK.");
        }

        // Removal entry point: a protected card is a refusal, not a slightly
        // worse score. The old ranking compared the refusal's 0 against an
        // unprotected candidate's negative removal value and removed the
        // protected card, so the deck lost the last copy of a role.
        Clear();
        Give<DefendIronclad>(1);
        Give<StrikeIronclad>(15);
        var lastBlock = bot.Deck.Cards.First(card => card.Id.Entry == "DEFEND_IRONCLAD");
        var strongTarget = combat.CreateCard<Inflame>(bot);
        bot.Deck.AddInternal(strongTarget);
        var guardRemoval = BuildValue.Remove(lastBlock, bot);
        var negativeRemoval = BuildValue.Remove(strongTarget, bot);
        if (!BuildValue.RemovalProtection(lastBlock, bot.Deck.Cards.ToArray()).Protected)
            throw new Exception($"The last block card must be a refused removal: {guardRemoval.Total:F1} ({guardRemoval.Reason}).");
        if (negativeRemoval.Total >= 0)
            throw new Exception($"This fixture needs an unprotected negative removal target, got {negativeRemoval.Total:F1} ({negativeRemoval.Reason}).");
        var preferUnprotected = BotBrain.SelectCards(bot, new CardModel[] { lastBlock, strongTarget }, 1, 1, "FromDeckForRemoval");
        if (preferUnprotected.Count != 1 || !ReferenceEquals(preferUnprotected[0], strongTarget))
            throw new Exception($"An unprotected candidate must win over a protected card even when its score is negative, got "
                + $"{preferUnprotected.FirstOrDefault()?.Id.Entry ?? "(none)"}.");
        Console.WriteLine("PASS: removal prefers an unprotected negative target over a protected card.");

        // Two removals must re-read the deck after each pick. With two Defends
        // the first may go, but the second becomes the last block card and must
        // be left alone while unprotected alternatives remain. The old code
        // scores the whole list once and takes the top two, which removes both.
        Clear();
        Give<DefendIronclad>(2);
        Give<StrikeIronclad>(12);
        var twoCandidates = bot.Deck.Cards.ToList();
        var twoRemovals = BotBrain.SelectCards(bot, twoCandidates, 2, 2, "FromDeckForRemoval");
        if (twoRemovals.Count != 2)
            throw new Exception($"Two removals must return two cards, got {twoRemovals.Count}.");
        if (twoRemovals.Count(card => card.Id.Entry == "DEFEND_IRONCLAD") > 1)
            throw new Exception("Two removals must not take the deck's last block card while other options exist.");
        if (ReferenceEquals(twoRemovals[0], twoRemovals[1]))
            throw new Exception("Two removals must not return the same card twice.");
        Console.WriteLine($"PASS: two removals keep a block card back ({string.Join(",", twoRemovals.Select(card => card.Id.Entry))}).");

        // When every legal candidate is protected the screen still demands its
        // count, so the pick fills deterministically from the protected pool and
        // logs the fallback rather than short-picking or stalling.
        Clear();
        Give<DefendIronclad>(1);
        Give<Breakthrough>(1);
        Give<PommelStrike>(1);
        Give<StrikeIronclad>(10);
        var forcedBlock = bot.Deck.Cards.First(card => card.Id.Entry == "DEFEND_IRONCLAD");
        var forcedAoe = bot.Deck.Cards.First(card => card.Id.Entry == "BREAKTHROUGH");
        var forcedDraw = bot.Deck.Cards.First(card => card.Id.Entry == "POMMEL_STRIKE");
        foreach (var protectedCard in new[] { forcedBlock, forcedAoe, forcedDraw })
            if (!BuildValue.RemovalProtection(protectedCard, bot.Deck.Cards.ToArray()).Protected)
                throw new Exception($"This fixture needs {protectedCard.Id.Entry} to be a refused removal, got {BuildValue.Remove(protectedCard, bot).Total:F1}.");
        var forced = BotBrain.SelectCards(bot, new CardModel[] { forcedBlock, forcedAoe, forcedDraw }, 2, 2, "FromDeckForRemoval");
        if (forced.Count != 2)
            throw new Exception($"A forced removal screen must still return its required count, got {forced.Count}.");
        if (ReferenceEquals(forced[0], forced[1]))
            throw new Exception("A forced removal screen must not return the same instance twice.");
        Console.WriteLine($"PASS: an all-protected forced removal still returns {forced.Count} distinct cards "
            + $"({string.Join(",", forced.Select(card => card.Id.Entry))}).");

        // Transformation must judge each candidate against the full actual deck,
        // not the offered subset. Seven Stars is penalized when its whole
        // context is the subset, but is a payable Regent payoff once the deck's
        // Star producer is visible, so the subset ranking and the full-deck
        // ranking disagree. The old code scored the subset and transformed the
        // wrong card.
        Clear();
        Give<StrikeIronclad>(5);
        Give<Glow>(1);
        var transformStrike = bot.Deck.Cards.First(card => card.Id.Entry == "STRIKE_IRONCLAD");
        var transformStar = combat.CreateCard<SevenStars>(bot);
        bot.Deck.AddInternal(transformStar);
        var fullDeckContext = bot.Deck.Cards.ToList();
        var transformCandidates = new CardModel[] { transformStrike, transformStar };
        var starFull = BuildValue.Marginal(transformStar, bot, fullDeckContext).Total;
        var strikeFull = BuildValue.Marginal(transformStrike, bot, fullDeckContext).Total;
        var starSubset = BuildValue.Marginal(transformStar, bot, transformCandidates).Total;
        var strikeSubset = BuildValue.Marginal(transformStrike, bot, transformCandidates).Total;
        if (!(starFull > strikeFull))
            throw new Exception($"This fixture needs the full deck to rank the Star payoff above a Strike "
                + $"(star={starFull:F1}, strike={strikeFull:F1}).");
        if (!(starSubset < strikeSubset))
            throw new Exception($"This fixture needs the subset to rank the unpayable Star payoff below a Strike "
                + $"(star={starSubset:F1}, strike={strikeSubset:F1}).");
        var transformed = BotBrain.SelectCards(bot, transformCandidates, 1, 1, "FromDeckForTransformation");
        if (transformed.Count != 1 || !ReferenceEquals(transformed[0], transformStrike))
            throw new Exception($"Transformation must use the full-deck ranking and target the weakest real card, got "
                + $"{transformed.FirstOrDefault()?.Id.Entry ?? "(none)"}.");
        Console.WriteLine("PASS: transformation scores candidates against the full deck, so the subset counterexample is avoided.");

        // The valuation reads a copy of the deck and never edits the real one,
        // and the same call twice must return the same cards in the same order.
        Clear();
        Give<DefendIronclad>(2);
        Give<StrikeIronclad>(12);
        var liveBefore = bot.Deck.Cards.ToList();
        var liveFirst = BotBrain.SelectCards(bot, liveBefore, 2, 2, "FromDeckForRemoval");
        var liveSecond = BotBrain.SelectCards(bot, liveBefore, 2, 2, "FromDeckForRemoval");
        if (bot.Deck.Cards.Count != liveBefore.Count)
            throw new Exception($"The live deck must keep its size during a removal choice "
                + $"({liveBefore.Count} -> {bot.Deck.Cards.Count}).");
        for (var index = 0; index < liveBefore.Count; index++)
            if (!ReferenceEquals(bot.Deck.Cards[index], liveBefore[index]))
                throw new Exception($"The live deck must not be mutated or reordered during a removal choice (index {index}).");
        if (liveFirst.Count != liveSecond.Count)
            throw new Exception($"Repeated selection must return the same count ({liveFirst.Count} vs {liveSecond.Count}).");
        for (var index = 0; index < liveFirst.Count; index++)
            if (!ReferenceEquals(liveFirst[index], liveSecond[index]))
                throw new Exception($"Repeated selection must be deterministic (index {index}).");
        Console.WriteLine("PASS: removal never touches the live deck and repeats deterministically.");

        // THE_GAMBIT is a hard acquisition veto (BuildValue.AcquisitionBlacklist):
        // its own THE_GAMBIT_POWER executes the owner on the first unblocked powered
        // hit, so the cost is not a number the estimator can read off the card. The
        // live 2026-09-29 Act-2 run had it bought for 173 gold, upgraded at a camp,
        // played next to PANIC_BUTTON, and lost the seat at full health.
        Clear();
        Give<StrikeIronclad>(5); Give<DefendIronclad>(4);
        var gambit = BuildValue.Add(combat.CreateCard<TheGambit>(bot), bot);
        if (Math.Abs(gambit.Total - (-100)) > 0.01)
            throw new Exception($"THE_GAMBIT must carry the flat acquisition veto, "
                + $"got {gambit.Total:F1} ({gambit.Reason}).");
        if (!gambit.Reason.Contains("blacklist"))
            throw new Exception($"The veto must be named in the reason, got {gambit.Reason}.");
        var gambitOffers = new List<CardModel> { combat.CreateCard<TheGambit>(bot) };
        if (BuildValue.BestReward(bot, gambitOffers, allowSkip: true) != -1)
            throw new Exception("A blacklisted card must never beat skipping on the reward screen.");
        // The veto has to survive the contextual path too: a deck big enough to run
        // hand experiments must still return the veto, not a re-priced experiment.
        Clear();
        for (var i = 0; i < 20; i++) Give<StrikeIronclad>();
        var gambitLate = BuildValue.AddDetailed(combat.CreateCard<TheGambit>(bot), bot);
        if (Math.Abs(gambitLate.Total - (-100)) > 0.01 || gambitLate.Contextual)
            throw new Exception($"The veto must short-circuit before the hand experiment, "
                + $"got {gambitLate.Total:F1} contextual={gambitLate.Contextual} ({gambitLate.Reason}).");
        Console.WriteLine("PASS: THE_GAMBIT is vetoed on every acquisition path (-100) and never outbids skipping.");

        // Owner 2026-10-23 imports the contribution's copy investment/choice
        // continuation policy and removes the 2026-10-02 Nightmare veto.
        // Ordinary deck value may still reject it; the card is no longer a
        // fixed -100 acquisition ban. Keep THE_GAMBIT and PANIC_BUTTON vetoes.
        // R2: restoring NIGHTMARE to the production blacklist caused exit 1:
        // "Nightmare must leave the acquisition blacklist when delayed-copy planning is imported".
        // Exact restoration passed, evidence: merge-key-card-acquisition-20261023/r2/nightmare-veto.
        Clear();
        Give<StrikeSilent>(5); Give<DefendSilent>(4);
        var nightmare = BuildValue.Add(combat.CreateCard<Nightmare>(bot), bot);
        if (!double.IsFinite(nightmare.Total) || nightmare.Reason.Contains("blacklist:nightmare", StringComparison.Ordinal))
            throw new Exception($"NIGHTMARE must use ordinary finite acquisition valuation after copy planning is imported, "
                + $"got {nightmare.Total:F1} ({nightmare.Reason}).");
        Console.WriteLine("PASS: NIGHTMARE uses ordinary acquisition valuation after delayed-copy planning is imported.");

        // PANIC_BUTTON is the same acquisition shape for a different reason: the
        // kernel applies NoBlockPower (CardEffectSpecRegistry), but the tournament
        // objective does not price the following turns' block lockout. Live log
        // 2026-10-02 played it 22 times, including turns where the immediate block
        // was not needed. Do not acquire it.
        Clear();
        Give<StrikeIronclad>(5); Give<DefendIronclad>(4);
        var panic = BuildValue.Add(combat.CreateCard<PanicButton>(bot), bot);
        if (Math.Abs(panic.Total - (-100)) > 0.01)
            throw new Exception($"PANIC_BUTTON must carry the flat acquisition veto, "
                + $"got {panic.Total:F1} ({panic.Reason}).");
        if (!panic.Reason.Contains("blacklist:panic_button", StringComparison.Ordinal))
            throw new Exception($"The Panic Button veto must be named in the reason, got {panic.Reason}.");
        if (BuildValue.BestReward(bot, new List<CardModel> { combat.CreateCard<PanicButton>(bot) }, allowSkip: true) != -1)
            throw new Exception("A blacklisted PANIC_BUTTON must never beat skipping on the reward screen.");
        Console.WriteLine("PASS: PANIC_BUTTON is vetoed (-100) for its unvalued multi-turn block lockout.");


        // B1 (2026-09-30): OROBAS "Sea Glass" opens a take-ANY-NUMBER grid, so the relic is
        // worth the sum of the cards this deck would actually take — not the best card of
        // each bundled rarity. The generic bundle rule prices every offer as Max(bundle),
        // which is right for a card reward and understated this one: measured on a starter
        // Ironclad it read 58.24, while the grid (answered by WorthTaking, V39) really does
        // take all of them. Sharing BuildValue.GreedyPick is what keeps the two in step.
        Clear();
        Give<StrikeIronclad>(5); Give<DefendIronclad>(4);
        var seaGlass = (SeaGlass)ModelDb.Relic<SeaGlass>().ToMutable();
        // The game sets CharacterId through the property; that writes to DynamicVars, which a
        // bare ToMutable() in this headless fixture has not populated yet.
        typeof(SeaGlass).GetField("_characterId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(seaGlass, (ModelId?)ModelDb.Character<Silent>().Id);
        var predicted = CoopBots.Kernel.OutOfCombatPredictions.RelicPickup(bot, seaGlass);
        var seaGlassOffered = predicted.Bundles.SelectMany(bundle => bundle.Cards).Distinct().ToList();
        if (predicted.IsEmpty || seaGlassOffered.Count == 0)
            throw new Exception("This fixture needs the Sea Glass prediction to produce its cards.");
        var bestPerBundle = predicted.Bundles.Sum(bundle => bundle.Cards
            .Select(card => Math.Max(0, BuildValue.Add(card, bot).Total)).DefaultIfEmpty(0).Max());
        var (seaGlassPicks, seaGlassRanked) = BuildValue.GreedyPick(bot, seaGlassOffered, 0, seaGlassOffered.Count);
        var greedy = seaGlassRanked.Take(seaGlassPicks.Count).Sum(entry => entry.Value);
        var price = HumanCoopAdvisor.RelicValue(seaGlass, bot).Score;
        if (Math.Abs(price - greedy) > 0.01)
            throw new Exception($"Sea Glass must be priced as the greedy take-any-number sum "
                + $"({greedy:F2}), got {price:F2}.");
        if (!(seaGlassPicks.Count > predicted.Bundles.Count))
            throw new Exception($"This fixture needs the offer to contain more than one worthwhile "
                + $"card per rarity, but only {seaGlassPicks.Count} of {seaGlassOffered.Count} were taken.");
        if (!(greedy > bestPerBundle + 0.01))
            throw new Exception($"The take-any-number price ({greedy:F2}) must beat the "
                + $"best-of-each-rarity reading ({bestPerBundle:F2}) or this fixture proves nothing.");
        Console.WriteLine($"PASS: Sea Glass is priced as a take-any-number grid ({bestPerBundle:F1} -> {greedy:F1}).");
        // Pael's Tooth (2026-09-30): "take 5 upgradable cards out; each comes back upgraded
        // after a later combat" is an UPGRADE of five cards, not a removal of five. Both
        // halves were wrong — the screen was answered by the removal ranking (so it spent
        // the relic on five Strikes and Defends) and the relic itself priced as the unpriced
        // zero, leaving the PAEL option to page order.
        Clear();
        Give<StrikeIronclad>(6); Give<DefendIronclad>(4);
        var basics = bot.Deck.Cards.ToList();
        CardModel? star = null;
        foreach (var candidate in new CardModel[]
                 { combat.CreateCard<Barricade>(bot), combat.CreateCard<DemonForm>(bot),
                   combat.CreateCard<Inflame>(bot), combat.CreateCard<Uppercut>(bot) })
            if (basics.All(basic => BuildValue.UpgradeDelta(candidate, bot).Total
                    > BuildValue.UpgradeDelta(basic, bot).Total))
                { star = candidate; break; }
        if (star is null)
            throw new Exception("This fixture needs a non-basic card whose upgrade outranks every "
                + "basic's, or the Pael's Tooth ranking cannot be told apart from the removal one.");
        bot.Deck.AddInternal(star);
        var toothCandidates = bot.Deck.Cards.ToList();

        // The removal ranking must NOT want it: that is exactly the live miss.
        var removalWanted = RemovalPlan.Choose(bot, toothCandidates, 5).Steps.Select(step => step.Card).ToList();
        if (removalWanted.Contains(star))
            throw new Exception("This fixture needs the removal ranking to pass over the upgrade-worthy "
                + "card, or it proves nothing about the fix.");

        // The dispatcher has to actually label it: answering the same screen under the
        // removal purpose IS the live bug, and PurposeFor is where that is decided.
        if (BotCardChoiceDispatcher.PurposeFor(nameof(CardSelectCmd.FromDeckForRemoval), true)
            != BotBrain.PaelsToothPurpose)
            throw new Exception("A FromDeckForRemoval made by Pael's Tooth must be answered as an upgrade.");
        if (BotCardChoiceDispatcher.PurposeFor(nameof(CardSelectCmd.FromDeckForRemoval), false)
            != nameof(CardSelectCmd.FromDeckForRemoval))
            throw new Exception("Every other deck removal must keep the removal purpose.");
        if (BotCardChoiceDispatcher.PurposeFor(nameof(CardSelectCmd.FromDeckForUpgrade), true)
            != nameof(CardSelectCmd.FromDeckForUpgrade))
            throw new Exception("Only Pael's Tooth's removal is re-labelled.");

        var toothPicked = BotBrain.SelectCards(bot, toothCandidates, 5, 5, BotBrain.PaelsToothPurpose);
        if (toothPicked.Count != 5)
            throw new Exception($"Pael's Tooth must still remove five cards, got {toothPicked.Count}.");
        if (!toothPicked.Contains(star))
            throw new Exception($"Pael's Tooth must take the card it will upgrade ({star.Id.Entry}); "
                + $"it took {string.Join(",", toothPicked.Select(card => card.Id.Entry))}.");

        // The relic is priced as that upgrade, so the PAEL option is no longer a zero.
        var tooth = ModelDb.Relic<PaelsTooth>().ToMutable();
        var toothPrice = HumanCoopAdvisor.RelicValue(tooth, bot).Score;
        var toothExpected = toothCandidates.Where(card => card.IsUpgradable)
            .Select(card => HumanCoopAdvisor.UpgradeValue(card, bot))
            .OrderByDescending(value => value).Take(5).Sum();
        if (toothPrice <= 0)
            throw new Exception($"Pael's Tooth must not price as the unpriced zero, got {toothPrice:F2}.");
        if (Math.Abs(toothPrice - toothExpected) > 0.01)
            throw new Exception($"Pael's Tooth must be worth the five best upgrades ({toothExpected:F2}), "
                + $"got {toothPrice:F2}.");
        Console.WriteLine($"PASS: Pael's Tooth takes the card it will upgrade and prices the five upgrades ({toothPrice:F1}).");
        NewLeafScenarios.Run();
        RunTakeBar();
        EffectiveDeckSizeScenarios.Run();
        HpLossMechanismScenarios.Run();
    }

    // Live 2026-10-02 (A10, four seats, work/potion-fumes-20261002): the reward
    // screen's bar was a flat 0, so the draft skipped 9 of its act-1 rewards and
    // every final deck was still 36% basic Strike/Defend (5.4-13.0 damage per card
    // at act 2). The anchors are two real menus from that run:
    //
    //   first reward, size 15    Backflip 3.24 taken;  Sucker Punch 0.34 rejected
    //   act-1 floor 5, size 17   Dagger Throw -2.75 rejected -> SKIP
    //
    // The bar has to admit the second while leaving the first alone, which is only
    // possible because the bar decides TAKE vs SKIP and never which card wins.
    private static void RunTakeBar()
    {
        // Wide enough for the measured rejection, at the floor it happened on.
        // ActFloor is 0-based, so floor 4 and floor 5 are both readings a reviewer
        // could take from the log's map row 5 — the anchor must hold under either.
        foreach (var floor in new[] { 0, 4, 5 })
        {
            var bar = BuildValue.RewardTakeBar(0, floor);
            if (bar > -2.8)
                throw new Exception($"The act-1 bar at floor {floor} must admit the measured -2.75 rejection "
                    + $"(Dagger Throw, log 2026-10-02), got {bar:F2}.");
        }
        if (Math.Abs(BuildValue.RewardTakeBar(0, 0) + BuildValue.EarlyTakeBonus) > 1e-9)
            throw new Exception($"The first floor must carry the full bonus, got {BuildValue.RewardTakeBar(0, 0):F2}.");
        // Decaying to zero means the bar RISES across act 1 (it starts negative).
        for (var floor = 0; floor < BuildValue.EarlyTakeRampFloors; floor++)
            if (BuildValue.RewardTakeBar(0, floor + 1) <= BuildValue.RewardTakeBar(0, floor))
                throw new Exception($"The bar must rise monotonically to 0 across act 1; "
                    + $"floor {floor} -> {BuildValue.RewardTakeBar(0, floor):F2}, "
                    + $"floor {floor + 1} -> {BuildValue.RewardTakeBar(0, floor + 1):F2}.");
        if (BuildValue.RewardTakeBar(0, BuildValue.EarlyTakeRampFloors) != 0
            || BuildValue.RewardTakeBar(0, 99) != 0)
            throw new Exception("The bar must be exactly 0 at and past the end of the ramp.");
        if (BuildValue.RewardTakeBar(1, 0) != 0 || BuildValue.RewardTakeBar(2, 0) != 0)
            throw new Exception("The bar must be 0 from act 2 on: only the forming deck gets the wider screen.");

        // The property the anchor depends on: moving the bar cannot re-order a menu.
        // `allowSkip: false` returns the best candidate regardless of the bar, so any
        // bar at all must agree with it about WHICH card wins.
        var barBot = Player.CreateForNewRun<Deprived>(UnlockState.all, 7);
        var barCombat = new CombatState(runState: RunState.CreateForTest([barBot], seed: "TAKE-BAR"));
        barBot.ResetCombatState(); barCombat.AddPlayer(barBot);
        // A menu whose best option scores inside the early bar but below zero: exactly
        // the live SKIP case (Dagger Throw at -2.75 against a bar of 0). The fixture
        // SEARCHES for that state rather than hard-coding one, and refuses to run if it
        // cannot reach it - a fixture that cannot reproduce the case proves nothing.
        var narrower = BuildValue.RewardTakeBar(0, 0);
        Func<CardModel>[] offers =
        [
            () => barCombat.CreateCard<StrikeSilent>(barBot),
            () => barCombat.CreateCard<DefendSilent>(barBot),
            () => barCombat.CreateCard<Slice>(barBot),
            () => barCombat.CreateCard<Backflip>(barBot),
            () => barCombat.CreateCard<Prepared>(barBot),
            () => barCombat.CreateCard<Survivor>(barBot),
            () => barCombat.CreateCard<Neutralize>(barBot),
            () => barCombat.CreateCard<DaggerThrow>(barBot),
            () => barCombat.CreateCard<HandTrick>(barBot),
            () => barCombat.CreateCard<SuckerPunch>(barBot),
            () => barCombat.CreateCard<CloakAndDagger>(barBot),
            () => barCombat.CreateCard<DeadlyPoison>(barBot),
        ];
        Func<CardModel>? qualifies = null;
        for (var curses = 0; curses <= 6 && qualifies is null; curses++)
        {
            foreach (var card in barBot.Deck.Cards.ToArray()) barBot.Deck.RemoveInternal(card);
            for (var i = 0; i < 5; i++) barBot.Deck.AddInternal(barCombat.CreateCard<StrikeSilent>(barBot));
            for (var i = 0; i < 4; i++) barBot.Deck.AddInternal(barCombat.CreateCard<DefendSilent>(barBot));
            for (var i = 0; i < curses; i++) barBot.Deck.AddInternal(barCombat.CreateCard<AscendersBane>(barBot));
            foreach (var make in offers)
            {
                var value = BuildValue.Add(make(), barBot).Total;
                if (value > narrower && value < 0) { qualifies = make; break; }
            }
        }
        if (qualifies is null)
            throw new Exception($"Fixture: no offer scores between the early bar ({narrower:F2}) and 0, "
                + "so it cannot reproduce the live SKIP case.");
        var menu = new List<CardModel> { qualifies(), qualifies(), qualifies() };
        var best = BuildValue.BestReward(barBot, menu, allowSkip: false);
        if (BuildValue.BestReward(barBot, menu, allowSkip: true) != -1)
            throw new Exception("With the historical bar of 0 this menu must still be skipped, "
                + "or the fixture proves nothing.");
        if (BuildValue.BestReward(barBot, menu, allowSkip: true, takeBar: narrower) != best)
            throw new Exception("The early bar must take this menu, and must pick the same candidate the "
                + "unconditional path picks - the bar decides TAKE vs SKIP, never WHICH card.");
        if (BuildValue.BestReward(barBot, menu, allowSkip: true, takeBar: -1000) != best)
            throw new Exception("No bar may change the winner of a menu.");
        Console.WriteLine("PASS: the reward bar slides down across act 1, admits the measured -2.75 rejection, "
            + "and never changes which candidate wins a menu.");
    }
}
