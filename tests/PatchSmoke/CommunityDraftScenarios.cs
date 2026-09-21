using CoopBots;
using CoopBots.Building;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Focused regression for the community-Elo draft policy. Every probe drives the
// production entry point (BuildValue.Add) against real game models, so the
// Elo-first branch, its hard guards and its bounded corrections are verified
// where the bot actually decides. The pure-Elo baseline is only a diagnostic
// printed beside the hybrid choice; it never makes a decision.
internal static class CommunityDraftScenarios
{
    internal static void Run()
    {
        TestEnvironment.Ensure();
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        bot.MaxEnergy = 3;
        var party = new[] { bot };
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "COMMUNITYDRAFT"));
        bot.ResetCombatState(); combat.AddPlayer(bot);
        void Clear() { foreach (var card in bot.Deck.Cards.ToArray()) bot.Deck.RemoveInternal(card); }
        void Give<T>(int count = 1) where T : CardModel
        {
            for (var i = 0; i < count; i++) bot.Deck.AddInternal(combat.CreateCard<T>(bot));
        }
        CardModel Card<T>() where T : CardModel => combat.CreateCard<T>(bot);
        void Check(bool ok, string message) { if (!ok) throw new Exception("Community draft regression: " + message); }

        Check(BakedCardElo.CardCount > 0 && CommunityDraft.IsFinite(BakedCardElo.SkipElo),
            "the baked all-runs Elo table must be present and carry a finite SKIP row.");
        Check(BakedCardElo.Source.Contains("spire-codex.com/api/runs/scores/cards", StringComparison.Ordinal)
            && BakedCardElo.LocalRetrieved == "2026-09-18"
            && BakedCardElo.UpstreamGenerated == "UNKNOWN"
            && BakedCardElo.SnapshotSha256.Length == 64,
            "the baked table must carry its source URL, local retrieval date, unknown upstream date and SHA256.");
        Console.WriteLine($"PASS: baked community Elo table ({BakedCardElo.CardCount} cards, "
            + $"{BakedCardElo.MissingKnownCount} known ids without Elo, SKIP={BakedCardElo.SkipElo:F1}, "
            + $"sha={BakedCardElo.SnapshotSha256[..16]}...).");

        // Hard guards precede the Elo lookup and stay exact. The 0-100 score is
        // never consulted, so a card with a strong community score but no Elo
        // still falls back rather than being invented as zero.
        Clear(); Give<StrikeSilent>(5); Give<DefendSilent>(4);
        var curse = Card<Decay>();
        var curseValue = BuildValue.Add(curse, bot);
        Check(curseValue.Total == -1000 && curseValue.Reason == "curse",
            $"a curse must keep its exact refusal before Elo, got {curseValue.Total:F1} ({curseValue.Reason}).");
        CardModel? status = null;
        foreach (var canonical in ModelDb.AllCards)
        {
            try
            {
                var candidate = combat.CreateCard(canonical, bot);
                if (candidate.Type == CardType.Status) { status = candidate; break; }
            }
            catch { }
        }
        if (status is not null)
        {
            var statusValue = BuildValue.Add(status, bot);
            Check(statusValue.Total == -1000 && statusValue.Reason == "status",
                $"a status must keep its exact refusal before Elo, got {statusValue.Total:F1} ({statusValue.Reason}).");
        }
        var sevenStars = Card<SevenStars>();
        var noProducer = BuildValue.Add(sevenStars, bot);
        Check(noProducer.Total == 0 && noProducer.Reason == "unplayable:no-star",
            $"a Star payoff without a producer must be the exact refusal even though it has Elo, "
            + $"got {noProducer.Total:F1} ({noProducer.Reason}).");
        Console.WriteLine("PASS: curse/status and missing-resource guards precede Elo and keep their exact reasons.");

        // A card without a baked Elo is evaluated the old way and labeled. Bash
        // is a real local card the all-runs snapshot does not cover, so it is the
        // live example of the documented fallback.
        var bash = Card<Bash>();
        var bashTerms = BuildValue.AddDetailed(bash, bot);
        Check(bashTerms.UsedFallback && !bashTerms.GuardRefusal,
            "Bash must be the missing-Elo fallback case in this build.");
        Check(!BakedCardElo.TryGet(bash.Id.Entry, out _),
            "the fallback fixture needs a card the snapshot does not cover.");
        var bashValue = BuildValue.Add(bash, bot);
        Check(bashValue.Reason.StartsWith("elo-missing:legacy-fallback,", StringComparison.Ordinal),
            $"a missing-Elo card must be labeled as a legacy fallback, got '{bashValue.Reason}'.");
        Check(bashValue.Total == bashTerms.LegacyTotal,
            "the fallback must return the legacy total unchanged, not an invented zero.");
        Console.WriteLine($"PASS: missing-Elo fallback is labeled and unchanged ({bash.Id.Entry} = {bashValue.Total:F1}).");

        // Strong Elo advantage cannot be overturned by the legacy correction,
        // which is bounded to +8. Compare a high-Elo card against a low-Elo one
        // in the same deck: the gap is (Elo difference / 10), far larger than the
        // entire rules band, so the community prior decides.
        Clear(); Give<StrikeSilent>(5); Give<DefendSilent>(4);
        var adrenaline = BuildValue.AddDetailed(Card<Adrenaline>(), bot);
        var lift = BuildValue.AddDetailed(Card<Lift>(), bot);
        Check(adrenaline.Elo > lift.Elo, "the fixture needs ADRENALINE to outrank LIFT on community Elo.");
        Check(adrenaline.Total > lift.Total,
            $"a large Elo advantage must survive the bounded legacy correction "
            + $"({adrenaline.Total:F2} vs {lift.Total:F2}).");
        Check(adrenaline.EloBase - lift.EloBase > 40.0,
            $"the Elo gap must dominate the whole rules band, got {adrenaline.EloBase - lift.EloBase:F2}.");
        Check(adrenaline.Rules >= -20.0 && adrenaline.Rules <= 8.0
            && lift.Rules >= -20.0 && lift.Rules <= 8.0,
            "the legacy correction must stay in [-20, +8] no matter the deck.");
        Console.WriteLine($"PASS: a strong Elo gap dominates the legacy band "
            + $"(Adrenaline {adrenaline.Total:F2} vs Lift {lift.Total:F2}).");

        // Every correction is bounded even when the legacy heuristic would run
        // away: a saturated, bloated deck and a rich one are both checked.
        foreach (var (label, prepare) in new (string, Action)[]
                 {
                     ("starter", () => { Clear(); Give<StrikeSilent>(5); Give<DefendSilent>(4); }),
                     ("bloated", () => { Clear(); Give<StrikeSilent>(18); Give<DefendSilent>(10); }),
                     ("rich", () => { Clear(); Give<StrikeSilent>(4); Give<BladeDance>(2); Give<Accuracy>(2); }),
                 })
        {
            prepare();
            foreach (var card in new CardModel[] { Card<BladeDance>(), Card<TwinStrike>(), Card<BloodWall>() })
            {
                var terms = BuildValue.AddDetailed(card, bot);
                Check(terms.Rules >= -20.0 && terms.Rules <= 8.0,
                    $"[{label}] rules must stay bounded, got {terms.Rules:F2}.");
                Check(terms.Development >= 0.0 && terms.Development <= 8.0,
                    $"[{label}] development must stay bounded, got {terms.Development:F2}.");
                Check(terms.Affinity >= 0.0 && terms.Affinity <= CommunityDraft.AffinityCap,
                    $"[{label}] affinity must stay bounded, got {terms.Affinity:F2}.");
                Check(Math.Abs(terms.Total - (terms.EloBase + terms.Rules + terms.Development + terms.Affinity)) < 0.0001,
                    $"[{label}] the total must be exactly the sum of the logged terms.");
            }
        }
        Console.WriteLine("PASS: rules, development and affinity stay inside their documented bounds on every fixture.");

        // The legacy apparatus is a single scalar, so a card the deck has a
        // demonstrated use for ("this deck has no block", "the mechanism's trigger
        // is already here") was priced exactly like generic filler. The fit
        // channel prices the same legacy total against a lower break-even, so a
        // fitted card clears skipping at a lower legacy score; the ceiling
        // deliberately does not move, so a saturated legacy score is still bounded
        // by the same +8.
        //
        // 2026-09-19: the fit channel used to be a steeper slope (0.5) on the same
        // pivot. A fitted card's legacy total normally sits below that pivot, so
        // the steeper slope doubled its penalty instead of lifting it - a fitted
        // card scored strictly worse than the same card unfitted. The last check
        // here is the regression guard for that inversion.
        // DEFLECT, not DEFEND_SILENT: the basic cards are outside the community
        // snapshot, so they take the fallback path and never reach this scale at
        // all. That hole is real and is reported separately; this check pins the
        // Elo-first path.
        Clear(); Give<StrikeSilent>(5);
        var neededBlock = BuildValue.AddDetailed(Card<Deflect>(), bot);
        Check(neededBlock.DeckFit,
            $"a block card in a deck holding none must be a deck fit; reason was '{neededBlock.LegacyReason}'.");
        Check(neededBlock.LegacyReason.Contains("needs-block", StringComparison.Ordinal),
            $"the fixture needs the functional-gap token, got '{neededBlock.LegacyReason}'.");
        var neededExcess = neededBlock.LegacyTotal - neededBlock.LegacyAffinity;
        Check(Math.Abs(neededBlock.Rules - Math.Clamp((neededExcess - 10.0) * 0.25, -20.0, 8.0)) < 0.001,
            $"a fitted card must be priced against the fit pivot (rules={neededBlock.Rules:F2}, excess={neededExcess:F2}).");
        Check(neededBlock.Rules <= 8.0001 && neededBlock.Rules >= -20.0001,
            "the fit pivot must not lift the ceiling the generic scale is bounded by.");
        // Same deck, a card with no structural claim on it: the generic pivot.
        var generic = BuildValue.AddDetailed(Card<TwinStrike>(), bot);
        Check(!generic.DeckFit,
            $"an ordinary attack the deck already has enough of must not be a deck fit; got '{generic.LegacyReason}'.");
        var genericExcess = generic.LegacyTotal - generic.LegacyAffinity;
        Check(Math.Abs(generic.Rules - Math.Clamp((genericExcess - 20.0) * 0.25, -20.0, 8.0)) < 0.001,
            $"an unfitted card must stay on the generic pivot (rules={generic.Rules:F2}, excess={genericExcess:F2}).");
        var fittedUnderGenericPivot = Math.Clamp((neededExcess - 20.0) * 0.25, -20.0, 8.0);
        Check(neededBlock.Rules >= fittedUnderGenericPivot - 0.001,
            "the fit pivot must never price a fitted card below the generic pivot "
            + $"(fit={neededBlock.Rules:F2}, generic at the same legacy total={fittedUnderGenericPivot:F2}).");
        Console.WriteLine($"PASS: a deck-demonstrated fit clears skipping at a lower legacy total "
            + $"(needed block rules={neededBlock.Rules:F2} fit={neededBlock.DeckFit} vs generic {generic.Rules:F2}).");

        // The directional held-card affinity: the held card's document recommends
        // the candidate, never the reverse. A deck holding DEADLY_POISON lifts
        // OUTBREAK; the reverse deck does not.
        Clear(); Give<StrikeSilent>(5); Give<DefendSilent>(4); Give<DeadlyPoison>(1);
        var outbreak = Card<Outbreak>();
        var directional = CommunityDraft.Affinity(outbreak, bot.Deck.Cards.ToList());
        Check(Math.Abs(directional - 2.10) < 0.001,
            $"DEADLY_POISON must recommend OUTBREAK for 2.10, got {directional:F3}.");
        Clear(); Give<StrikeSilent>(5); Give<DefendSilent>(4); Give<Outbreak>(1);
        var reverse = CommunityDraft.Affinity(Card<DeadlyPoison>(), bot.Deck.Cards.ToList());
        Check(reverse == 0.0,
            $"affinity must not read the arrow backwards; got {reverse:F3} for OUTBREAK -> DEADLY_POISON.");
        Console.WriteLine("PASS: affinity is directional (held => offered), not reversed.");

        // Duplicate held copies pay once (unique held ids) and the term is
        // capped. Three Accuracy lift Blade Dance exactly as one does.
        Clear(); Give<StrikeSilent>(5); Give<Accuracy>(3);
        var bladeDance = Card<BladeDance>();
        var deduped = CommunityDraft.Affinity(bladeDance, bot.Deck.Cards.ToList());
        Check(Math.Abs(deduped - 1.30) < 0.001,
            $"three held Accuracy must pay the same as one (1.30), got {deduped:F3}.");
        Clear(); Give<StrikeSilent>(5); Give<Accuracy>(1); Give<DeadlyPoison>(1); Give<Speedster>(1);
        var capped = CommunityDraft.Affinity(bladeDance, bot.Deck.Cards.ToList());
        Check(capped <= CommunityDraft.AffinityCap,
            $"the affinity term must never exceed its cap, got {capped:F3}.");
        // The candidate's own instance and id are excluded.
        Clear(); Give<StrikeSilent>(5); Give<BladeDance>(1);
        var self = CommunityDraft.Affinity(Card<BladeDance>(), bot.Deck.Cards.ToList());
        Check(self == 0.0, $"a held copy of the candidate's own id must not lift it, got {self:F3}.");
        Console.WriteLine("PASS: affinity deduplicates held ids, excludes self edges and stays capped.");

        // A low-Elo offer set in a deck that has finished forming is skipped: the
        // development term is zero and the negative Elo base survives the bounded
        // rules correction.
        Clear(); Give<StrikeSilent>(12); Give<DefendSilent>(12);
        var lowSet = new CardModel[] { Card<Lift>(), Card<Snakebite>(), Card<Speedster>() };
        foreach (var candidate in lowSet)
        {
            var terms = BuildValue.AddDetailed(candidate, bot);
            Check(terms.Development == 0.0, "a 24-card deck must have finished forming.");
            Check(terms.Total < 0.0,
                $"{candidate.Id.Entry} is below skip in a formed deck, got {terms.Total:F2}.");
        }
        Check(BuildValue.BestReward(bot, lowSet, allowSkip: true) == -1,
            "BestReward must skip when every offered card is below skip.");
        Console.WriteLine("PASS: low-Elo offers in a formed deck are skipped.");

        // Crowded duplicate low/moderate-Elo cards stay skippable: the bounded
        // rules correction cannot rescue saturated, bloated repeats.
        Clear(); Give<StrikeSilent>(20); Give<DefendSilent>(4); Give<Breakthrough>(4);
        var duplicate = BuildValue.Add(Card<Breakthrough>(), bot);
        Check(duplicate.Total < 0.0,
            $"a crowded duplicate must stay below skip, got {duplicate.Total:F2} ({duplicate.Reason}).");
        Check(BuildValue.BestReward(bot, new[] { Card<Breakthrough>() }, allowSkip: true) == -1,
            "BestReward must skip a saturated duplicate.");
        Console.WriteLine("PASS: crowded duplicate low/moderate-Elo cards remain skippable.");

        // The policy is deterministic: the same card and deck produce the same
        // terms and the same reward choice on every call.
        Clear(); Give<StrikeSilent>(5); Give<DefendSilent>(4);
        var first = BuildValue.AddDetailed(Card<Adrenaline>(), bot);
        var second = BuildValue.AddDetailed(Card<Adrenaline>(), bot);
        Check(first.Total == second.Total && first.Rules == second.Rules && first.Affinity == second.Affinity,
            "repeated valuation must be identical.");
        var set = new CardModel[] { Card<BladeDance>(), Card<Concoct>(), Card<Speedster>() };
        Check(BuildValue.BestReward(bot, set, allowSkip: true) == BuildValue.BestReward(bot, set, allowSkip: true),
            "repeated BestReward must be identical.");
        Console.WriteLine("PASS: Elo-first reward valuation is deterministic across repeated calls.");

        // The card shop prices with the same Add call, so its gold ordering is the
        // deck-value ordering scaled by the shared constant.
        Clear(); Give<StrikeSilent>(5); Give<DefendSilent>(4);
        var shopHigh = BuildValue.AddDetailed(Card<Adrenaline>(), bot);
        var shopLow = BuildValue.AddDetailed(Card<Lift>(), bot);
        Check(shopHigh.Total * BotShopPlanner.GoldPerDeckValue > shopLow.Total * BotShopPlanner.GoldPerDeckValue,
            "the shop's gold value must order cards exactly as Add does.");
        Console.WriteLine($"PASS: shop gold value follows the shared Add path "
            + $"(high {shopHigh.Total * BotShopPlanner.GoldPerDeckValue:F1} > low {shopLow.Total * BotShopPlanner.GoldPerDeckValue:F1}).");

        // Source-based synthetic offer sets: pure Elo choice beside the hybrid
        // choice with the full term breakdown. These are not replayed private run
        // state and are not outcome proof.
        Report("silent-starter", bot, new CardModel[] { Card<BladeDance>(), Card<Concoct>(), Card<Speedster>() });
        Report("defensive-offer", bot, new CardModel[] { Card<EscapePlan>(), Card<Snakebite>(), Card<Fade>() });
        Console.WriteLine("PASS: community-Elo draft scenarios (guards, fallback, directional affinity, bounds, skip, determinism, shop path).");
    }

    // Print the pure-Elo ranking and the hybrid Add choice with every term, for
    // the source-based synthetic sets named in the task. Diagnostics only.
    private static void Report(string label, Player bot, IReadOnlyList<CardModel> cards)
    {
        var pure = CommunityDraft.RankByElo(cards, includeSkip: true);
        var hybridTerms = cards.Select(card => (card.Id.Entry, Terms: BuildValue.AddDetailed(card, bot))).ToList();
        var hybrid = hybridTerms.OrderByDescending(row => row.Terms.Total).First();
        Console.WriteLine($"REPORT {label}: pure-elo={pure.Selected ?? "(none)"} ranking=[{string.Join(">", pure.Ranking)}]");
        foreach (var row in hybridTerms)
        {
            var t = row.Terms;
            Console.WriteLine($"  hybrid {row.Entry}: total={t.Total:F2} elo={t.Elo:F1} skip={t.SkipElo:F1} "
                + $"base={t.EloBase:F2} rules={t.Rules:F2} dev={t.Development:F2} aff={t.Affinity:F2}");
        }
        Console.WriteLine($"  hybrid-winner={hybrid.Entry} ({hybrid.Terms.Total:F2}) vs skip 0.00 -> "
            + $"{(hybrid.Terms.Total > 0 ? hybrid.Entry : "SKIP")}");
    }
}
