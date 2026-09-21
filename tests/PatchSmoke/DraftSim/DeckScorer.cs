using CoopBots.Building;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;

namespace DeckSim;

/// <summary>
/// What one play-out of a deck measured. Every field is a number a drafting
/// change can move, so a report can say *which* part of the deck got better
/// instead of only that the score did.
/// </summary>
internal sealed record DeckScore(
    double Power,
    double Offence,
    double Defence,
    double UpgradeScore,
    double Efficiency,
    double Consistency,
    double DamagePerTurn,
    double BlockPerTurn,
    double TurnsToKill,
    double KillRate,
    double SurvivalRate,
    double BlockCoverage,
    double DeadDrawRate,
    double EnergyWaste,
    double RampRatio,
    double PeakDamage,
    double PeakTurn,
    int Size,
    int Curses,
    int Upgrades)
{
    internal static DeckScore Empty(int turns) => new(0, 0, 0, 0, 0, 0, 0, 0, turns, 0, 0, 0, 1, 1, 1, 0, 0, 0, 0, 0);
}

/// <summary>
/// Scores a finished deck by playing it out — the "实战打分".
///
/// This is deliberately not the linear sum <see cref="BuildValue.Deck"/> computes.
/// A sum cannot see energy, draw order or the difference between a card that is
/// good on turn one and a card that is good once something else has been played,
/// which is exactly what separates a drafted deck from a pile of good cards.
/// The play-out gives the deck a shuffled draw pile, three energy a turn, a
/// discard that recycles, powers that stay in play and exhaust cards that leave
/// it, then greedily plays the best card it can afford each turn against a
/// reference fight.
///
/// The deck does have HP, and that is not decoration. The first version of this
/// scorer had none, and the community check caught what it cost: with no way to
/// lose, the highest-damage deck always won the comparison, and real Ascension 10
/// winners — which block twice what a damage-only draft does — scored below decks
/// this bot drafted. A fight the deck can die in is the whole reason defence is
/// worth a card slot.
///
/// What it is still not: a combat simulator. Enemy moves are one damage number a
/// turn, card text is read through <see cref="CardProfile"/>'s facts rather than
/// executed, and there are no relics, potions or healing. It is a deterministic,
/// cheap ordering of decks; only differences between decks scored the same way
/// mean anything.
/// </summary>
internal static class DeckScorer
{
    private const int HandSize = 5;
    private const int EnergyPerTurn = 3;

    internal static DeckScore Score(IReadOnlyList<CardModel> deck, ReferenceFight reference, SimConfig config, ulong seed)
    {
        var turns = Math.Max(1, config.ScoreTurns);
        if (deck.Count == 0) return DeckScore.Empty(turns);

        var shuffles = Math.Max(1, config.ScoreShuffles);
        double damage = 0, block = 0, waste = 0, dead = 0, drawn = 0, ramp = 0, killTurns = 0;
        double peakDamage = 0, peakTurn = 0;
        var kills = 0;
        var survived = 0;
        var perShuffleDamage = new List<double>(shuffles);

        for (var shuffle = 0; shuffle < shuffles; shuffle++)
        {
            var outcome = PlayOut(deck, reference, config, seed, shuffle);
            damage += outcome.Damage;
            block += outcome.Block;
            waste += outcome.EnergyWaste;
            dead += outcome.DeadDraws;
            drawn += outcome.Drawn;
            ramp += outcome.RampRatio;
            peakDamage += outcome.PeakDamage;
            peakTurn += outcome.PeakTurn;
            perShuffleDamage.Add(outcome.Damage);
            if (!outcome.Died) survived++;
            if (outcome.KillTurn > 0) { kills++; killTurns += outcome.KillTurn; }
        }

        var meanDamage = damage / shuffles / turns;
        var meanBlock = block / shuffles / turns;
        var coverage = reference.IncomingPerTurn <= 0 ? 1 : meanBlock / reference.IncomingPerTurn;
        var survival = (double)survived / shuffles;
        var wasted = waste / shuffles / (turns * EnergyPerTurn);
        var upgradeCount = deck.Count(card => card.IsUpgraded);

        // Both components are measured against what a real winner of this character
        // produces, not against an absolute. "Enough damage" is not a number that
        // can be picked out of the air: it is whatever the decks that beat this
        // fight actually did.
        //
        // Every component saturates above par rather than growing without limit —
        // see Saturating.
        var offence = Saturating(meanDamage / Math.Max(0.1, reference.ParDamagePerTurn));
        var defence = Saturating(meanBlock / Math.Max(0.1, reference.ParBlockPerTurn));
        // Efficiency and upgrades are not properties of the play-out's output but of
        // the deck, and the community sample says they are the two strongest
        // predictors of which finished decks actually won. Energy waste is measured
        // by the play-out (energy left unspent because nothing playable was in
        // hand); upgrades are counted off the list.
        var efficiency = Saturating((1 - wasted) / Math.Max(0.1, reference.ParEfficiency));
        var upgrades = Saturating(upgradeCount / Math.Max(1, reference.ParUpgrades));
        // Spread of damage between shuffles. Deliberately damage and not
        // turns-to-kill: a deck that cannot kill inside the turn cap has no
        // kill-turn spread at all, and would read as perfectly consistent.
        var consistency = Consistency(perShuffleDamage);

        var weights = config.Weights;
        // Viability first, weighting second.
        //
        // The weights rank decks that can win; they were measured on real finished
        // decks, every one of which killed its boss. They say nothing about a deck
        // that cannot, and a weighted average cannot express "this deck does not
        // win at all" — which is why a sweep of BuildValue.BlockWeight ran power up
        // to 48 while the kill rate collapsed from 30% to 12%: each point of block
        // bought defence weight, and nothing anywhere charged for the damage that
        // was no longer there to close the fight.
        //
        // The bar is half of par, not par. Par is the *median* of winning decks, so
        // gating at par discounts half of them — measured, that took the community
        // AUC from 0.52 to 0.45, i.e. the score started ranking winners below
        // losers. A gate has to be inert across the range real decks occupy and
        // bite only below it, and half of what a winning deck produces is the point
        // where a deck is no longer racing the boss at all.
        var viability = Math.Min(1, meanDamage / Math.Max(0.1, reference.ParDamagePerTurn * 0.5));
        // A deck that dies is not paid. Without this the survival model would only
        // shorten the fight, and a deck that dies on turn four with a big opening
        // would still read as a high-damage deck.
        var power = (weights.Offence * (offence / 2 * 100)
            + weights.Defence * (defence / 2 * 100)
            + weights.Upgrades * (upgrades / 2 * 100)
            + weights.Efficiency * (efficiency / 2 * 100)
            + weights.Consistency * (consistency * 100)) / Math.Max(1, weights.Total) * survival * viability;

        return new DeckScore(
            Power: power,
            Offence: offence / 2 * 100,
            Defence: defence / 2 * 100,
            UpgradeScore: upgrades / 2 * 100,
            Efficiency: efficiency / 2 * 100,
            Consistency: consistency * 100,
            DamagePerTurn: meanDamage,
            BlockPerTurn: meanBlock,
            // Shuffles that fail to kill inside the cap are counted at the cap, so
            // the number stays an average of the whole batch instead of of the
            // lucky draws. KillRate is what says whether it happened at all.
            TurnsToKill: (kills == 0 ? (turns + 1) * shuffles : killTurns + (shuffles - kills) * (turns + 1)) / shuffles,
            KillRate: (double)kills / shuffles,
            SurvivalRate: survival,
            BlockCoverage: coverage,
            DeadDrawRate: drawn <= 0 ? 0 : dead / drawn,
            EnergyWaste: wasted,
            RampRatio: ramp / shuffles,
            PeakDamage: peakDamage / shuffles,
            PeakTurn: peakTurn / shuffles,
            Size: deck.Count,
            Curses: deck.Count(card => card.Type == CardType.Curse),
            Upgrades: upgradeCount);
    }

    /// <summary>
    /// Output relative to par, saturating above it.
    ///
    /// Meeting par is the job, and it is the whole job: a deck that kills in time
    /// does not win harder by killing faster, it just has less room for everything
    /// else. Past par the curve flattens towards 1.35 rather than climbing, so the
    /// difference between "enough damage" and "twice enough damage" is worth a
    /// third of the distance between "no damage" and "enough".
    ///
    /// Deliberately monotone. An earlier version peaked at par and fell away above
    /// it, on the evidence that losing A10 decks out-damage winning ones in four of
    /// five characters. That comparison was of means, and the distributions behind
    /// them are not comparable — winning decks have the fatter right tail, so
    /// penalising over-production punished winners harder than losers and inverted
    /// the ranking a different way. The honest read of that evidence is the one
    /// already applied here: extra damage is worth much less than the damage that
    /// got you to par, not less than nothing. Nothing in the sample supports a
    /// score that prefers a smaller number.
    /// </summary>
    private static double Saturating(double ratio)
        => ratio <= 1 ? Math.Clamp(ratio, 0, 1) : 1 + (1 - Math.Exp(-(ratio - 1))) * 0.35;

    /// <summary>
    /// How much a deck's output moves between shuffles, as one minus the
    /// coefficient of variation of its damage. 1 is the same every draw, 0 is a
    /// coin flip — and because it is measured on damage it stays meaningful for a
    /// deck too weak to finish the reference fight at all.
    /// </summary>
    private static double Consistency(List<double> perShuffleDamage)
    {
        if (perShuffleDamage.Count < 2) return 1;
        var mean = perShuffleDamage.Average();
        if (mean <= 0.01) return 0;
        var variance = perShuffleDamage.Sum(value => (value - mean) * (value - mean)) / perShuffleDamage.Count;
        return Math.Clamp(1 - Math.Sqrt(variance) / mean, 0, 1);
    }

    private sealed record Outcome(
        double Damage, double Block, double EnergyWaste, double DeadDraws, double Drawn, double RampRatio,
        double PeakDamage, double PeakTurn, int KillTurn, bool Died);

    /// <summary>One full fight: shuffle, then <c>ScoreTurns</c> turns of play.</summary>
    private static Outcome PlayOut(IReadOnlyList<CardModel> deck, ReferenceFight reference, SimConfig config, ulong seed, int shuffle)
    {
        var rng = new Rng(seed * 1_000_003UL + (ulong)shuffle, "coopbots-deck-score");
        var drawPile = Shuffle(deck.ToList(), rng);
        var discard = new List<CardModel>();
        var exhausted = new List<CardModel>();
        var hand = new List<CardModel>();

        double strength = 0, dexterity = 0, focus = 0, vulnerable = 0, weak = 0, poison = 0;
        double damage = 0, block = 0, energyWaste = 0, deadDraws = 0, drawn = 0;
        var enemyHp = reference.EnemyHp;
        var hp = reference.PlayerHp;
        var perTurn = new double[config.ScoreTurns];
        var killTurn = 0;
        var died = false;

        for (var turn = 0; turn < config.ScoreTurns; turn++)
        {
            var turnDamage = 0.0;

            // Poison ticks before the draw and decays by one stack — the shape the
            // live power has, so a poison deck's damage arrives over time instead of
            // as one lump on the turn it is applied.
            if (poison > 0)
            {
                turnDamage += poison;
                poison = Math.Max(0, poison - 1);
            }

            drawn += Draw(drawPile, discard, hand, HandSize, out var unplayable);
            deadDraws += unplayable;

            var energy = (double)EnergyPerTurn;
            var blockThisTurn = 0.0;
            // Weak on the enemy is the cheapest defence there is, so it is modelled:
            // it lowers what this turn's block has to cover.
            var demand = reference.IncomingPerTurn * (weak > 0 ? 0.75 : 1.0);

            while (true)
            {
                var best = default(CardModel);
                var bestValue = 0.0;
                CardProfile.Facts? bestFacts = null;
                var gap = demand - blockThisTurn;
                // What this card's block is actually worth: the damage it stops,
                // and the whole fight when the alternative is dying this turn. A
                // play-out without that second term always spends its last energy
                // on damage, which is how it rated a damage-only draft above every
                // real Ascension 10 winner.
                var lethal = gap >= hp;
                foreach (var card in hand)
                {
                    var facts = CardProfile.Of(card, energy: (int)Math.Max(0, energy));
                    if (facts.Unplayable) continue;
                    if (CostOf(card, energy) > energy) continue;
                    var value = PlayValue(facts, card, strength, focus, dexterity, vulnerable,
                        demanded: Math.Max(0, gap), turnsLeft: config.ScoreTurns - turn, lethal: lethal);
                    if (value <= bestValue) continue;
                    best = card; bestValue = value; bestFacts = facts;
                }
                if (best is null || bestFacts is null || bestValue <= 0) break;

                energy -= CostOf(best, energy);
                hand.Remove(best);
                // Damage and block are resolved with the stats the card was played
                // with; anything it raises applies from the next card on. That
                // ordering is what stops a ramp card from paying itself back twice.
                var hit = bestFacts.Damage + (strength + focus) * Math.Max(1, bestFacts.Hits);
                if (vulnerable > 0) hit *= 1.5;
                turnDamage += hit;
                if (bestFacts.Block > 0) blockThisTurn += bestFacts.Block + dexterity;
                energy += bestFacts.Energy;
                vulnerable += bestFacts.Vulnerable;
                weak += bestFacts.Weak;
                poison += bestFacts.Poison + bestFacts.Doom;
                Ramp(bestFacts, ref strength, ref dexterity, ref focus);

                if (best.Type == CardType.Power || best.Keywords.Contains(CardKeyword.Exhaust))
                    exhausted.Add(best);
                else
                    discard.Add(best);
            }

            energyWaste += energy;
            foreach (var card in hand) discard.Add(card);
            hand.Clear();

            // Debuffs on the enemy fade, so holding them up costs a card every
            // turn — which is what makes a setup card pay for its slot.
            vulnerable = Math.Max(0, vulnerable - 1);
            weak = Math.Max(0, weak - 1);

            enemyHp -= turnDamage;
            damage += turnDamage;
            block += blockThisTurn;
            perTurn[turn] = turnDamage;

            // Whatever the deck could not block, it wears. Reaching zero ends the
            // fight where it ended, so the turns it never survived do not count as
            // damage the deck dealt.
            hp -= Math.Max(0, demand - blockThisTurn);
            if (hp <= 0)
            {
                died = true;
                break;
            }
            if (enemyHp <= 0)
            {
                killTurn = turn + 1;
                break;
            }
        }

        // Burst is reported, not scored: how hard the deck's best turn hits, and
        // when. A deck whose peak arrives on turn nine is a different deck from one
        // that peaks on turn two even when their averages match, and the average is
        // all the score has ever seen. Turns after the fight ended stay zero, so the
        // peak is always inside the part of the fight that actually happened.
        var peakTurn = 0;
        for (var turn = 1; turn < perTurn.Length; turn++)
            if (perTurn[turn] > perTurn[peakTurn]) peakTurn = turn;
        return new Outcome(damage, block, energyWaste, deadDraws, drawn, RampRatio(perTurn),
            perTurn[peakTurn], peakTurn + 1, killTurn, died);
    }

    /// <summary>
    /// How much playing this card is worth right now. Offence is always wanted;
    /// block is wanted only up to the damage actually coming; a card that ramps is
    /// worth more the earlier it lands, because it has more turns left to pay for
    /// itself — that term is what makes a power deck's score reflect its ramp
    /// instead of its first turn.
    /// </summary>
    private static double PlayValue(CardProfile.Facts facts, CardModel card,
        double strength, double focus, double dexterity, double vulnerable, double demanded, int turnsLeft, bool lethal)
    {
        var damage = facts.Damage + (strength + focus) * Math.Max(1, facts.Hits);
        if (vulnerable > 0) damage *= 1.5;

        var block = facts.Block > 0 ? facts.Block + dexterity : 0;
        var value = damage
            + Math.Min(block, demanded) * 1.1 + Math.Max(0, block - demanded) * 0.15
            + facts.Draw * 6 + facts.Energy * 10
            + facts.Vulnerable * 3 + facts.Weak * 3 + facts.StrengthDown * 2
            + (facts.Poison + facts.Doom) * 2.5;

        // Blocking survives the turn; damage does not. When the unblocked damage
        // would be lethal, a block card is the only card that keeps the fight
        // going, so it outranks anything else in hand.
        if (lethal && block > 0) value += 1000;

        if (facts.Scaling > 0) value += facts.Scaling * 8 * (turnsLeft / 10.0);
        if (card.Type == CardType.Power) value += 3;
        if (CostOf(card, 3) == 0 && value > 0) value += 2;
        return value;
    }

    /// <summary>
    /// Feeds a ramp card's scaling into the running stats. <see cref="CardProfile"/>
    /// reports one combined scaling number plus the roles it came from; splitting it
    /// by those roles is the only way to know whether the card feeds damage or
    /// block, so a card that raises two at once is split evenly.
    /// </summary>
    private static void Ramp(CardProfile.Facts facts, ref double strength, ref double dexterity, ref double focus)
    {
        if (facts.Scaling <= 0) return;
        var roles = facts.Roles;
        var parts = (roles.Contains("strength") ? 1 : 0) + (roles.Contains("dexterity") ? 1 : 0)
            + (roles.Contains("focus") ? 1 : 0);
        if (parts <= 0) parts = 1;
        if (roles.Contains("strength")) strength += facts.Scaling / parts;
        if (roles.Contains("dexterity")) dexterity += facts.Scaling / parts;
        // Focus scales orb output; with no orb model it is folded into damage, which
        // is the same direction and the same size as a point of Strength.
        if (roles.Contains("focus")) focus += facts.Scaling / parts;
    }

    private static int CostOf(CardModel card, double energy)
        => card.EnergyCost.CostsX
            ? Math.Max(0, (int)energy)
            : Math.Max(0, card.EnergyCost.GetWithModifiers(CostModifiers.All));

    private static double Draw(List<CardModel> drawPile, List<CardModel> discard, List<CardModel> hand,
        int count, out int unplayable)
    {
        unplayable = 0;
        var drawn = 0;
        for (var i = 0; i < count; i++)
        {
            if (drawPile.Count == 0)
            {
                if (discard.Count == 0) break;
                drawPile.AddRange(discard);
                discard.Clear();
            }
            var card = drawPile[^1];
            drawPile.RemoveAt(drawPile.Count - 1);
            hand.Add(card);
            drawn++;
            if (CardProfile.Of(card).Unplayable) unplayable++;
        }
        return drawn;
    }

    private static List<CardModel> Shuffle(List<CardModel> cards, Rng rng)
    {
        for (var i = cards.Count - 1; i > 0; i--)
        {
            var j = rng.NextInt(i + 1);
            (cards[i], cards[j]) = (cards[j], cards[i]);
        }
        return cards;
    }

    /// <summary>Spread between the first and last third of the fight. A deck that only works late reads above 1.</summary>
    private static double RampRatio(double[] perTurn)
    {
        if (perTurn.Length < 4) return 1;
        var third = Math.Max(1, perTurn.Length / 3);
        var early = perTurn.Take(third).Average();
        var late = perTurn.Skip(perTurn.Length - third).Average();
        if (early <= 0.01) return late > 0.01 ? 3 : 1;
        return Math.Clamp(late / early, 0, 3);
    }
}
