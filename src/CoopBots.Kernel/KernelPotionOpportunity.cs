using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using System.Reflection;
using CoopBots.Kernel.Vendor;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Potions;

namespace CoopBots.Kernel;

/// <summary>
/// Upstream compares combat endings. Our user requested (2026-09-27) a full-belt
/// spending rule and a last-chance rule even when every continuation still loses.
/// These are spending preferences, never claims that a heuristic is a victory.
/// </summary>
public static class KernelPotionOpportunity
{
    // Core's cached-plan rescue needs a sliced native side witness, not another
    // full tournament. Keep these bounds here rather than exposing arbitrary
    // internal RolloutRun options across the assembly boundary.
    public sealed class CurrentTurnWitness
    {
        private readonly RolloutRun run;
        private readonly KernelDecisionRequest? request;
        public CurrentTurnWitness(KernelSession root, IReadOnlyList<Player> party,
            IReadOnlyList<Player> actors, RolloutPolicyKind policy, KernelDecisionRequest? request = null)
        {
            this.request = request;
            run = new(root, party, actors,
                new(policy, MaxSteps: 48, MaxRounds: 1) {
                    InvestmentLookahead = false, SequenceLookahead = false }) { StopRequested = () => request?.IsStopped == true };
        }
        public bool Done => run.Done || request?.IsStopped == true;
        public KernelSession State => run.State;
        public RolloutOutcome? Result => run.Result;
        public void Step()
        {
            using var budgetScope = KernelSearchBudgetScope.Enter(request);
            run.Step();
        }
    }

    public sealed record Choice(KernelTeamSearch.Action Action, string Reason, double Benefit);

    public static bool HasFullBelt(Player p) => p.MaxPotionCount > 0
        && p.Potions.Count(q => !q.HasBeenRemovedFromState) >= p.MaxPotionCount;

    public static bool HasUnmodeledFullBelt(IReadOnlyList<Player> actors)
        => actors.Any(p => HasFullBelt(p) && p.Potions.Any(q => !q.HasBeenRemovedFromState
            && !HasFullBeltValuation(q)));

    public static bool PredictedDeath(Player p, IReadOnlyList<Player> party, TerminalRecord? forecast)
    {
        if (forecast is not { Verified: true }) return false;
        for (var i = 0; i < party.Count && i < forecast.PostCombatHp.Count; i++)
            if (party[i] == p) return forecast.PostCombatHp[i] <= 0;
        return false;
    }

    public static bool LastChance(KernelSession root, Player owner, IReadOnlyList<Player> party, TerminalRecord? forecast)
        => PredictedDeath(owner, party, forecast)
            && (forecast!.RoundsSurvived <= 1 || party.Any(p => root.Hp(p.Creature) > 0 && PredictedDeath(p, party, forecast)
                && (root.Threat(p).HpLoss >= root.Hp(p.Creature) || root.Hp(p.Creature) <= root.MaxHp(p.Creature) * .3)));

    public static Choice? Choose(KernelSession root, IReadOnlyList<Player> actors,
        IReadOnlyList<Player> party, TerminalRecord? forecast = null,
        IReadOnlyList<KernelTeamSearch.Action>? plan = null, bool unmodeledOnly = false,
        bool bossOpening = false, KernelDecisionRequest? request = null)
    {
        using var budgetScope = KernelSearchBudgetScope.Enter(request);
        if (request?.IsStopped == true) return null;
        // OWNER RULE 2026-10-05: a boss fight must never reach the loss screen still holding a
        // bottle. On the first round of a boss encounter, spend EVERY usable potion before the
        // first card. This is deliberately the same "use it, then re-capture and re-plan" path as
        // the full-belt branch below — the effect (especially a CHOOSER potion, which the kernel
        // cannot simulate) is never planned across, it is dispatched and the board is re-read.
        // The rule is a spending POLICY, not a claim that the bottle is worth its slot here; the
        // owner's requirement is that the case "died with potions unused" cannot happen again.
        // This branch runs before the `unmodeledOnly`/winning-tail guards on purpose: on round one
        // of a boss every bottle is spent, winning script or not.
        //
        // OWNER FOLLOW-UP 2026-10-05: 倒瓶也不许浪费。手牌 10 张还喝加牌/抽牌药，或能量已经
        // 花不完还喝能量药，都是把瓶子倒进下水道：
        //   - CHOOSER（Attack/Skill/Power/Colorless）与 SwiftPotion 在目标手牌满 10 时跳过；
        //   - 能量药改给「手牌可打费用 > 现有能量」的人（自己花不完就找队友），都花不完就留瓶
        //     （EnergyPotion 是 AnyTime，后面回合还能用）；
        //   - 其余瓶子维持原行为。
        // 跳过不等于放弃规则：瓶还在腰带上，后续任何一回合的常规判定（满腰带/预测死亡/
        // 已建模收益）仍可以花掉它，只是不再"为了清空而浪费"。
        if (bossOpening)
        {
            foreach (var owner in actors.Where(p => root.CanAct(p) && root.CanUsePotions(p)))
            foreach (var potion in root.UsablePotions(owner))
            {
                if (request?.IsStopped == true) return null;
                var targets = root.PotionTargets(potion);
                if (targets.Count == 0) continue;
                var target = BossOpeningTarget(root, owner, potion, targets, request, actors);
                if (request?.IsStopped == true) return null;
                if (target is null) continue;
                if (KernelSession.RequiresPotionChoiceContract(potion))
                {
                    var action = MandatoryChoiceAction(root, potion, target, actors);
                    if (request?.IsStopped == true) return null;
                    if (action is null) continue;
                    return new(action, "boss-opening:unmodeled-use-and-replan", 0);
                }
                return new(new(owner, null, target, potion), "boss-opening:unmodeled-use-and-replan", 0);
            }
            return null;
        }
        // A spending heuristic is not an alternative terminal result. The live
        // Ovicopter run (2026-10-01) discarded a 148-step victory to save a seat
        // that the winning line expected to lose. Keep both last-chance and
        // full-belt heuristics behind the same rule while a winning tail exists.
        // Planned bottles still execute through the stored script. Invalid or
        // exhausted scripts are cleared by the normal execution checks.
        // User policy 2026-10-02: unlike terminal-only upstream spending,
        // a full belt releases a usable bottle whose full-belt value is unknown.
        // Do not simulate its benefit: live dispatch clears the old script and
        // captures the native result before planning again, even for a winning tail.
        foreach (var owner in actors.Where(p => root.CanAct(p) && root.CanUsePotions(p)
                     && root.PotionCapacity(p) > 0 && root.HeldPotionCount(p) >= root.PotionCapacity(p)))
        foreach (var potion in root.UsablePotions(owner).Where(p => !HasFullBeltValuation(p)))
        {
            if (request?.IsStopped == true) return null;
            var targets = root.PotionTargets(potion);
            if (targets.Count == 0) continue;
            var target = targets.FirstOrDefault(t => t == owner.Creature) ?? targets[0];
            if (KernelSession.RequiresPotionChoiceContract(potion))
            {
                var action = MandatoryChoiceAction(root, potion, target, actors);
                if (request?.IsStopped == true) return null;
                if (action is null) continue;
                return new(action, "full-belt:unmodeled-use-and-replan", 0);
            }
            return new(new(owner, null, target, potion), "full-belt:unmodeled-use-and-replan", 0);
        }
        if (unmodeledOnly) return null;
        if (forecast is { Verified: true, Victory: true } && plan is { Count: > 0 }) return null;
        Choice? best = null;
        var bestScore = double.NegativeInfinity;
        foreach (var owner in actors.Where(p => root.CanAct(p) && root.CanUsePotions(p)))
        {
            if (request?.IsStopped == true) return best;
            // A verified loss ten rounds away is not the last opportunity to
            // drink a chooser potion. Preserve that resource for a bad hand;
            // imminent death and a critically wounded forecast victim still
            // release every useful bottle, including repeated free actions.
            var emergency = LastChance(root, owner, party, forecast);
            var full = root.HeldPotionCount(owner) >= root.PotionCapacity(owner) && root.PotionCapacity(owner) > 0;
            if (!emergency && !full) continue;
            foreach (var potion in root.UsablePotions(owner))
            foreach (var target in root.PotionTargets(potion))
            {
                if (request?.IsStopped == true) return best;
                var value = Measure(root, potion, target, party, emergency);
                if (plan is not null && potion.GetType().Name is "ShacklingPotion" or "BeetleJuice" or "WeakPotion" or "BlockPotion" or "Fortifier")
                {
                    var marginal = KernelPotionPolicy.ImmediateBenefit(root, party, new(owner, null, target, potion), plan);
                    value = (Math.Min(value.Benefit, Math.Max(0, marginal)), value.FullValue && marginal >= value.Benefit);
                }
                if (value.Benefit <= .01 || !emergency && !value.FullValue) continue;
                var score = value.Benefit + (emergency ? 10000 : 0);
                if (score <= bestScore) continue;
                bestScore = score;
                var capacity = IsGeneratedChoice(potion)
                    ? $":recipient-hand={root.Hand(target?.Player ?? owner).Count}/10" : "";
                best = new(new(owner, null, target, potion),
                    $"{(emergency ? "predicted-death" : "full-belt")}:benefit={value.Benefit:F1}{capacity}", value.Benefit);
            }
        }
        return best;
    }

    // Strong mandatory-spending preferences still publish only executable
    // supported choice actions. Use the existing Touch-only native continuation
    // adapter, its bounded cost-priority menu, original bottle and delegated
    // recipient. No bare fallback when its menu/prefix cannot be completed.
    // The surrounding Choose request scope owns the same deadline; no search.
    // Actual 2026-10-12 baseline05 and production withdrawal R2 RED:
    // "Touch opportunity: actual mandatory opportunity must publish complete Touch choices before Core confirmation"
    // Native DIAG: full-belt potion=TOUCH_OF_INSANITY choices=-1.
    // R2 [0,0,1,0,0,0] restored all 1281 sources/31 DLL+PDB exactly,
    // including forced builds; r2-producer-v1/proof.json records the raw commands.
    private static KernelTeamSearch.Action? MandatoryChoiceAction(KernelSession root,
        PotionModel potion, Creature? target, IReadOnlyList<Player> actors)
        => root.PotionActions(potion, target, maximumChoices: 1, controlledActors: actors).FirstOrDefault();

    /// <summary>
    /// Boss 开幕倒瓶的目标选择与浪费闸门（owner 2026-10-05 追加）：
    /// CHOOSER 与 SwiftPotion 在目标手牌满 10 张时跳过；能量药只给真的缺能量的席位；
    /// 其余瓶子自己优先、再取第一个合法目标。返回 null 表示"这瓶现在喝就是浪费，留着"。
    /// </summary>
    private static Creature? BossOpeningTarget(KernelSession root, Player owner, PotionModel potion,
        IReadOnlyList<Creature?> targets, KernelDecisionRequest? request = null,
        IReadOnlyList<Player>? actors = null)
    {
        var name = potion.GetType().Name;
        if (potion.GetType() == typeof(BlockPotion))
        {
            // Resolve an eligible offensive/draw/choice bottle first, through
            // the existing use-and-recapture path. An unresolved AnyPlayer
            // chooser is not evidence that block has zero future utility.
            foreach (var nextOwner in (actors ?? root.Party).Where(p => root.CanAct(p) && root.CanUsePotions(p)))
            foreach (var next in root.UsablePotions(nextOwner).Where(q => q.GetType() != typeof(BlockPotion)))
            {
                if (request?.IsStopped == true) return null;
                var legal = root.PotionTargets(next);
                if (legal.Count > 0 && BossOpeningTarget(root, nextOwner, next, legal, request, actors) is not null)
                    return null;
            }
            // The named expiry/attack/retention/offense witness exercises this
            // bounded decision separately. Unknown hooks or generators retain
            // the established opening rule; preserve its original target.
            var preferred = targets.FirstOrDefault(t => t == owner.Creature) ?? targets[0];
            return preferred is not null && OpeningBlockProvenZero(root, potion, preferred, request)
                ? null : preferred;
        }
        if (IsGeneratedChoice(potion))
            return targets.FirstOrDefault(t => t?.Player is { } p && root.Hand(p).Count < 10);
        if (name == "SwiftPotion")
            return targets.FirstOrDefault(t => t?.Player is { } p
                && root.Hand(p).Count < 10 && root.DrawPile(p).Count > 0);
        if (name == "EnergyPotion")
        {
            Creature? best = null;
            var bestNeed = 0;
            foreach (var target in targets)
            {
                if (target?.Player is not { } p) continue;
                var need = EnergyNeed(root, p);
                if (need <= bestNeed) continue;
                bestNeed = need;
                best = target;
            }
            return best;
        }
        // 满血治疗不是"用完"，而是把瓶子倒进下水道：20% 治疗被上限吃光。
        // 实测 2026-10-05 最终 boss：Bot1 99/99 仍喝 BLOOD_POTION。
        if (name is "BloodPotion" or "Ambergris")
        {
            Creature? best = null;
            var bestMissing = 0;
            foreach (var target in targets)
            {
                if (target is null || root.Hp(target) <= 0 || target.Player is null) continue;
                var missing = root.MaxHp(target) - root.Hp(target);
                if (missing <= bestMissing) continue;
                bestMissing = missing;
                best = target;
            }
            return bestMissing > 0 ? best : null;
        }
        // 无敌回合打伤害药水同样是在浪费瓶子；只有在至少一个敌人没有
        // IntangiblePower 时才允许 Boss 开幕使用。
        if (name is "FirePotion" or "PotionShapedRock" or "ExplosiveAmpoule")
        {
            var enemies = targets.Where(t => t is { Monster: not null } && root.Hp(t) > 0).ToArray();
            if (enemies.Length == 0) return null;
            if (enemies.All(t => root.Power<IntangiblePower>(t!) > 0)) return null;
        }
        return targets.FirstOrDefault(t => t == owner.Creature) ?? targets[0];
    }

    private static readonly MethodInfo[] BlockUtilityHooks = typeof(AbstractModel)
        .GetMethods(BindingFlags.Instance | BindingFlags.Public)
        .Where(m => m.Name is nameof(AbstractModel.BeforeBlockGained) or nameof(AbstractModel.AfterBlockGained)
            or nameof(AbstractModel.AfterBlockBroken) or nameof(AbstractModel.AfterBlockCleared)
            or nameof(AbstractModel.AfterPreventingBlockClear) or nameof(AbstractModel.ShouldClearBlock)
            or nameof(AbstractModel.BeforeDamageReceived) or nameof(AbstractModel.AfterDamageReceived)
            or nameof(AbstractModel.BeforePotionUsed) or nameof(AbstractModel.AfterPotionUsed))
        .ToArray();

    private static bool MayHaveBlockHook(Type type) => BlockUtilityHooks.Any(b =>
        type.GetMethod(b.Name, BindingFlags.Instance | BindingFlags.Public, null,
            b.GetParameters().Select(p => p.ParameterType).ToArray(), null)?.DeclaringType != b.DeclaringType);

    private static readonly MethodInfo[] UnresolvedOpeningBlockHooks = typeof(AbstractModel)
        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
        .Where(m => m.IsVirtual && !m.IsSpecialName
            && new[] { "Card", "Draw", "Hand", "AutoPlay", "Damage", "Potion", "Block" }
                .Any(family => m.Name.Contains(family, StringComparison.Ordinal)))
        .ToArray();

    private static bool HasUnknownOpeningBlockDomain(KernelSession root, SimulatedCombatState combat)
        => combat.IterateHookListeners().Any(model => UnresolvedOpeningBlockHooks.Any(hook =>
            model.GetType().GetMethod(hook.Name, BindingFlags.Instance | BindingFlags.Public, null,
                hook.GetParameters().Select(p => p.ParameterType).ToArray(), null)?.DeclaringType != hook.DeclaringType))
            || root.Party.Any(player => root.Simulator.State.GetPlayerCombatState(player).AllPiles
                .SelectMany(pile => pile.Cards).Any(card => card.Preview.Enchantment is not null
                    || card.Preview.Affliction is not null));

    private static bool OpeningBlockProvenZero(KernelSession root, PotionModel potion, Creature target,
        KernelDecisionRequest? request)
    {
        if (target.Player is not { } recipient || !root.CanFork || root.ProjectionDifferences.Count != 0
            || root.LastActionHadEnvironmentalRisk || root.RoundsAdvanced != 0 || request?.IsStopped == true)
            return false;
        try
        {
            var combat = (SimulatedCombatState)root.Simulator.State.CombatState;
            if (!combat.ShouldClearBlock(target) || root.OrbCount(recipient) > 0 || root.HasOsty(recipient)) return false;
            // Incoming is only an early positive guard. A zero intent by itself
            // never proves zero utility: the bounded lifecycle witness below
            // must settle and be fully covered before this method returns true.
            var threat = root.Threat(recipient);
            if (threat.HandDamage > 0 || threat.AttackDamage > 0 || threat.LateDamage > 0) return false;
            // A plain card can still generate unknown cards through an active
            // lifecycle hook or its attachments. A no-card side probe does not
            // establish their future block independence. Unknown keeps use.
            if (HasUnknownOpeningBlockDomain(root, combat)) return false;
            if (combat.IterateHookListeners().Any(m => m.GetType().Assembly != typeof(BlockPotion).Assembly
                || MayHaveBlockHook(m.GetType()))) return false;

            foreach (var owner in root.Party)
            {
                // A not-yet-resolved chooser is unknown, including a teammate's
                // AnyPlayer bottle. Do not infer its future choice from a log.
                if (root.UsablePotions(owner).Any(q => q != potion && !KernelSession.CanModelPotion(q))) return false;
                foreach (var card in root.Simulator.State.GetPlayerCombatState(owner).AllPiles
                    .SelectMany(p => p.Cards).Select(c => c.Preview))
                {
                    if (card.GetType().Assembly != typeof(BlockPotion).Assembly || card.HasTurnEndInHandEffect) return false;
                    // A registered OnPlay mirror is not a proof of future block
                    // independence. Keep this bounded to reviewed plain attacks,
                    // plain block and exactly two native Shiv generators. Every
                    // other card, including unknown Status/Curse, is unknown.
                    if (card is not (StrikeSilent or DefendSilent or StrikeIronclad or DefendIronclad
                        or Shiv or AscendersBane or BladeDance or CloakAndDagger)) return false;
                }
            }
            if (request?.IsStopped == true) return false;
            var after = root.Fork();
            if (!after.UsePotion(potion, target, out _) || after.LastActionHadEnvironmentalRisk) return false;
            if (root.Party.Any(p => root.Hp(p.Creature) != after.Hp(p.Creature))
                || root.Enemies.Any(e => root.Hp(e) != after.Hp(e))
                || root.Party.Any(p => p != recipient && root.Block(p.Creature) != after.Block(p.Creature))) return false;
            if (request?.IsStopped == true) return false;
            var baseline = root.Fork();
            bool ToNextPlayerSide(KernelSession s)
            {
                foreach (var p in root.Party)
                {
                    if (request?.IsStopped == true) return false;
                    if (s.CanAct(p) && !s.EndTurn(p, 2, out _)) return false;
                    if (s.RoundsAdvanced == 1) return !s.LastActionHadEnvironmentalRisk && !s.HasTerminal;
                }
                return false;
            }
            if (!ToNextPlayerSide(baseline) || !ToNextPlayerSide(after) || request?.IsStopped == true) return false;
            return root.Party.All(p => baseline.Hp(p.Creature) == after.Hp(p.Creature)
                    && baseline.Block(p.Creature) == after.Block(p.Creature))
                && root.Enemies.All(e => baseline.Hp(e) == after.Hp(e))
                && baseline.Simulator.Rng.ShuffleState == after.Simulator.Rng.ShuffleState
                && baseline.Simulator.Rng.CombatCardSelectionState == after.Simulator.Rng.CombatCardSelectionState;
        }
        catch
        {
            // A failed/missing lifecycle fact is unknown, never a zero proof.
            return false;
        }
    }

    /// <summary>
    /// 手牌里正费牌的总能量费用 − 现有能量；&lt;=0 表示多给的能量花不掉。
    /// 注意不能用 CanPlay 过滤：0 能量的队友手里全打不出去，需要能量才变得可打，
    /// 过滤掉就永远得不出"他缺能量"的结论（2026-10-05 测试实测）。
    /// </summary>
    private static int EnergyNeed(KernelSession root, Player player)
    {
        var cost = 0;
        foreach (var card in root.Hand(player))
        {
            var energy = root.AttackCosts(card).Energy;
            if (energy > 0) cost += energy;
        }
        return cost - root.Energy(player);
    }

    // These bottles have explicit full-dose criteria in Measure. Missing an
    // effect mirror OR missing these criteria must not imprison a full slot.
    private static bool HasFullBeltValuation(PotionModel potion)
        => IsGeneratedChoice(potion) || KernelSession.CanModelPotion(potion)
            && potion.GetType().Name is ("BlockPotion" or "Fortifier" or "ExplosiveAmpoule"
                or "FirePotion" or "PotionShapedRock" or "BloodPotion" or "FlexPotion"
                or "SpeedPotion" or "BlessingOfTheForge" or "StrengthPotion"
                or "DexterityPotion" or "GigantificationPotion" or "FyshOil");

    private static bool IsGeneratedChoice(PotionModel potion)
        => potion.GetType().Name is "AttackPotion" or "SkillPotion" or "PowerPotion" or "ColorlessPotion";

    public static (double Benefit, bool FullValue) Measure(KernelSession root,
        PotionModel potion, Creature? target, IReadOnlyList<Player> party, bool emergency = false)
    {
        var name = potion.GetType().Name;
        if (IsGeneratedChoice(potion))
        {
            var recipient = target?.Player ?? potion.Owner;
            if (!root.CanAct(recipient) || root.Hand(recipient).Count >= 10) return (0, false);
            var offers = root.PreviewPotionOffers(potion, target, [recipient]);
            var benefit = offers.Count == 0 ? 0 : offers.Max(o => o.Benefit);
            // Free choice cards have no fixed 12-block/30-damage maximum. A
            // useful immediate choice is enough to release an occupied slot.
            // At last chance, still expose the live chooser when its uncommon
            // offered mechanic is not understood by this bounded probe.
            return (Math.Max(benefit, emergency ? 1 : 0), benefit > .01);
        }
        if (!KernelSession.CanModelPotion(potion))
        {
            // Choice bottles are executed live, followed by a fresh capture. Never
            // pretend their unresolved pick is part of a replayable combat script.
            var recipient = target?.Player ?? potion.Owner;
            var usableChoice = name switch
            {
                "Ashwater" => root.Hand(recipient).Any(c => c.Type is MegaCrit.Sts2.Core.Entities.Cards.CardType.Curse or MegaCrit.Sts2.Core.Entities.Cards.CardType.Status),
                "DropletOfPrecognition" => root.DrawPile(recipient).Count > 0,
                "GamblersBrew" or "TouchOfInsanity" => root.Hand(recipient).Count > 0,
                "LiquidMemories" => root.Discard(recipient).Count > 0,
                _ => false,
            };
            return (emergency && usableChoice ? 1 : 0, false);
        }
        var after = root.Fork();
        if (!after.UsePotion(potion, target, out _)) return (0, false);
        var damage = root.Enemies.Sum(e => Math.Max(0, root.Hp(e) - after.Hp(e)));
        double Exposed(KernelSession s, Creature c) => c.Player is { } p ? s.Threat(p).HpLoss : 0;
        var saved = party.Sum(p => Math.Max(0, Exposed(root, p.Creature) - Exposed(after, p.Creature))
            + Math.Max(0, after.Hp(p.Creature) - root.Hp(p.Creature)));
        if (name is "BlockPotion" or "Fortifier" && target is not null)
        {
            var dose = after.Block(target) - root.Block(target);
            var useful = Math.Min(dose, Exposed(root, target));
            return (useful, dose > 0 && useful >= dose);
        }
        // Intangible caps every damage bottle to 1; only a lethal bottle is worth drinking.
        if (name is "ExplosiveAmpoule" or "FirePotion" or "PotionShapedRock")
        {
            var alive = root.Enemies.Where(e => root.Hp(e) > 0).ToArray();
            if (alive.Length > 0
                && alive.All(e => root.Power<IntangiblePower>(e) > 0)
                && !alive.Any(e => after.Hp(e) <= 0))
                return (0, false);
        }
        if (name == "ExplosiveAmpoule") return (damage + saved, damage >= 30);
        if (name is "FirePotion" or "PotionShapedRock")
        {
            var nominal = (double)root.PotionValue(potion, "Damage");
            return (damage + saved, nominal > 0 && damage >= nominal);
        }
        if (name == "BloodPotion" && target is not null)
        {
            var dose = Math.Floor(root.MaxHp(target) * (double)root.PotionValue(potion, "HealPercent") / 100);
            var useful = after.Hp(target) - root.Hp(target);
            return (useful, dose > 0 && useful >= dose);
        }
        if (name is "FlexPotion" or "SpeedPotion")
        {
            if (target?.Player is not { } recipient || !root.CanAct(recipient)) return (0, false);
            var (benefit, triggers) = Amplification(root, after, recipient, name == "FlexPotion");
            // Actual extra damage / useful block, and three payable amplifications.
            return (benefit, triggers >= 3 && benefit >= 15);
        }
        var tactical = KernelTacticalPolicy.Value(after, party) - KernelTacticalPolicy.Value(root, party);
        var benefitNow = Math.Max(damage + saved, tactical);
        if (name is "BlessingOfTheForge" or "StrengthPotion" or "DexterityPotion" or "GigantificationPotion" or "FyshOil")
        {
            if (target?.Player is not { } recipient || !root.CanAct(recipient)) return (benefitNow, false);
            var offense = Amplification(root, after.Fork(), recipient, attack: true);
            var defense = Amplification(root, after.Fork(), recipient, attack: false);
            // Persistent buffs need not wait for lethal. Either branch must
            // independently realize two paid improvements; summing both would
            // spend the same energy twice. Temporary +5 keeps its 3-trigger rule.
            var useful = Math.Max(offense.Benefit, defense.Benefit);
            // Oil's native dose is +1/+1, so two paid improvements realize
            // two points, not the four required of the larger single-stat bottles.
            var minimum = name == "FyshOil" ? 2 : 4;
            return (emergency ? Math.Max(benefitNow, useful) : useful, offense.Triggers >= 2 && offense.Benefit >= minimum
                || defense.Triggers >= 2 && defense.Benefit >= minimum);
        }
        if (emergency && name is "VulnerablePotion" or "Duplicator" or "StrengthPotion" or "GigantificationPotion")
        {
            // A debuff/replay bottle changes the next attack, not HP at drink
            // time. Both were present in V9's final fight; a paired attack probe
            // exposed their zero immediate valuation. Resolve payable attacks
            // before calling these zero value.
            var beneficiaries = name == "VulnerablePotion" ? party
                : party.Where(p => p.Creature == target).ToArray();
            benefitNow = Math.Max(benefitNow, beneficiaries.Where(root.CanAct)
                .Sum(p => Amplification(root, after, p, attack: true).Benefit));
        }
        // Persistent buffs and debuffs can be useful even though no card or enemy
        // turn has resolved yet. The state value owns their known future benefit.
        return (Math.Max(0, benefitNow), false);
    }

    private static (double Benefit, int Triggers) Amplification(KernelSession baseline,
        KernelSession boosted, Player player, bool attack)
    {
        // Paired real branches enforce shared energy, modified costs, exhaust and
        // target survival. Counting three individually payable cards overvalued
        // hands such as three two-cost attacks with only three energy.
        var before = baseline.Fork();
        double benefit = 0;
        var triggers = 0;
        for (var step = 0; step < 3; step++)
        {
            KernelSession? nextBase = null, nextBoost = null;
            double best = 0, bestRank = 0;
            foreach (var card in before.Hand(player).Where(c => attack ? c.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Attack : c.GainsBlock)
                         .GroupBy(before.CardStateKey).Select(g => g.First()))
            {
                if (!before.CanPlay(card) || !boosted.CanPlay(card)) continue;
                foreach (var target in before.Targets(card))
                {
                    if (!boosted.Targets(card).Contains(target)) continue;
                    var a = before.Fork(); var b = boosted.Fork();
                    if (!a.Play(card, target, out _) || !b.Play(card, target, out _)) continue;
                    var extra = attack
                        ? before.Enemies.Sum(e => Math.Max(0, a.Hp(e) - b.Hp(e)))
                        : Math.Max(0, b.Block(player.Creature) - a.Block(player.Creature));
                    // Subtract previous accumulated divergence, not count it again.
                    var existing = attack
                        ? before.Enemies.Sum(e => Math.Max(0, before.Hp(e) - boosted.Hp(e)))
                        : Math.Max(0, boosted.Block(player.Creature) - before.Block(player.Creature));
                    var gain = extra - existing;
                    var spent = Math.Max(.25, before.Energy(player) - a.Energy(player));
                    var rank = gain / spent;
                    if (gain <= 0 || rank <= bestRank) continue;
                    best = gain; bestRank = rank; nextBase = a; nextBoost = b;
                }
            }
            if (nextBase is null || nextBoost is null) break;
            before = nextBase; boosted = nextBoost;
            benefit += best; triggers++;
        }
        if (!attack)
        {
            var incoming = boosted.CurrentAttacks().Sum(a => boosted.IntentHit(a.Enemy, player.Creature, a.Raw) * a.Repeats);
            benefit = Math.Min(benefit, Math.Max(0, incoming - before.Block(player.Creature) - before.EndPlating(player.Creature)));
        }
        return (benefit, triggers);
    }
}
