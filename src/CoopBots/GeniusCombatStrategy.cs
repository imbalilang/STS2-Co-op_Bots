using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;

namespace CoopBots;

/// <summary>
/// Tactical combat policy used only by Genius bots.  It deliberately separates
/// survival, setup and damage instead of assigning a fixed value to a card name.
/// This keeps the policy useful for upgraded cards and most modded cards too.
/// </summary>
internal static class GeniusCombatStrategy
{
    private static readonly HashSet<string> FixedDoubleHitCards = new(StringComparer.Ordinal)
    {
        "AstralPulse", "DaggerSpray", "Maul", "Refract", "RipAndTear", "Thrash", "TwinStrike", "Uproar",
    };

    private static readonly HashSet<string> RepeatIsHitCountCards = new(StringComparer.Ordinal)
    {
        "CelestialMight", "Conflagration", "Exterminate", "FightMe", "GunkUp", "Peck", "Ricochet",
        "SevenStars", "SovereignBlade", "SwordBoomerang",
    };

    private static readonly HashSet<string> EnergyXHitCards = new(StringComparer.Ordinal)
    {
        "Eradicate", "Skewer", "Volley", "Whirlwind",
    };

    private static readonly HashSet<string> ImmediateEnergyCards = new(StringComparer.Ordinal)
    {
        "Adrenaline", "Bloodletting", "DoubleEnergy", "Offering", "Scavenge", "Turbo",
        "BelieveInYou", "EnergySurge", "Constellation",
    };

    private static readonly HashSet<string> StrongDrawFirstCards = new(StringComparer.Ordinal)
    {
        "Acrobatics", "Adrenaline", "Backflip", "BattleTrance", "CompileDriver", "Coolheaded",
        "Finesse", "FlashOfSteel", "Ftl", "Glimmer", "MasterOfStrategy", "Offering", "Overclock",
        "Parse", "PommelStrike", "Reboot", "Scrawl", "ShrugItOff", "Skim", "SweepingBeam",
        "Constellation", "HuddleUp",
    };

    public static BotBrain.CombatMove? Choose(
        Player player,
        IReadOnlyList<BotBrain.CombatMove> legalMoves,
        int actionNumber)
    {
        var scored = ScoreLegalMoves(player, legalMoves, actionNumber);
        if (scored.Count == 0)
            return null;

        // A Genius bot may deliberately leave energy unused.  Playing a Defend
        // into zero incoming damage, or paying HP for no tactical gain, is worse
        // than ending the turn.
        return scored[0].Score > 0.0 ? scored[0] : null;
    }

    internal static IReadOnlyList<BotBrain.CombatMove> ScoreLegalMoves(
        Player player,
        IReadOnlyList<BotBrain.CombatMove> legalMoves,
        int actionNumber)
    {
        var state = player.Creature.CombatState;
        var combat = player.PlayerCombatState;
        if (state is null || combat is null || legalMoves.Count == 0)
            return Array.Empty<BotBrain.CombatMove>();

        var enemies = state.Enemies.Where(enemy => enemy.IsAlive).ToList();
        var incoming = CombatAssessment.Incoming(player.Creature);
        var uncovered = Math.Max(0.0, incoming - player.Creature.Block);
        var enemyEffectiveHp = enemies.Sum(EffectiveHp);
        var singleEnemy = enemies.Count == 1 ? enemies[0] : null;
        var affordableDamage = singleEnemy is null
            ? 0.0
            : EstimateAffordableDamage(player, singleEnemy, combat.Energy);
        var offenseMode = singleEnemy is not null && affordableDamage >= EffectiveHp(singleEnemy);
        var estimatedHandDamage = singleEnemy is null ? EstimateAllHandDamage(player) : affordableDamage;
        var longFight = enemyEffectiveHp > Math.Max(20.0, estimatedHandDamage * 1.15);
        var attackCount = combat.Hand.Cards.Count(card => card.Type == CardType.Attack && card.CanPlay());
        var blockCardCount = combat.Hand.Cards.Count(card => card.GainsBlock && card.CanPlay());

        return legalMoves
            .Select(move => ScoreMove(
                move,
                player,
                enemies,
                uncovered,
                offenseMode,
                longFight,
                attackCount,
                blockCardCount,
                actionNumber))
            .OrderByDescending(move => move.Score)
            .ThenBy(move => move.Card.Id.Entry, StringComparer.Ordinal)
            .ThenBy(move => move.Target?.CombatId ?? uint.MaxValue)
            .ToList();
    }

    private static BotBrain.CombatMove ScoreMove(
        BotBrain.CombatMove move,
        Player player,
        IReadOnlyList<Creature> enemies,
        double uncovered,
        bool offenseMode,
        bool longFight,
        int attackCount,
        int blockCardCount,
        int actionNumber)
    {
        var card = move.Card;
        var target = move.Target;
        var combat = player.PlayerCombatState!;
        // Model IDs use upper snake case (for example TWIN_STRIKE), while the
        // runtime type is stable and directly identifies the card implementation.
        var id = card.GetType().Name;
        var facts = Analyze(card, target, combat.Energy, enemies.Count);
        var cost = facts.EnergyCost;
        var score = 0.0;
        var reasons = new List<string>(5);

        if (id == "FranticEscape")
            return move with { Score = MonsterHazards.EscapeScore(player.Creature), Reason = "sandpit-escape" };
        if (card.Type is CardType.Curse or CardType.Status)
        {
            // A status is not automatically "harmful to play": one that punishes
            // being held is harmful to KEEP, and playing it is the only way to
            // stop the bleeding. Beckon (6 unblockable at end of turn, twice per
            // cast) cost a real act-1 boss fight two teammates because every
            // planner here refused to play it. Reading the same variable the real
            // effect charges keeps this true for any card shaped like it.
            var held = HeldPenalty(card);
            return held > 0
                ? move with { Score = held * 6.0 - cost * 2, Reason = "avoid-hold-penalty" }
                : move with { Score = -500.0, Reason = "avoid-harmful-card" };
        }

        var targetHp = target is { IsEnemy: true } ? EffectiveHp(target) : double.PositiveInfinity;
        var targetThreat = target is { IsEnemy: true }
            ? EstimateIntentDamage(target, player.Creature.CombatState!.Allies)
            : 0.0;
        var isSingleTargetLethal = target is { IsEnemy: true } && facts.TotalDamage >= targetHp && facts.TotalDamage > 0;
        var aoeKills = card.TargetType == TargetType.AllEnemies
            ? enemies.Count(enemy => facts.DamagePerEnemy >= EffectiveHp(enemy))
            : 0;

        if (isSingleTargetLethal)
        {
            // Removing an attacker is simultaneously the best block card and the
            // safest form of damage. Prefer high-threat and primary enemies.
            score += 600.0 + targetThreat * 5.0;
            if (target!.IsPrimaryEnemy)
                score += 35.0;
            reasons.Add("lethal");
        }
        if (aoeKills > 0)
        {
            score += 330.0 * aoeKills;
            reasons.Add($"aoe-kill:{aoeKills}");
        }

        var usefulDamage = EstimateUsefulDamage(facts, target, enemies);
        score += usefulDamage * (offenseMode ? 6.0 : 3.2);
        if (facts.TotalDamage > 0)
        {
            reasons.Add(offenseMode ? "turn-lethal-line" : "damage");
            if (target is { IsEnemy: true })
            {
                score += targetThreat * 1.4;
                if (target.IsSecondaryEnemy)
                    score -= 8.0;
            }
        }

        var blockRecipient = id is not ("Intercept" or "Mimic") && target is { IsPlayer: true } ? target : player.Creature;
        facts = facts with { Block = CombatAssessment.BlockFor(card, blockRecipient, target) };
        var recipientUncovered = CombatAssessment.Uncovered(blockRecipient);
        var usefulBlock = Math.Min(facts.Block, recipientUncovered);
        if (facts.Block > 0)
        {
            if (recipientUncovered <= 0)
            {
                score -= 18.0 + cost * 3.0;
                reasons.Add("avoid-overblock");
            }
            else
            {
                var lethalIncoming = recipientUncovered >= blockRecipient.CurrentHp;
                score += usefulBlock * (lethalIncoming ? 15.0 : offenseMode ? 1.4 : 5.5) * CombatAssessment.HumanWeight(blockRecipient);
                if (lethalIncoming)
                    score += (recipientUncovered - usefulBlock < blockRecipient.CurrentHp ? 650.0 : 180.0) * CombatAssessment.HumanWeight(blockRecipient);
                if (facts.Block > usefulBlock)
                    score -= (facts.Block - usefulBlock) * 0.8;
                reasons.Add(lethalIncoming ? "prevent-death" : "cover-intent");
            }
        }
        if (card.GainsBlock && card.TargetType == TargetType.AllAllies)
        {
            foreach (var ally in player.Creature.CombatState!.PlayerCreatures.Where(ally => ally.IsAlive
                && ally != player.Creature))
            {
                var allyUncovered = CombatAssessment.Uncovered(ally);
                var supplied = Math.Min(allyUncovered, CombatAssessment.BlockFor(card, ally));
                if (supplied <= 0) continue;
                score += supplied * (allyUncovered >= ally.CurrentHp ? 15 : 5.5) * CombatAssessment.HumanWeight(ally);
                if (allyUncovered >= ally.CurrentHp && allyUncovered - supplied < ally.CurrentHp)
                    score += 650 * CombatAssessment.HumanWeight(ally);
                reasons.Add("team-block");
            }
        }

        if (id == "Intercept" && target is { IsPlayer: true })
        {
            var alreadyCovered = target.Powers.Any(p => p.GetType().Name == "CoveredPower");
            var coveredCount = player.Creature.CombatState!.PlayerCreatures.Count(c =>
                c.Powers.Any(p => p.GetType().Name == "CoveredPower" && p.Applier == player.Creature));
            var incomingNow = CombatAssessment.Incoming(player.Creature);
            var incomingAfter = incomingNow * (coveredCount + 2.0) / (coveredCount + 1.0);
            var casterSurvives = incomingAfter - player.Creature.Block - facts.Block < player.Creature.CurrentHp;
            if (alreadyCovered || !casterSurvives)
                return move with { Score = -1000, Reason = "avoid-unsafe-or-duplicate-intercept" };
            score += Math.Min(CombatAssessment.Uncovered(target), target.CurrentHp) * 7 * CombatAssessment.HumanWeight(target);
            if (CombatAssessment.InDanger(target)) score += 850 * CombatAssessment.HumanWeight(target);
            reasons.Add("safe-intercept");
        }

        if (facts.Weak > 0 && target is { IsEnemy: true })
        {
            // Weak is only urgent on an enemy that is actually attacking. In
            // co-op it protects every player, so scale by the living party size.
            var partySize = player.Creature.CombatState!.PlayerCreatures.Count(creature => creature.IsAlive);
            var prevented = targetThreat * 0.25 * Math.Max(1, partySize);
            score += targetThreat > 0 ? 38.0 + prevented * 2.4 : -6.0;
            if (FindPowerAmount(target, "WeakPower") > 0)
                score -= 15.0;
            reasons.Add(targetThreat > 0 ? "weak-attacker" : "weak-no-attack");
        }

        if (facts.StrengthDown > 0)
        {
            var affected = card.TargetType == TargetType.AllEnemies
                ? enemies
                : target is { IsEnemy: true } ? new[] { target } : Array.Empty<Creature>();
            var partySize = player.Creature.CombatState!.PlayerCreatures.Count(creature => creature.IsAlive);
            var prevented = 0.0;
            var attackers = 0;
            foreach (var enemy in affected)
            {
                var repeats = enemy.Monster?.NextMove.Intents.OfType<AttackIntent>().Sum(intent => intent.Repeats) ?? 0;
                if (repeats <= 0) continue;
                attackers++;
                // Every remaining hit this enemy phase loses StrengthDown damage,
                // for every living teammate it would hit. Bound it by what the
                // enemy would actually deal so an already-crippled attacker
                // cannot inflate the estimate.
                var teamThreat = player.Creature.CombatState!.PlayerCreatures.Where(creature => creature.IsAlive)
                    .Sum(creature => CombatAssessment.FromEnemy(enemy, creature));
                prevented += Math.Min(teamThreat, facts.StrengthDown * repeats * Math.Max(1, partySize));
            }
            if (attackers > 0)
            {
                // The most efficient prevention available: it deletes whole hits
                // instead of racing them with block, and it protects everyone the
                // enemy targets. Score it above block so it is played first and
                // the rest of the team can size its block to the reduced hit.
                score += 52.0 + Math.Min(280.0, prevented * 2.8);
                reasons.Add("strength-down-first");
            }
            else
            {
                score -= 14.0;
                reasons.Add("strength-down-no-attack");
            }
        }

        if (facts.Vulnerable > 0)
        {
            var affectedEnemies = card.TargetType == TargetType.AllEnemies
                ? enemies
                : target is { IsEnemy: true } ? new[] { target } : Array.Empty<Creature>();
            var teammates = player.Creature.CombatState!.Players.Where(p => p.Creature.IsAlive
                && p.PlayerCombatState?.Phase == PlayerTurnPhase.Play
                && !CombatManager.Instance.IsPlayerReadyToEndTurn(p)).ToList();
            var humanFollowUp = 0.0;
            var followUpDamage = affectedEnemies.Sum(enemy => teammates.Sum(p =>
            {
                var damage = p == player ? EstimateRemainingAttackDamage(p, enemy, card)
                    : EstimateAffordableDamage(p, enemy, p.PlayerCombatState?.Energy ?? 0);
                // Drives: the discount exists because a visible human hand is an option,
                // not a promise. A handed-over seat's hand IS a promise — the same logic
                // is choosing it — so it must count at full weight like any bot's.
                if (!AutoPilot.Drives(p.NetId))
                {
                    humanFollowUp += damage;
                    // A visible human hand is useful option value, not a promise
                    // that the player will follow the Bot's proposed line.
                    return damage * 0.35;
                }
                return damage;
            }));
            score += followUpDamage > 0 ? 90.0 + Math.Min(240.0, followUpDamage * 1.1) : 2.0;
            if (affectedEnemies.Count > 0 && affectedEnemies.All(enemy => FindPowerAmount(enemy, "VulnerablePower") > 1))
                score -= 28.0;
            reasons.Add("vulnerable-before-attacks");
            if (humanFollowUp > 0)
                reasons.Add("human-hand-synergy");
        }

        if (facts.Poison > 0 || facts.Doom > 0)
        {
            var damageOverTime = facts.Poison + facts.Doom;
            var existing = target is null
                ? 0
                : FindPowerAmount(target, facts.Doom > 0 ? "DoomPower" : "PoisonPower");
            score += damageOverTime * 3.2 + (existing > 0 ? damageOverTime * 0.9 : 12.0);
            if (target is { IsEnemy: true } && existing + damageOverTime >= target.CurrentHp)
                score += 280.0;
            reasons.Add(facts.Doom > 0 ? "doom" : "poison");
        }

        if (id == "FightMe" && target is { IsEnemy: true } && !isSingleTargetLethal)
        {
            var enemyStrength = card.DynamicVars.Values.Where(v => v.Name == "EnemyStrength")
                .Sum(v => (double)v.BaseValue);
            var repeats = target.Monster?.NextMove.Intents.OfType<AttackIntent>().Sum(i => i.Repeats) ?? 0;
            var party = player.Creature.CombatState!.PlayerCreatures.Where(c => c.IsAlive).ToList();
            var extraTeamDamage = enemyStrength * Math.Max(1, repeats) * party.Sum(CombatAssessment.HumanWeight);
            score -= extraTeamDamage * 10;
            if (party.Any(CombatAssessment.InDanger)) score -= 500;
            reasons.Add("enemy-strength-cost");
        }

        var handSpace = Math.Max(0, 10 - combat.Hand.Cards.Count);
        var usefulDraw = Math.Min(facts.Draw, handSpace);
        if (facts.Draw > 0)
        {
            score += usefulDraw * 11.0;
            score -= Math.Max(0.0, facts.Draw - handSpace) * 7.0;
            if (StrongDrawFirstCards.Contains(id) && actionNumber <= 2 && handSpace > 0)
                score += 36.0;
            reasons.Add("draw-first");
        }

        if (facts.ImmediateEnergy > 0)
        {
            var spendableCards = combat.Hand.Cards.Count(candidate => candidate != card && candidate.CanPlay());
            score += spendableCards > 0 ? 45.0 + facts.ImmediateEnergy * 14.0 : -12.0;
            if (actionNumber <= 2)
                score += 18.0;
            reasons.Add("energy-first");
        }

        if (id == "DoubleEnergy")
        {
            var netEnergy = Math.Max(0, combat.Energy - 1);
            score += netEnergy >= 2 ? 95.0 + netEnergy * 9.0 : -30.0;
            reasons.Add("double-energy");
        }
        else if (id == "BulletTime")
        {
            var remainingCost = combat.Hand.Cards
                .Where(candidate => candidate != card && candidate.CanPlay() && !candidate.EnergyCost.CostsX)
                .Sum(candidate => Math.Max(0, candidate.EnergyCost.GetWithModifiers(CostModifiers.All)));
            score += remainingCost > cost + 2 ? 180.0 + remainingCost * 6.0 : -55.0;
            reasons.Add("bullet-time-order");
        }

        if (card.Type == CardType.Power)
        {
            score += offenseMode ? -45.0 : longFight ? 70.0 : 24.0;
            score += Math.Max(0, 3 - player.PlayerCombatState!.TurnNumber) * 12.0;
            reasons.Add("scaling-power");
        }

        if (facts.Strength > 0 && id != "Friendship")
        {
            score += facts.Strength * Math.Max(2, attackCount) * (longFight ? 4.0 : 2.0);
            reasons.Add("strength-before-attacks");
        }
        if (facts.Dexterity > 0)
        {
            score += facts.Dexterity * Math.Max(1, blockCardCount) * (longFight ? 3.5 : 1.5);
            reasons.Add("dexterity-before-block");
        }
        if (facts.Focus > 0 && id != "Hyperbeam")
        {
            score += facts.Focus * (longFight ? 18.0 : 9.0);
            reasons.Add("focus-scaling");
        }
        if (id == "Hyperbeam" && !offenseMode)
        {
            score -= 35.0 + FindPowerAmount(player.Creature, "FocusPower") * 12.0;
            reasons.Add("focus-loss");
        }

        if (facts.HpLoss > 0)
        {
            var hpAfter = player.Creature.CurrentHp - facts.HpLoss;
            if (hpAfter <= Math.Max(1.0, uncovered))
                score -= 700.0;
            else
                score -= facts.HpLoss * (player.Creature.CurrentHp <= player.Creature.MaxHp * 0.35 ? 8.0 : 2.5);
            reasons.Add("hp-cost");
        }

        if (target is { IsPlayer: true } && target.Player != player)
        {
            var allyHealthRatio = (double)target.CurrentHp / Math.Max(1, target.MaxHp);
            if (facts.Dexterity > 0 || facts.Block > 0)
                score += (1.0 - allyHealthRatio) * 35.0;
            if (facts.Strength > 0)
            {
                var allyAttacks = target.Player?.PlayerCombatState?.Hand.Cards.Count(candidate => candidate.Type == CardType.Attack) ?? 0;
                score += allyAttacks * 5.0;
            }
            // Timing decides whether an aimed buff is worth anything. A one-turn
            // buff handed to someone who has already ended is thrown away, and a
            // human still deciding only spends it with a discount because we
            // cannot make them act — the team is the reliable recipient. Permanent
            // buffs keep their value either way and are not touched.
            var recipient = target.Player!;
            if (CombatAssessment.TemporaryBuff(card))
            {
                if (!CombatAssessment.CanStillAct(recipient))
                {
                    score -= 120.0;
                    reasons.Add("buff-after-end");
                }
                else if (!AutoPilot.Drives(recipient.NetId))
                {
                    score *= 0.7;
                    reasons.Add("prefer-team-buff");
                }
            }
            // Energy handed to someone who cannot act this turn is just as wasted.
            if (facts.ImmediateEnergy > 0 && !CombatAssessment.CanStillAct(recipient))
            {
                score -= 120.0;
                reasons.Add("energy-after-end");
            }
            reasons.Add("ally-fit");
        }

        // Energy represents option value. Expensive setup is bad when the bot
        // still has lethal incoming damage to solve this turn.
        score -= cost * 4.0;
        if (uncovered >= player.Creature.CurrentHp && facts.Block <= 0 && !isSingleTargetLethal && facts.Weak <= 0
            && facts.StrengthDown <= 0)
            score -= 360.0 + cost * 15.0;

        if (facts.DamagePerEnemy <= 0 && facts.Block <= 0 && facts.Draw <= 0 && facts.ImmediateEnergy <= 0 &&
            facts.Vulnerable <= 0 && facts.Weak <= 0 && facts.StrengthDown <= 0 && facts.Poison <= 0 && facts.Doom <= 0 &&
            facts.Strength <= 0 && facts.Dexterity <= 0 && facts.Focus <= 0 && card.Type != CardType.Power)
            score += 2.0 - cost * 6.0;

        // Playing one more card can cost more than the card itself. The kernel
        // simulates play-count mechanics; this scorer prices cards one at a time
        // and saw only the card, so a last-stand turn would happily spend the
        // whole hand into an Aeonglass counter and then die to the Withers it had
        // just created. Charge the end-of-turn cost of the status this play is
        // about to hand the player.
        var triggered = StatusTriggerPenalty(player);
        if (triggered > 0)
        {
            score -= triggered;
            reasons.Add($"status-trigger:{triggered:F0}");
        }

        return move with
        {
            Score = score,
            Reason = reasons.Count == 0 ? "fallback" : string.Join(',', reasons.Distinct()),
        };
    }

    internal static CardFacts Analyze(CardModel card, Creature? target, int energy, int enemyCount)
    {
        try
        {
            card.UpdateDynamicVarPreview(CardPreviewMode.Normal, target, card.DynamicVars);
        }
        catch
        {
            // Some highly stateful or modded cards cannot build a preview in a
            // headless remote-player context. Base values remain a safe fallback.
        }

        var vars = card.DynamicVars.Values.ToList();
        double Preview(DynamicVar variable) => (double)variable.PreviewValue;
        double Named(string name) => vars
            .Where(variable => string.Equals(variable.Name, name, StringComparison.OrdinalIgnoreCase))
            .Sum(Preview);

        var calculatedDamage = vars.FirstOrDefault(variable => variable.GetType().Name == "CalculatedDamageVar");
        var damage = calculatedDamage is null
            ? vars.Where(variable => variable.GetType().Name is "DamageVar" or "OstyDamageVar").Sum(Preview)
            : Preview(calculatedDamage);

        var hitCount = 1.0;
        var id = card.GetType().Name;
        if (FixedDoubleHitCards.Contains(id))
            hitCount = 2.0;
        else if (RepeatIsHitCountCards.Contains(id))
            hitCount = Math.Max(1.0, Named("Repeat"));
        else if (EnergyXHitCards.Contains(id))
            hitCount = Math.Max(1, energy);
        else
        {
            var calculatedHits = vars.FirstOrDefault(variable => variable.Name == "CalculatedHits");
            if (calculatedHits is not null)
                hitCount = Math.Max(1.0, Preview(calculatedHits));
        }

        var perEnemy = id == "Cacophony" ? 0 : Math.Max(0, Math.Floor(damage)) * hitCount;
        var aoeMultiplier = card.TargetType == TargetType.AllEnemies ? Math.Max(1, enemyCount) : 1;
        var immediateEnergy = ImmediateEnergyCards.Contains(id)
            ? vars.Where(variable => variable.GetType().Name == "EnergyVar" && variable.Name != "ExtraCost").Sum(Preview)
            : 0.0;

        var vulnerable = PowerAmount(vars, "VulnerablePower");
        var weak = PowerAmount(vars, "WeakPower");
        if (id is "Uppercut" or "Shockwave")
            vulnerable = weak = Named("Power");
        if (id == "GoForTheEyes" && target?.Monster?.IntendsToAttack != true)
            weak = 0;
        // Temporary enemy Strength loss (尖啸/黑暗镣铐/弱化之触/星灭, plus
        // CrushUnder/Mangle): every remaining hit this enemy phase is smaller by
        // this amount, for every teammate it attacks. The kernel mirrors these as
        // TemporaryStrengthPower; here they are read from the shared var name.
        // Malaise is X-cost (it spends the whole pool) and applies a permanent
        // loss, so it carries no StrengthLoss variable.
        var strengthDown = Named("StrengthLoss");
        if (id == "Malaise")
        {
            strengthDown = Math.Max(0, energy + (card.IsUpgraded ? 1 : 0));
            weak = strengthDown;
        }

        return new CardFacts(
            DamagePerEnemy: perEnemy,
            TotalDamage: perEnemy * aoeMultiplier,
            Hits: (int)hitCount,
            Block: vars.Where(variable => variable.GetType().Name is "BlockVar" or "CalculatedBlockVar").Sum(Preview),
            Draw: card.Type != CardType.Power && StrongDrawFirstCards.Contains(id)
                && (!CombatResourceProjection.ModelsDraw(card) || !card.IsMutable || card.Owner is null
                    || !card.Owner.Creature.Powers.Any(power => power.GetType().Name == "NoDrawPower"))
                ? vars.Where(variable => variable.GetType().Name == "CardsVar").Sum(Preview) : 0.0,
            ImmediateEnergy: immediateEnergy,
            HpLoss: vars.Where(variable => variable.GetType().Name == "HpLossVar").Sum(Preview),
            Vulnerable: vulnerable,
            Weak: weak,
            StrengthDown: strengthDown,
            Poison: PowerAmount(vars, "PoisonPower"),
            Doom: PowerAmount(vars, "DoomPower"),
            Strength: id is "Friendship" or "DemonForm" ? 0.0 : PowerAmount(vars, "StrengthPower"),
            Dexterity: PowerAmount(vars, "DexterityPower"),
            Focus: id == "Hyperbeam" ? 0.0 : PowerAmount(vars, "FocusPower"),
            EnergyCost: card.EnergyCost.CostsX
                ? Math.Max(0, energy)
                : Math.Max(0, card.EnergyCost.GetWithModifiers(CostModifiers.All)));
    }

    private static double PowerAmount(IEnumerable<DynamicVar> variables, string powerTypeName)
    {
        return variables
            .Where(variable =>
            {
                var type = variable.GetType();
                return type.IsGenericType && type.GetGenericArguments().Any(argument => argument.Name == powerTypeName);
            })
            .Sum(variable => (double)variable.PreviewValue);
    }

    private static double EstimateUsefulDamage(CardFacts facts, Creature? target, IReadOnlyList<Creature> enemies)
    {
        if (facts.TotalDamage <= 0)
            return 0;
        if (target is { IsEnemy: true })
            return Math.Min(facts.TotalDamage, EffectiveHp(target));
        if (enemies.Count == 0)
            return 0;
        return enemies.Sum(enemy => Math.Min(facts.DamagePerEnemy, EffectiveHp(enemy)));
    }

    private static double EstimateAffordableDamage(Player player, Creature target, int energy)
    {
        var dp = new double[Math.Max(0, energy) + 1];
        foreach (var card in player.PlayerCombatState!.Hand.Cards.Where(card => card.Type == CardType.Attack && card.CanPlay()))
        {
            var cost = card.EnergyCost.CostsX
                ? energy
                : Math.Max(0, card.EnergyCost.GetWithModifiers(CostModifiers.All));
            if (cost > energy)
                continue;
            var damage = Analyze(card, target, energy, 1).TotalDamage;
            for (var budget = energy; budget >= cost; budget--)
                dp[budget] = Math.Max(dp[budget], dp[budget - cost] + damage);
        }
        return dp.Max();
    }

    private static double EstimateRemainingAttackDamage(Player player, Creature target, CardModel excluded)
    {
        var combat = player.PlayerCombatState!;
        var energy = Math.Max(0, combat.Energy - Math.Max(0, excluded.EnergyCost.GetWithModifiers(CostModifiers.All)));
        return EstimateAffordableDamageExcluding(player, target, energy, excluded);
    }

    private static double EstimateAffordableDamageExcluding(Player player, Creature target, int energy, CardModel excluded)
    {
        var dp = new double[energy + 1];
        foreach (var card in player.PlayerCombatState!.Hand.Cards.Where(card => card != excluded && card.Type == CardType.Attack && card.CanPlay()))
        {
            var cost = card.EnergyCost.CostsX
                ? energy
                : Math.Max(0, card.EnergyCost.GetWithModifiers(CostModifiers.All));
            if (cost > energy)
                continue;
            var damage = Analyze(card, target, energy, 1).TotalDamage;
            for (var budget = energy; budget >= cost; budget--)
                dp[budget] = Math.Max(dp[budget], dp[budget - cost] + damage);
        }
        return dp.Max();
    }

    private static double EstimateAllHandDamage(Player player)
    {
        var state = player.Creature.CombatState!;
        var target = state.Enemies.Where(enemy => enemy.IsAlive).OrderByDescending(EffectiveHp).FirstOrDefault();
        return target is null ? 0.0 : EstimateAffordableDamage(player, target, player.PlayerCombatState!.Energy);
    }

    private static double EstimateIntentDamage(Creature enemy, IReadOnlyList<Creature> allies)
    {
        if (!enemy.IsAlive || enemy.Monster is null)
            return 0;
        try
        {
            return enemy.Monster.NextMove.Intents
                .OfType<AttackIntent>()
                .Sum(intent => (double)intent.GetTotalDamage(allies, enemy));
        }
        catch
        {
            return enemy.Monster.IntendsToAttack ? 8.0 : 0.0;
        }
    }

    private static int FindPowerAmount(Creature creature, string powerTypeName)
        => creature.Powers.FirstOrDefault(power => power.GetType().Name == powerTypeName)?.Amount ?? 0;

    private static double EffectiveHp(Creature creature) => Math.Max(0, creature.CurrentHp + creature.Block);

    /// <summary>
    /// Unblockable damage this card will deal its owner if it is still in hand at
    /// the end of the turn. Zero for everything else, so the caller can keep
    /// treating ordinary statuses as unplayable.
    /// </summary>
    internal static double HeldPenalty(CardModel card)
    {
        HeldPenaltySplit(card, out var unblockable, out var blockable);
        return unblockable + blockable;
    }

    /// <summary>
    /// The same cost, split by whether block can absorb it. Beckon charges HpLoss
    /// — life loss, which no amount of block stops. The Aeonglass boss's Wither
    /// charges Damage, which is ordinary damage and must be priced against the
    /// block the player still has; treating both as unblockable made the fast
    /// paths overstate Wither and defend against it harder than the game does.
    /// </summary>
    internal static void HeldPenaltySplit(CardModel card, out double unblockable, out double blockable)
    {
        unblockable = blockable = 0;
        try
        {
            if (!card.HasTurnEndInHandEffect) return;
            // Read the variable the end-of-turn mirror actually charges. Beckon
            // and friends use HpLoss; the Aeonglass boss's Wither uses Damage, so
            // looking only for HpLoss left it invisible to every scorer that uses
            // this — and a bot died in its own end-turn phase holding one.
            unblockable = card.DynamicVars.Values
                .Where(variable => variable.GetType().Name == "HpLossVar")
                .Sum(variable => (double)variable.BaseValue);
            if (unblockable <= 0)
                blockable = card.DynamicVars.Values
                    .Where(variable => variable.GetType().Name == "DamageVar")
                    .Sum(variable => (double)variable.BaseValue);
        }
        catch { unblockable = blockable = 0; }
    }

    /// <summary>
    /// End-of-turn cost of the status card that playing one more card now creates.
    /// Zero unless an enemy counts the party's plays and this play is the one that
    /// trips the counter. The mechanic itself lives in the kernel, which is where
    /// the counter's bookkeeping is modeled; this only prices the result.
    /// </summary>
    private static double StatusTriggerPenalty(Player player)
    {
        try
        {
            return Kernel.KernelSession.PendingHeldStatus(player) is { } status ? HeldPenalty(status) : 0;
        }
        catch
        {
            // A mechanic we cannot read must not change the score at all.
            return 0;
        }
    }

    internal static double UsefulBlockForTest(double incoming, double currentBlock, double cardBlock)
        => Math.Min(Math.Max(0, cardBlock), Math.Max(0, incoming - currentBlock));

    internal readonly record struct CardFacts(
        double DamagePerEnemy,
        double TotalDamage,
        int Hits,
        double Block,
        double Draw,
        double ImmediateEnergy,
        double HpLoss,
        double Vulnerable,
        double Weak,
        double StrengthDown,
        double Poison,
        double Doom,
        double Strength,
        double Dexterity,
        double Focus,
        int EnergyCost);
}

