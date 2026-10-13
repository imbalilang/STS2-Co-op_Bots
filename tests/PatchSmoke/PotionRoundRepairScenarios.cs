using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;
using System.Text.Json;

internal static class PotionRoundRepairScenarios
{
    // v2 R2: three actual source faults, physical source/DLL/PDB, immediate
    // build/run exits 0/1, exact byte restore and exits 0/0. Named RED excerpts:
    // clean repair removed -> "a pre-transaction unplayable card must freshly resolve";
    // emergency combination inserted first -> "both actual single-bottle endings must precede";
    // unplayable called target gap -> "an absent-hand Concoct must remain a separate native unplayable reason".
    // Receipts: work/szg-repair-20261008T124624/model/potion-round-repair-v2/r2/proof.json.
    // v1's ordinary-no-repair assertion was this round's internal assumption,
    // not an upstream/native safety rule. It is superseded by a legal current-
    // round repair and strict full-party checkpoint/actual-action replay.
    // Missing metadata, native extra turns and mismatched actual owner/counter
    // controls still retain complete formal tails. Reconstructed endings match
    // whole-party piles, checkpoints and all nine RNG streams. The added order
    // control actually completes both single jobs and the retained combination.
    // Existing Quality fixtures and their 3200/16 limits remain unchanged.
    private static bool Format(ref string __result) { __result = ""; return false; }
    internal static void Run(string? only = null)
    {
        TestEnvironment.Ensure();
        var hooks = new Harmony("coopbots.potion-round-repair-tests");
        hooks.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(PotionRoundRepairScenarios), nameof(Format)));
        try
        {
            foreach (var name in new[] { "future-menu", "phase-gap", "missing-metadata", "extra-turn", "wrong-owner-turn", "same-turn-extra-card", "ordinary-unplayable", "single-and-combination" })
                if (only is null || only == name) {
                    if (name == "future-menu") FutureMenu();
                    else if (name == "single-and-combination") SingleAndCombination();
                    else Case(name);
                }
        }
        finally
        {
            foreach (var method in hooks.GetPatchedMethods().ToArray()) hooks.Unpatch(method, HarmonyPatchType.All, hooks.Id);
        }
    }
    private static void Case(string name)
    {
        var player = Player.CreateForNewRun<Deprived>(UnlockState.all, 62000);
        Player[] party = [player];
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "POTION-ROUND-GAP")) { RoundNumber = 1 };
        player.ResetCombatState(); combat.AddPlayer(player); player.MaxEnergy = 2;
        player.PlayerCombatState!.Phase = PlayerTurnPhase.Play; player.PlayerCombatState.GainEnergy(2);
        player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(67);
        foreach (var model in new[] { ModelDb.Card<StrikeIronclad>(), ModelDb.Card<StrikeIronclad>() })
            player.PlayerCombatState.Hand.AddInternal(combat.CreateCard(model, player));
        Shiv? extraCard = null;
        if (name == "same-turn-extra-card")
        {
            extraCard = combat.CreateCard<Shiv>(player);
            player.PlayerCombatState.Hand.AddInternal(extraCard);
        }
        if (name == "extra-turn") ModelDb.Power<AmbergrisPower>().ToMutable().ApplyInternal(player.Creature, 1, true);
        DefendIronclad? absentHandCard = null;
        if (name == "ordinary-unplayable")
        {
            absentHandCard = combat.CreateCard<DefendIronclad>(player);
            player.PlayerCombatState.DrawPile.AddInternal(absentHandCard);
        }
        for (var i = 0; i < 2; i++)
        {
            var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, i.ToString());
            combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(i == 0 ? 11 : 800);
            enemy.SetCurrentHpInternal(i == 0 ? 11 : 800);
            enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
            ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(enemy, 9, true);
        }
        player.AddPotionInternal(ModelDb.Potion<FirePotion>().ToMutable(), silent: true);
        var root = KernelSession.Capture(combat); var rootText = root.PartyStateText();
        var outcome = KernelRollout.Run(root.Fork(), party, party, new(RolloutPolicyKind.Kill) {
            InvestmentLookahead = false, SequenceLookahead = true, SequenceHandLookahead = false,
            CaptureActionCheckpoints = true });
        Check(outcome.Terminal && outcome.Record.Verified && outcome.Checkpoints?.Count == outcome.Actions.Count,
            "complete native reference must carry one actually captured checkpoint per action");
        Check(outcome.Actions[0].Target == combat.Enemies[0], "native reference must actually aim at the removable enemy");
        var checkpoints = outcome.Checkpoints!;
        if (name == "extra-turn") Check(checkpoints.Any(c => c.ExtraTurn), "native Ambergris must actually reach an extra-turn boundary");
        if (name == "wrong-owner-turn") checkpoints = checkpoints.Select(c => c with {
            Seats = c.Seats.Select(s => s with { Turn = s.Turn + 1 }).ToArray() }).ToArray();
        if (name == "same-turn-extra-card")
        {
            var nativeExtra = root.CardBranches(extraCard!, combat.Enemies[1], 1).Single(b => b.Boundary.Length == 0).State.ActionCheckpoint();
            var before = root.ActionCheckpoint();
            Check(before.NativeRound == nativeExtra.NativeRound && before.Seats[0].Turn == nativeExtra.Seats[0].Turn
                && before.Seats[0].Ready == nativeExtra.Seats[0].Ready && before.Seats[0].Acting == nativeExtra.Seats[0].Acting
                && before.Seats[0].EnergySpent == nativeExtra.Seats[0].EnergySpent
                && before.Seats[0].CardsPlayed + 1 == nativeExtra.Seats[0].CardsPlayed && !before.SameBoundary(nativeExtra),
                "a native zero-cost card in the same owner turn must invalidate the untouched round checkpoint");
            checkpoints = checkpoints.Select(c => c with {
                Seats = c.Seats.Select(s => s with { CardsPlayed = s.CardsPlayed + 1 }).ToArray() }).ToArray();
        }
        var baseline = new TournamentResult(outcome.Actions[0], outcome.Record, [outcome.Record], 1, 0, "native-reference") {
            Script = name == "ordinary-unplayable" ? outcome.Actions.Select((a, i) => i == 0
                ? a with { Card = absentHandCard, CardStateKey = root.CardStateKey(absentHandCard!) } : a).ToArray()
                : outcome.Actions, ScriptAliveStates = outcome.AliveStates, ScriptEnemyStates = outcome.EnemyStates,
            ScriptCheckpoints = name == "missing-metadata" ? null : checkpoints,
        };
        var run = new KernelPotionContinuationRun(root, party, party, baseline, 2000);
        while (!run.Advance(TimeSpan.FromMilliseconds(2))) { }
        Console.WriteLine($"PROBE potion-round {name} completed={run.Completed} repairs={run.RepairedActions} resumes={run.ResumedScripts} referenceActions={outcome.Actions.Count} ms={run.ComputeMs:F1}");
        Check(run.Completed > 0 && run.CompletedResults.All(r => r.Record is { Verified: true }),
            "native potion jobs must finish real verified endings");
        if (name == "phase-gap") Check(run.ResumedScripts > 0 && run.RepairedActions > 0,
            "removed-target gap must formally settle a real round then resume its native-aligned script");
        else if (name == "ordinary-unplayable") Check(run.UnplayableRepairs > 0 && run.ResumedScripts > 0,
            "a pre-transaction unplayable card must freshly resolve a real side and strictly resume a matching native suffix");
        else Check(run.ResumedScripts == 0, "missing metadata, extra turns or mismatched actual owner turns must retain full formal tails");
        VerifyEndings(root, party, run);
        Check(root.PartyStateText() == rootText, "round repair and failed transactions must preserve the original root");
        Console.WriteLine("PASS potion-round " + name);
    }
    private static void FutureMenu()
    {
        string[] decks = ["STRIKE_IRONCLAD+,STRIKE_IRONCLAD,DEFEND_IRONCLAD,DEFEND_IRONCLAD,DEFEND_IRONCLAD,BASH+,ASCENDERS_BANE,STRIKE_IRONCLAD,DEFEND_IRONCLAD,SHRUG_IT_OFF,SHRUG_IT_OFF,POMMEL_STRIKE+,WHIRLWIND+,INFLAME+,STOKE,WHIRLWIND+,REGRET,REGRET","STRIKE_IRONCLAD,STRIKE_IRONCLAD,STRIKE_IRONCLAD,BASH+,ASCENDERS_BANE,GREED,THUNDERCLAP+,SETUP_STRIKE+,POMMEL_STRIKE,BULLY,TAUNT+,OFFERING+,BATTLE_TRANCE+,INFLAME+,TWIN_STRIKE,FIEND_FIRE+,CRUELTY,CLUMSY,HEMOKINESIS","STRIKE_SILENT,STRIKE_SILENT,STRIKE_SILENT,DEFEND_SILENT,NEUTRALIZE,ASCENDERS_BANE,GREED,DAGGER_SPRAY+,BLADE_DANCE,EXPOSE,BLADE_DANCE,BLADE_DANCE,ASSASSINATE+,HIDDEN_DAGGERS,DEFEND_SILENT+,PREPARED+,BLADE_DANCE+,PIERCING_WAIL,FINISHER,STRIKE_SILENT+,BACKFLIP,DEFEND_SILENT+,ENVENOM,PREPARED,BACKFLIP,REGRET","STRIKE_SILENT,DEFEND_SILENT+,DEFEND_SILENT,NEUTRALIZE,SURVIVOR+,ASCENDERS_BANE,BACKSTAB+,HIDDEN_DAGGERS,FLICK_FLACK+,SUCKER_PUNCH,BLADE_OF_INK+,ESCAPE_PLAN,PREPARED+,ACCURACY,PIERCING_WAIL+,PREPARED,SNEAKY+,ESCAPE_PLAN,PREPARED,DECAY,CONCOCT"];
        string[] hands = ["INFLAME+,POMMEL_STRIKE+,WHIRLWIND+,SHRUG_IT_OFF,DEFEND_IRONCLAD",
            "OFFERING+,BATTLE_TRANCE+,INFLAME+,FIEND_FIRE+,CRUELTY",
            "HIDDEN_DAGGERS,DEFEND_SILENT+,BLADE_DANCE,PREPARED+,ENVENOM",
            "ACCURACY,PREPARED+,ESCAPE_PLAN,HIDDEN_DAGGERS,SNEAKY+"];
        var party=Enumerable.Range(0,4).Select(i=>Player.CreateForNewRun<Deprived>(UnlockState.all,(ulong)(71+i))).ToArray();
        var c=new CombatState(encounter:ModelDb.Encounter<MegaCrit.Sts2.Core.Models.Encounters.TestSubjectBoss>().ToMutable(),runState:RunState.CreateForTest(party,ascensionLevel:10,seed:"WMQZBCD4582E-QUALITY"));
        for(var i=0;i<4;i++)
        {
            var p=party[i];p.ResetCombatState();c.AddPlayer(p);p.MaxEnergy=3;
            p.PlayerCombatState!.Phase=PlayerTurnPhase.Play;p.PlayerCombatState.GainEnergy(3);
            p.Creature.SetMaxHpInternal(110);p.Creature.SetCurrentHpInternal(new[]{85,76,105,105}[i]);
            var remaining=decks[i].Split(',').ToList();
            void Add(string id,bool held)
            {
                var card=c.CreateCard(ModelDb.AllCards.First(m=>m.Id.Entry==id.TrimEnd('+')),p);
                if(id.EndsWith('+'))card.UpgradeInternal();
                (held?p.PlayerCombatState.Hand:p.PlayerCombatState.DrawPile).AddInternal(card);
            }
            foreach(var id in hands[i].Split(',')) { Add(id,true);remaining.Remove(id); }
            foreach(var id in remaining)Add(id,false);
            p.AddPotionInternal(ModelDb.Potion<MegaCrit.Sts2.Core.Models.Potions.FirePotion>().ToMutable(),silent:true);
        }
        var e=c.CreateCreature(ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.TestSubject>().ToMutable(),CombatSide.Enemy,"0");
        c.AddCreature(e);e.Monster!.SetUpForCombat();e.SetMaxHpInternal(500);e.SetCurrentHpInternal(500);
        e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["BITE_MOVE"],true);
        ModelDb.Power<AdaptablePower>().ToMutable().ApplyInternal(e,1,true);
        ModelDb.Power<EnragePower>().ToMutable().ApplyInternal(e,3,true);
        var root=KernelSession.Capture(c);

        var outcome = KernelRollout.Run(root.Fork(), party, party, new(RolloutPolicyKind.Kill) {
            InvestmentLookahead = false, SequenceLookahead = true, SequenceHandLookahead = false,
            CaptureActionCheckpoints = true });
        Check(outcome.Terminal && outcome.Record.Verified, "late native reference must genuinely finish");
        var baseline = new TournamentResult(outcome.Actions[0], outcome.Record, [outcome.Record], 1, 0, "native-late-reference") {
            Script = outcome.Actions, ScriptAliveStates = outcome.AliveStates, ScriptEnemyStates = outcome.EnemyStates,
            ScriptCheckpoints = outcome.Checkpoints };
        var run = new KernelPotionContinuationRun(root, party, party, baseline, 3000,
            new(RolloutPolicyKind.Kill) { InvestmentLookahead = false, SequenceLookahead = true, SequenceHandLookahead = false });
        while (!run.Advance(TimeSpan.FromMilliseconds(5))) { }
        Console.WriteLine($"PROBE potion-round future-menu completed={run.Completed} repairs={run.RepairedActions} menus={run.MenuRepairs} unplayable={run.UnplayableRepairs} resumes={run.ResumedScripts} formal={run.FormalActions} ms={run.ComputeMs:F1} {run.RepairDiagnostics}");
        Check(run.Completed > 0 && run.MenuRepairs > 0 && run.ResumedScripts >= 2,
            "the actual later Prepared menu must be freshly compared through a real round then strictly rejoin the verified suffix");
        Check(run.UnplayableRepairs > 0, "the actual future Concoct absent from hand must fund a fresh native current-round repair");
        Check(run.RepairDiagnostics.Contains("CONCOCT:unplayable targetGone=False menuChanged=False cleanUnplayable=True"),
            "an absent-hand Concoct must remain a separate native unplayable reason, without claiming a missing enemy target");
        VerifyEndings(root, party, run);
        Console.WriteLine("PASS potion-round future-menu");
    }

    private static void SingleAndCombination()
    {
        var player = Player.CreateForNewRun<Deprived>(UnlockState.all, 62500);
        Player[] party = [player];
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "POTION-JOB-ORDER")) { RoundNumber = 1 };
        player.ResetCombatState(); combat.AddPlayer(player); player.MaxEnergy = 2;
        player.PlayerCombatState!.Phase = PlayerTurnPhase.Play; player.PlayerCombatState.GainEnergy(2);
        player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(67);
        for (var i = 0; i < 2; i++) player.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(player));
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(800); enemy.SetCurrentHpInternal(800);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(enemy, 9, true);
        var bottles = Enumerable.Range(0, 2).Select(_ => ModelDb.Potion<FirePotion>().ToMutable()).ToArray();
        foreach (var bottle in bottles) player.AddPotionInternal(bottle, silent: true);
        var root = KernelSession.Capture(combat); var before = root.PartyStateText();
        var outcome = KernelRollout.Run(root.Fork(), party, party, new(RolloutPolicyKind.Kill) {
            InvestmentLookahead = false, SequenceLookahead = true, SequenceHandLookahead = false, CaptureActionCheckpoints = true });
        Check(outcome.Terminal && outcome.Record.Verified && !outcome.Victory, "job-order control must begin from a genuine losing native reference");
        var baseline = new TournamentResult(outcome.Actions[0], outcome.Record, [outcome.Record], 1, 0, "native-job-reference") {
            Script = outcome.Actions, ScriptAliveStates = outcome.AliveStates, ScriptEnemyStates = outcome.EnemyStates,
            ScriptCheckpoints = outcome.Checkpoints };
        var run = new KernelPotionContinuationRun(root, party, party, baseline, 2000,
            new(RolloutPolicyKind.Kill) { InvestmentLookahead = false, SequenceLookahead = true, SequenceHandLookahead = false });
        while (!run.Advance(TimeSpan.FromMilliseconds(2))) { }
        var used = run.CompletedResults.Select(r => r.Script!.Count(a => a.Potion is not null)).ToArray();
        Console.WriteLine($"PROBE potion-round single-and-combination completed={run.Completed} used=[{string.Join(',', used)}] ms={run.ComputeMs:F1}");
        Check(run.Done && run.Completed == 3 && used.SequenceEqual(new[] { 1, 1, 2 }),
            "both actual single-bottle endings must precede the retained complete emergency combination");
        Check(ReferenceEquals(run.CompletedResults[0].FirstAction!.Potion, bottles[0])
            && ReferenceEquals(run.CompletedResults[1].FirstAction!.Potion, bottles[1]),
            "native single jobs must retain their actual distinct original potion identities");
        VerifyEndings(root, party, run);
        Check(root.PartyStateText() == before && player.Potions.Count() == 2, "all native potion jobs must preserve the same captured root");
        Console.WriteLine("PASS potion-round single-and-combination");
    }

    private static void VerifyEndings(KernelSession root, Player[] party, KernelPotionContinuationRun run)
    {
        Check(run.CompletedResults.Count == run.CompletedNativeStates.Count,
            "every completed ending must retain its actual native state witness");
        for (var ending = 0; ending < run.CompletedResults.Count; ending++)
        {
            var result = run.CompletedResults[ending]; var native = run.CompletedNativeStates[ending];
            Check(result.Script!.Count == result.ScriptAliveStates!.Count && result.Script.Count == result.ScriptEnemyStates!.Count,
                "repaired and replayed actions must preserve complete preaction evidence");
            var replay = root.Fork();
            for (var i = 0; i < result.Script.Count; i++)
            {
                Check(party.Where(p => replay.Hp(p.Creature) > 0).Select(p => p.NetId).Order().SequenceEqual(result.ScriptAliveStates[i])
                    && replay.Enemies.Count == result.ScriptEnemyStates[i].Length
                    && replay.Enemies.Zip(result.ScriptEnemyStates[i]).All(pair => KernelSession.SameCreature(pair.First, pair.Second)),
                    "each reconstructed action must retain its actual native alive and enemy boundary evidence");
                Check(replay.ReplayPlanned(result.Script[i], 80, out _, out var resolved)
                    && JsonSerializer.Serialize(resolved.Choices) == JsonSerializer.Serialize(result.Script[i].Choices)
                    && (resolved.ChoiceOrdinals ?? []).SequenceEqual(result.Script[i].ChoiceOrdinals ?? []),
                    "every reconstructed repaired prefix and resolved choice vector must strictly replay");
            }
            Check(replay.HasTerminal && replay.HasWon == result.Record!.Victory,
                "only a real native terminal route may be published");
            Check(State(replay) == State(native),
                "complete repaired script must reproduce its actual native ending state, all nine RNG streams and round counters");
        }
    }

    private static string State(KernelSession state)
    {
        string Cards(IReadOnlyList<CardModel> cards) => string.Join(',', cards.Select(state.CardStateKey));
        var rng = state.Simulator.Rng;
        return state.PartyStateText() + "|" + string.Join(";", state.ChoiceParty.Select(p =>
            $"{p.NetId}|H:{Cards(state.Hand(p))}|D:{Cards(state.DrawPile(p))}|C:{Cards(state.Discard(p))}|X:{Cards(state.Exhaust(p))}|ready:{state.IsReady(p)}"))
            + "|checkpoint:" + JsonSerializer.Serialize(state.ActionCheckpoint())
            + $"|phase:{state.EnemyPhaseCompleted}|horizon:{state.HorizonClosed}|round:{state.RoundsAdvanced}"
            + "|rng:" + JsonSerializer.Serialize(new[] { rng.ShuffleState, rng.CombatCardGenerationState,
                rng.CombatCardSelectionState, rng.CombatTargetsState, rng.MonsterAiState, rng.NicheState,
                rng.CombatPotionGenerationState, rng.CombatEnergyCostsState, rng.CombatOrbGenerationState });
    }
    private static void Check(bool ok, string why) { if (!ok) throw new Exception("Potion round repair: " + why); }
}
