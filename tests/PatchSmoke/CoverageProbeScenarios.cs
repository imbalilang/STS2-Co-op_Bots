using System.Reflection;
using CoopBots;
using CoopBots.Building;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

/// <summary>
/// Read-only sweep, not a gate: prints <see cref="BuildCoverage.Of"/> and every
/// fact that feeds it for every Ironclad / Silent card in the local build. Used
/// to find cards the model claims to read (coverage 1.00) whose printed
/// per-event scaling is absent from <see cref="CardProfile.Facts"/>.
///
/// 跑法：<c>… -- --coverage-probe</c>
/// </summary>
internal static class CoverageProbeScenarios
{
    private static bool Format(ref string __result) { __result = ""; return false; }

    internal static void Run()
    {
        new Harmony("coopbots.coverage.probe.presentation").Patch(
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(CoverageProbeScenarios), nameof(Format)));

        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var combat = new CombatState(runState: RunState.CreateForTest([bot], seed: "COVERAGE-PROBE"));
        bot.ResetCombatState();
        combat.AddPlayer(bot);

        foreach (var character in new CharacterModel[] { ModelDb.Character<Ironclad>(), ModelDb.Character<Silent>() })
        {
            foreach (var template in character.CardPool.AllCards.OrderBy(c => c.Id.Entry, StringComparer.Ordinal))
            {
                CardModel card;
                try { card = combat.CreateCard(template, bot); }
                catch { continue; }

                var facts = CardProfile.Of(card);
                var coverage = BuildCoverage.Of(card);
                Console.WriteLine(
                    $"COV class={character.GetType().Name} id={card.Id.Entry} type={card.Type} "
                    + $"coverage={coverage:F2} unsupported={SustainedDeckValue.Unsupported(card)} "
                    + $"dmg={facts.Damage:F2} blk={facts.Block:F2} draw={facts.Draw:F2} en={facts.Energy:F2} "
                    + $"vul={facts.Vulnerable:F2} weak={facts.Weak:F2} strdown={facts.StrengthDown:F2} "
                    + $"poison={facts.Poison:F2} doom={facts.Doom:F2} scaling={facts.Scaling:F2} "
                    + $"hploss={facts.HpLoss:F2} cost={facts.Cost} hits={facts.Hits} aoe={facts.IsAoe} "
                    + $"vars=[{Vars(card)}]");
            }
        }
    }

    /// <summary>
    /// Measurement only: for every Ironclad / Silent card, capture a combat and
    /// ask the kernel whether it can produce a boundary-free branch. A card with
    /// no such branch is a card the live tournament cannot play at all.
    /// 跑法：<c>… -- --kernel-cards-probe</c>
    /// </summary>
    internal static void KernelCoverage()
    {
        new Harmony("coopbots.coverage.probe.presentation").Patch(
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(CoverageProbeScenarios), nameof(Format)));

        foreach (var character in new CharacterModel[] { ModelDb.Character<Ironclad>(), ModelDb.Character<Silent>() })
        foreach (var template in character.CardPool.AllCards.OrderBy(c => c.Id.Entry, StringComparer.Ordinal))
        {
            foreach (var upgraded in new[] { false, true })
            {
                var name = template.Id.Entry + (upgraded ? "+" : "");
                try
                {
                    var (party, combat, enemy) = KernelBoard();
                    var owner = party[0];
                    var card = combat.CreateCard(template, owner);
                    if (upgraded) card.UpgradeInternal();
                    owner.PlayerCombatState!.Hand.AddInternal(card);
                    Creature? target = card.TargetType switch
                    {
                        TargetType.AnyEnemy => enemy,
                        TargetType.AnyAlly or TargetType.AnyPlayer => party[1].Creature,
                        _ => null,
                    };
                    var root = KernelSession.Capture(combat);
                    var branches = root.CardBranches(card, target, maximumBranches: 8).ToArray();
                    var playable = branches.FirstOrDefault(b => b.Boundary.Length == 0) ?? branches.FirstOrDefault();
                    var boundary = branches.FirstOrDefault()?.Boundary ?? "no-branch";
                    var structural = playable?.State.LastActionHadEnvironmentalRisk ?? false;
                    var choices = playable?.Choices.Count ?? 0;
                    var gaps = playable is null ? Array.Empty<PredictionGap>()
                        : PredictionCoverage.Collect(Simulator(playable.State))
                            .Where(g => g.Method == "OnPlay" && g.SourceId == card.Id.Entry).ToArray();
                    var gapText = string.Join(",", gaps.Select(g => $"{g.Reason}(comp={g.Compensated})"));
                    Console.WriteLine($"KCOV {character.GetType().Name} id={name} type={card.Type} "
                        + $"cost={CardProfile.Of(card).Cost} ok={branches.Any(b => b.Boundary.Length == 0)} "
                        + $"structural={structural} choices={choices} boundary=[{boundary}] gaps=[{gapText}]");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"KCOV {character.GetType().Name} id={name} EXCEPTION "
                        + $"{ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Measurement only: does the tournament's own CardBranches path actually
    /// apply Nightmare (NightmarePower + next-turn copies), or is the choice
    /// resolved but the effect dropped?
    /// 跑法：<c>… -- --nightmare-probe</c>
    /// </summary>
    internal static void NightmareProbe()
    {
        new Harmony("coopbots.coverage.probe.presentation").Patch(
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(CoverageProbeScenarios), nameof(Format)));

        AllocationProbe.RefreshEnabled();
        var (party, combat, _) = KernelBoard();
        var owner = party[0];
        foreach (var card in owner.PlayerCombatState!.Hand.Cards.ToArray())
            owner.PlayerCombatState.Hand.RemoveInternal(card);
        var nightmare = combat.CreateCard(ModelDb.Card<Nightmare>(), owner);
        owner.PlayerCombatState.Hand.AddInternal(nightmare);
        var pinpoint = combat.CreateCard(ModelDb.Card<Pinpoint>(), owner);
        owner.PlayerCombatState.Hand.AddInternal(pinpoint);
        for (var i = 0; i < 5; i++)
            owner.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard(ModelDb.Card<StrikeSilent>(), owner));

        var root = KernelSession.Capture(combat);
        var branches = root.CardBranches(nightmare, null, 8).ToArray();
        foreach (var branch in branches)
        {
            var choices = string.Join("|", branch.Choices.Select(choice =>
                string.Join(",", choice.Cards.Select(token => token.Id))));
            var power = State(branch.State).EffectivePowers().OfType<NightmarePower>().Any();
            Console.WriteLine($"NMPROBE boundary=[{branch.Boundary}] choices=[{choices}] power={power}");
        }
        var playable = branches.FirstOrDefault(b => b.Boundary.Length == 0);
        if (playable is null) return;
        var after = playable.State;
        Console.WriteLine($"NMPROBE persistent before={root.PersistentValue(owner, 10, 1):F2} "
            + $"after={after.PersistentValue(owner, 10, 1):F2}");
        after.AutoResolveTurnStartChoices = true;
        after.AutoResolveEnemyChoices = true;
        foreach (var player in party)
            if (!after.EndTurn(player, 3, out var boundary))
            {
                Console.WriteLine($"NMPROBE end-turn stopped at {boundary}");
                break;
            }
        Console.WriteLine("NMPROBE next-hand=" + string.Join(",", after.Hand(owner).Select(c => c.Id.Entry)));

        // Tournament path: is Nightmare selected when copying Anger (0-cost) is
        // clearly the best long-run play available in this hand?
        var (party2, combat2, enemy2) = KernelBoard();
        var owner2 = party2[0];
        foreach (var card in owner2.PlayerCombatState!.Hand.Cards.ToArray())
            owner2.PlayerCombatState.Hand.RemoveInternal(card);
        owner2.PlayerCombatState.Hand.AddInternal(combat2.CreateCard(ModelDb.Card<Nightmare>(), owner2));
        owner2.PlayerCombatState.Hand.AddInternal(combat2.CreateCard(ModelDb.Card<Anger>(), owner2));
        owner2.PlayerCombatState.Hand.AddInternal(combat2.CreateCard(ModelDb.Card<StrikeIronclad>(), owner2));
        enemy2.SetMaxHpInternal(2000);
        enemy2.SetCurrentHpInternal(2000);
        var options = new TournamentOptions(TopK: 6, IncludePotions: false, MaxDegreeOfParallelism: 1);
        var firstActions = KernelTournament.FirstActions(KernelSession.Capture(combat2), party2, 6, options);
        Console.WriteLine("NMPROBE first-actions=" + string.Join(",",
            firstActions.Select(a => a.Card?.Id.Entry ?? (a.EndTurn ? "END" : "?"))));
        var result = KernelTournament.Run(KernelSession.Capture(combat2), party2, party2, options, 30000);
        Console.WriteLine("NMPROBE tournament-script=" + string.Join(",", (result.Script ?? [])
            .Take(6).Select(a => a.Card?.Id.Entry ?? (a.EndTurn ? "END" : "?"))));
        Console.WriteLine("NMPROBE alloc=" + AllocationProbe.ReportAndReset());
    }

    private static CombatPredictionSimulator Simulator(KernelSession session)
        => (CombatPredictionSimulator)AccessTools.Field(typeof(KernelSession), "simulator").GetValue(session)!;

    private static SimulatedCombatState State(KernelSession session)
        => (SimulatedCombatState)AccessTools.Property(typeof(KernelSession), "Combat").GetValue(session)!;

    private static (Player[], CombatState, Creature) KernelBoard()
    {
        var party = Enumerable.Range(0, 4)
            .Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 77, i)))
            .ToArray();
        var run = RunState.CreateForTest(party, seed: "KERNEL-CARD-AUDIT");
        var combat = new CombatState(runState: run);
        foreach (var p in party)
        {
            p.ResetCombatState();
            combat.AddPlayer(p);
            p.MaxEnergy = 99;
            p.Creature.SetMaxHpInternal(80);
            p.Creature.SetCurrentHpInternal(80);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
            p.PlayerCombatState.GainEnergy(9);
            foreach (var support in new CardModel[]
            {
                ModelDb.Card<StrikeIronclad>(), ModelDb.Card<DefendIronclad>(), ModelDb.Card<Shiv>(),
                ModelDb.Card<Backflip>(), ModelDb.Card<Bloodletting>(), ModelDb.Card<StrikeSilent>(),
                ModelDb.Card<DefendSilent>(), ModelDb.Card<Neutralize>(),
            })
                p.PlayerCombatState.Hand.AddInternal(combat.CreateCard(support, p));
            for (var i = 0; i < 6; i++)
                p.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard(ModelDb.Card<StrikeIronclad>(), p));
        }
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "enemy");
        combat.AddCreature(enemy);
        enemy.Monster!.SetUpForCombat();
        enemy.SetMaxHpInternal(500);
        enemy.SetCurrentHpInternal(500);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        return (party, combat, enemy);
    }

    private static string Vars(CardModel card)
    {
        try
        {
            return string.Join(",", card.DynamicVars.OrderBy(v => v.Key, StringComparer.Ordinal)
                .Select(v => $"{v.Key}:{v.Value.GetType().Name}:{v.Value.BaseValue}"));
        }
        catch (Exception ex) { return "vars-error:" + ex.GetType().Name; }
    }

    /// <summary>
    /// Measurement only: rebuild the live 2026-10-02 shop deck as far as the log
    /// names it, then print TEAR_ASUNDER's add/remove value and the removal
    /// ranking before/after the coverage fix.
    /// </summary>
    internal static void TearRemoval()
    {
        new Harmony("coopbots.coverage.probe.presentation").Patch(
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(CoverageProbeScenarios), nameof(Format)));

        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var combat = new CombatState(runState: RunState.CreateForTest([bot], seed: "TEAR-REMOVAL"));
        bot.ResetCombatState();
        combat.AddPlayer(bot);
        foreach (var raw in ("ASCENDERS_BANE BASH EXPOSE HELLRAISER INFERNAL_BLADE POMMEL_STRIKE POMMEL_STRIKE+ "
            + "SECOND_WIND+ SHRUG_IT_OFF SHRUG_IT_OFF SPOILS_MAP TAUNT+ TEMPEST TEAR_ASUNDER ARMAMENTS "
            + "BLOODLETTING COLOSSUS FIEND_FIRE FIGHT_ME UNRELENTING UPPERCUT").Split(' '))
        {
            var id = raw.TrimEnd('+');
            var card = combat.CreateCard(ModelDb.AllCards.First(c => c.Id.Entry == id), bot);
            if (raw.EndsWith('+')) card.UpgradeInternal();
            bot.Deck.AddInternal(card);
        }

        var tear = bot.Deck.Cards.First(c => c.Id.Entry == "TEAR_ASUNDER");
        var without = bot.Deck.Cards.Where(c => !ReferenceEquals(c, tear)).ToList();
        var with = bot.Deck.Cards.ToList();
        var add = BuildValue.Add(tear, bot, without);
        var remove = BuildValue.Remove(tear, bot, with);
        Console.WriteLine($"TEAR coverage={BuildCoverage.Of(tear):F2}");
        Console.WriteLine($"TEAR add={add.Total:F2} [{add.Reason}]");
        Console.WriteLine($"TEAR remove-raw={remove.Total:F2} [{remove.Reason}]");
        Console.WriteLine($"TEAR shopGold={BotShopPlanner.GoldPerDeckValue * BotShopPlanner.RemovalValue(tear, bot):F1} "
            + $"bar={150 * RunDepth.ShopThresholdFactor(bot):F1}");
        foreach (var (id, value) in BotShopPlanner.RemovalCandidates(with)
            .Select(i => (Id: with[i].Id.Entry, Value: BotShopPlanner.RemovalValue(with[i], bot)))
            .OrderByDescending(x => x.Value).ThenBy(x => x.Id, StringComparer.Ordinal).Take(6))
            Console.WriteLine($"TEAR rank {id} value={value:F2} gold={BotShopPlanner.GoldPerDeckValue * value:F1}");
    }
}
