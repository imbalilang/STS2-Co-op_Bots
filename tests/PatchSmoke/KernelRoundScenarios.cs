using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Test-isolation R2 2026-10-08: successful private build -> named RED1 -> exact restore -> build0/GREEN0.
// Raw results: work/latest-run-repair-20261008/choice/fixture-isolation/r2-results.json.
// duplicate-wire-physical-occurrence: System.Exception: P1-1: Equal-looking cards must preserve their physical occurrence.
internal static class KernelRoundScenarios
{
    internal static void Run()
    {
        TestEnvironment.Ensure();
        var presentation = new Harmony("coopbots.kernel-round.presentation");
        presentation.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(KernelRoundScenarios), nameof(Format)));
        try { RunCore(); }
        finally { presentation.UnpatchAll(presentation.Id); }
    }

    private static bool Format(ref string __result) { __result = ""; return false; }

    private static void RunCore()
    {
        if (Environment.GetCommandLineArgs().Contains("--extra-round-only"))
        {
            ExtraTurnBoundaries();
            return;
        }
        void Check(bool value, string reason) { if (!value) throw new Exception("P1-1: " + reason); }
        var players = Enumerable.Range(1, 3).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)(50 + i))).ToArray();
        var combat = new CombatState(runState: RunState.CreateForTest(players, seed: "P1-ROUND"));
        foreach (var p in players)
        {
            p.ResetCombatState(); combat.AddPlayer(p); p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
        }
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(200); enemy.SetCurrentHpInternal(200);
        enemy.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(1)) { FollowUpStateId = "ATTACK" }, true);
        T Hand<T>(Player p) where T : CardModel { var c = combat.CreateCard<T>(p); p.PlayerCombatState!.Hand.AddInternal(c); return c; }
        var headbutt = Hand<Headbutt>(players[0]);
        var strike = combat.CreateCard<StrikeIronclad>(players[0]);
        var defend = combat.CreateCard<DefendIronclad>(players[0]);
        players[0].PlayerCombatState!.DiscardPile.AddInternal(strike); players[0].PlayerCombatState!.DiscardPile.AddInternal(defend);
        var root = KernelSession.Capture(combat);
        var unresolved = root.Fork();
        Check(!unresolved.Play(headbutt, enemy, out _), "Direct replay without a choice must report its unresolved transaction.");
        var rejected = false;
        try { unresolved.Fork(); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Never fork a partially resolved choice transaction.");
        var choices = root.CardBranches(headbutt, enemy).ToArray();
        Check(choices.Length >= 2 && choices.All(c => c.Boundary.Length == 0),
            "Headbutt must enumerate settled retrieval alternatives: " + string.Join(',', choices.Select(c => c.Boundary)));
        Check(choices.Select(c => c.Choices.Single().Cards.Single().Id).Distinct().Count() == 2, "Headbutt alternatives lost identity.");
        // Regression: CardBranches seeds its queue with the EMPTY vector, so the branch
        // that used the default choice index carries [] — not null. Replay used to treat
        // [] as "no vector", which disabled the choice cursor, made ManualPlay return
        // false and failed the step with `pending-choice`. One SURVIVOR in a path was
        // therefore enough to empty ActionStates for an entire 104-action route.
        Check(choices[0].ChoiceOrdinals is { Count: 0 },
            "the default-index branch must carry an empty (not null) ordinals vector, got "
            + (choices[0].ChoiceOrdinals is null ? "null" : choices[0].ChoiceOrdinals.Count.ToString()));
        var replayAction = new KernelTeamSearch.Action(players[0], headbutt, enemy,
            Choices: choices[0].Choices, ChoiceOrdinals: choices[0].ChoiceOrdinals);
        var replay = KernelSession.Capture(combat).Fork();
        Check(replay.ReplayPlanned(replayAction, 2, out var replayReason),
            $"An EMPTY choice vector must still drive the cursor, got {replayReason}.");
        foreach (var branch in choices)
        {
            var choice = branch.Choices.Single();
            var serialized = System.Text.Json.JsonSerializer.Serialize(choice);
            var roundTrip = System.Text.Json.JsonSerializer.Deserialize<KernelChoice>(serialized)!;
            var selected = roundTrip.Resolve(players[0].NetId, [strike, defend], 1, 1);
            Check(selected?.Single().Id.Entry == choice.Cards.Single().Id, "Wire choice must resolve to the same live card.");
            Check(roundTrip.Resolve(players[1].NetId, [strike, defend], 1, 1) is null, "Reject another player's choice.");
        }
        Check(root.Hand(players[0]).Contains(headbutt) && enemy.CurrentHp == 200, "Choice replay mutated root/live state.");
        var strikeChoice = choices.Select(c => c.Choices.Single()).First(c => c.Cards.Single().Id == strike.Id.Entry);
        var secondStrike = combat.CreateCard<StrikeIronclad>(players[0]);
        // Capture a real duplicate-card menu rather than replacing only the
        // selected token while retaining the original Strike/Defend menu.
        players[0].PlayerCombatState.DiscardPile.AddInternal(secondStrike);
        var duplicateMenu = KernelSession.Capture(combat).CardBranches(headbutt, enemy)
            .Select(branch => branch.Choices.Single())
            .First(choice => choice.Cards.Single().Id == strike.Id.Entry);
        // Retrieval search may coalesce equal-value representatives. This wire
        // identity control explicitly selects the other legal physical instance
        // while retaining the complete menu captured from the real transaction.
        var duplicate = duplicateMenu with { Cards = [duplicateMenu.Cards[0] with { Occurrence = 1 }] };
        Check(ReferenceEquals(duplicate.Resolve(players[0].NetId, [strike, defend, secondStrike], 1, 1)?.Single(), secondStrike),
            "Equal-looking cards must preserve their physical occurrence.");
        players[0].PlayerCombatState.DiscardPile.RemoveInternal(secondStrike);
        var altered = strikeChoice with { Cards = [strikeChoice.Cards[0] with { StateKey = "changed" }] };
        Check(altered.Resolve(players[0].NetId, [strike, defend], 1, 1) is null, "Reject changed semantic state even when card ID still matches.");
        Console.WriteLine("PASS: P1-1 Headbutt choice branching, wire roundtrip and owner validation.");

        foreach (var p in players) Hand<DefendIronclad>(p);
        root = KernelSession.Capture(combat);
        var ended = root.Fork();
        Check(ended.EndTurn(players[0], out var reason), "First ready: " + reason);
        Check(ended.IsReady(players[0]) && !ended.CanPlay(headbutt) && !ended.EnemyPhaseCompleted
            && ended.Hand(players[0]).Count == root.Hand(players[0]).Count, "Individual readiness must not flush any hand.");
        Check(ended.EndTurn(players[1], out reason), "Second ready: " + reason);
        Check(!ended.EnemyPhaseCompleted && ended.Hp(players[0].Creature) == 80, "Never implicitly finish the remaining player.");
        var complete = ended.EndTurn(players[2], out reason);
        Check(complete, "Full round: " + reason + " " + ended.LastRoundFailure);
        Check(ended.EnemyPhaseCompleted, "Complete enemy phase marker missing.");
        Check(players.All(p => ended.Hp(p.Creature) == 79), "Monster must damage every living player once: " + string.Join(',', players.Select(p => ended.Hp(p.Creature))));
        Check(players.All(p => ended.Hand(p).Count == 0), "Shared boundary must flush each hand once.");
        Check(!ended.EndTurn(players[2], out _) && players.All(p => ended.Hp(p.Creature) == 79), "Repeated end must not run enemies twice.");
        Check(root.Hp(players[0].Creature) == 80 && players.All(p => p.Creature.CurrentHp == 80), "Round replay leaked to root/live.");
        Console.WriteLine("PASS: P1-1 per-player readiness, shared hand flush, full-party monster hit and no repeated enemy phase.");

        players[0].Creature.SetCurrentHpInternal(1);
        ended = KernelSession.Capture(combat);
        foreach (var p in players) Check(ended.EndTurn(p, out reason), "Death round: " + reason);
        Check(ended.Hp(players[0].Creature) == 0 && ended.Hp(players[1].Creature) == 79 && ended.Hp(players[2].Creature) == 79,
            "One player's death must not stop damage to remaining teammates.");
        Console.WriteLine("PASS: P1-1 enemy attacks continue after the first teammate dies.");

        (CombatState Combat, Player[] Party, MegaCrit.Sts2.Core.Entities.Creatures.Creature Enemy) Encounter<T>(string moveId) where T : MonsterModel
        {
            var party = Enumerable.Range(1, 3).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)(60 + i))).ToArray();
            var state = new CombatState(encounter: typeof(T) == typeof(LivingFog)
                ? ModelDb.Encounter<MegaCrit.Sts2.Core.Models.Encounters.LivingFogNormal>().ToMutable() : null,
                runState: RunState.CreateForTest(party, seed: "P1-SPECIAL"));
            foreach (var p in party)
            {
                p.ResetCombatState(); state.AddPlayer(p); p.Creature.SetMaxHpInternal(100); p.Creature.SetCurrentHpInternal(100);
                p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
            }
            var foe = state.CreateCreature(ModelDb.Monster<T>().ToMutable(), CombatSide.Enemy,
                typeof(T) == typeof(LivingFog) ? "livingFog" : "0");
            state.AddCreature(foe); foe.Monster!.SetUpForCombat();
            foe.Monster.SetMoveImmediate((MoveState)foe.Monster.MoveStateMachine!.States[moveId], true);
            return (state, party, foe);
        }
        KernelSession Finish(CombatState state, Player[] party)
        {
            var branch = KernelSession.Capture(state);
            foreach (var p in party)
            {
                var ok = branch.EndTurn(p, out var failure);
                Check(ok, "Special enemy phase: " + failure + " " + branch.LastRoundFailure);
            }
            return branch;
        }
        var rage = Encounter<SludgeSpinner>("RAGE_MOVE");
        var rageState = Finish(rage.Combat, rage.Party);
        Check(rageState.Power<StrengthPower>(rage.Enemy) == 3, "Three targets must not triple SludgeSpinner growth.");
        Check(rage.Party.All(p => rageState.Hp(p.Creature) == 94), "SludgeSpinner attack must precede its Strength gain.");
        var settledMetrics = new KernelCombatEvaluation(rage.Combat, rage.Party, null, futureRounds: 0).Evaluate(rageState);
        Check(settledMetrics.HpLoss == 18, "Completed enemy phase must not charge incoming intent twice: " + settledMetrics.HpLoss);
        var spray = Encounter<SludgeSpinner>("OIL_SPRAY_MOVE");
        var sprayState = Finish(spray.Combat, spray.Party);
        Check(spray.Party.All(p => sprayState.Power<WeakPower>(p.Creature) == 1), "Oil spray must weaken all three targets.");
        Check(spray.Party.All(p => p.Creature.Powers.Count == 0 && p.Creature.CurrentHp == 100), "Party debuffs leaked to live combat.");
        var fog = Encounter<LivingFog>("BLOAT_MOVE");
        var fogState = Finish(fog.Combat, fog.Party);
        var bombs = fogState.Enemies.Where(e => e.Monster is GasBomb).ToArray();
        Check(bombs.Length == 1 && fog.Combat.Enemies.Count == 1, "Fog summons duplicated or leaked to the live encounter: " + bombs.Length);
        Console.WriteLine("PASS: P1-1 real monster growth once, party-wide Weak, branch-local summons.");

        var worm = Encounter<TheInsatiable>("LIQUIFY_GROUND_MOVE");
        var wormState = Finish(worm.Combat, worm.Party);
        Check(worm.Party.All(p => wormState.SandpitTurnsRemaining(p.Creature) == 4), "Sandpit target-specific instances must exist for every player: "
            + string.Join(',', worm.Party.Select(p => wormState.SandpitTurnsRemaining(p.Creature)?.ToString() ?? "missing")));
        Console.WriteLine("PASS: P1-1 native sandworm phase applies each player's Sandpit/Escape transaction.");

        // End-turn candidates must not replace a discovered lethal with a worse
        // complete nonlethal round simply because lethal needs no explicit end.
        var lethal = Encounter<SludgeSpinner>("RAGE_MOVE");
        lethal.Enemy.SetCurrentHpInternal(6);
        var lethalStrike = lethal.Combat.CreateCard<StrikeIronclad>(lethal.Party[0]);
        lethal.Party[0].PlayerCombatState!.Hand.AddInternal(lethalStrike);
        var searchRoot = KernelSession.Capture(lethal.Combat);
        using var search = new KernelTeamSearch(searchRoot, lethal.Party,
            s => (s.HasWon ? 10000 : 0) - s.Hp(lethal.Enemy) * 10 + lethal.Party.Sum(p => s.Hp(p.Creature)),
            new(Depth: 6, Width: 12, MaxNodes: 200, IncludeEndTurns: true));
        while (!search.Advance(TimeSpan.FromMilliseconds(2), () => true)) { }
        Check(search.CompletedResult!.Actions.FirstOrDefault()?.Card == lethalStrike && search.CompletedState!.HasWon,
            "A complete nonlethal round must not displace an immediate lethal.");
        Console.WriteLine("PASS: P1-1 completed-round search preserves immediate lethal over premature ending.");

        // The simulation must pay the SAME gold the live game pays. Live gold goes through
        // PlayerCmd.GainGold, which BotGoldCheatPatch triples for a Cheated bot; the
        // simulator pays through its own ledger and never sees that command, so it reads
        // this registered rule instead. A Cheated seat that prices the untripled amount
        // drifts on `field=gold` the moment a gold-gain card (e.g. HandOfGreed) resolves.
        var savedMultiplier = CoopBots.Kernel.Vendor.SimulatedGoldGain.MultiplierResolver;
        try
        {
            CoopBots.Kernel.Vendor.SimulatedGoldGain.MultiplierResolver = static netId => netId == 42UL ? 3 : 1;
            Check(CoopBots.Kernel.Vendor.SimulatedGoldGain.MultiplierFor(42UL) == 3
                && CoopBots.Kernel.Vendor.SimulatedGoldGain.MultiplierFor(43UL) == 1,
                "the registered gold multiplier must be resolved per seat.");
        }
        finally { CoopBots.Kernel.Vendor.SimulatedGoldGain.MultiplierResolver = savedMultiplier; }
        Check(CoopBots.Kernel.Vendor.SimulatedGoldGain.MultiplierFor(42UL) == 1,
            "the gold multiplier must fall back to 1x once the registration is removed.");
        Console.WriteLine("PASS: the simulator's gold payout follows the registered per-seat multiplier.");

        // A plan names a card by IDENTITY, not by instance. That only works if the identity
        // is stable across copies — two Shivs are distinct CardModel objects but must share
        // one state key. Instance matching (Hand.Cards.Contains / PredictedCard.References)
        // is reference equality, so it matched nothing for generated cards and refused every
        // route through one (observed: `(SHIV): unplayable` on four consecutive fights).
        var shivA = lethal.Combat.CreateCard<Shiv>(lethal.Party[0]);
        var shivB = lethal.Combat.CreateCard<Shiv>(lethal.Party[0]);
        var keyA = CoopBots.Kernel.Vendor.CardChoiceSupport.ChoiceCardKey(shivA);
        var keyB = CoopBots.Kernel.Vendor.CardChoiceSupport.ChoiceCardKey(shivB);
        Check(!ReferenceEquals(shivA, shivB) && keyA == keyB,
            "two copies of a generated card must be distinct objects sharing one state key.");
        Console.WriteLine("PASS: a card's plan identity is stable across instances, not per object.");

        // Same board and evaluator as above; only StopOnFirstTerminal differs. The
        // extension ladder sets it so a found ending is USED instead of being ranked
        // against endings the search has not found yet — without it every extension ran
        // to `time-budget` and burned its whole minute after already holding a route.
        var earlyRoot = KernelSession.Capture(lethal.Combat);
        using var early = new KernelTeamSearch(earlyRoot, lethal.Party,
            s => (s.HasWon ? 10000 : 0) - s.Hp(lethal.Enemy) * 10 + lethal.Party.Sum(p => s.Hp(p.Creature)),
            new(Depth: 6, Width: 12, MaxNodes: 200, IncludeEndTurns: true, StopOnFirstTerminal: true));
        while (!early.Advance(TimeSpan.FromMilliseconds(2), () => true)) { }
        Check(early.HasRoute && early.CompletedResult!.StopReason == "terminal-found",
            $"StopOnFirstTerminal must end the search on the first line that ends the fight, got "
            + $"stop={early.CompletedResult!.StopReason} route={early.HasRoute} "
            + $"nodes={early.CompletedResult.ExpandedNodes} (ranked run used {search.CompletedResult!.ExpandedNodes}).");
        Console.WriteLine($"PASS: an extension attempt stops on the first ending "
            + $"(nodes={early.CompletedResult.ExpandedNodes} vs {search.CompletedResult.ExpandedNodes} ranked).");

        void ChoiceCard<T>(Action<CombatState, Player> setup, int minBranches) where T : CardModel
        {
            var e = Encounter<SludgeSpinner>("RAGE_MOVE");
            var p = e.Party[0];
            var card = e.Combat.CreateCard<T>(p); p.PlayerCombatState!.Hand.AddInternal(card);
            setup(e.Combat, p);
            // Derive the target from the card's own target type rather than assuming the
            // caster's creature. A Self card is played with NO target (the live game
            // logs a blank `targetid:` for Defend/Inflame/Hologram alike), so passing
            // p.Creature made the engine reject every such play as "invalid-target".
            var session = KernelSession.Capture(e.Combat);
            var alternatives = session.CardBranches(card, session.Targets(card).FirstOrDefault()).ToArray();
            Check(alternatives.Length >= minBranches && alternatives.All(a => a.Boundary == "" && a.Choices.Count > 0),
                typeof(T).Name + " choices: " + string.Join(',', alternatives.Select(a => a.Boundary)));
            // Every card that resolves through a choice vector must also REPLAY through it.
            // This is the general form of the SURVIVOR bug: an empty vector meant "use the
            // default index" during the search but "no choice at all" during replay, which
            // failed the step with `pending-choice` and emptied the sentinel. Checking it
            // for every ChoiceCard case keeps Discard/Exhaust/Upgrade/Transform/Duplicate
            // covered, not just the one card that happened to hit the empty vector first.
            var firstBranch = alternatives[0];
            var replayTarget = session.Targets(card).FirstOrDefault();
            var replayAction = new KernelTeamSearch.Action(p, card, replayTarget,
                Choices: firstBranch.Choices, ChoiceOrdinals: firstBranch.ChoiceOrdinals);
            var replay = session.Fork();
            Check(replay.ReplayPlanned(replayAction, 2, out var replayReason),
                $"{typeof(T).Name} must replay through its own choice vector, got {replayReason}.");
        }
        ChoiceCard<Hologram>((s, p) =>
        {
            p.PlayerCombatState!.DiscardPile.AddInternal(s.CreateCard<StrikeIronclad>(p));
            p.PlayerCombatState.DiscardPile.AddInternal(s.CreateCard<DefendIronclad>(p));
        }, 2);
        ChoiceCard<Armaments>((s, p) =>
        {
            p.PlayerCombatState!.Hand.AddInternal(s.CreateCard<StrikeIronclad>(p));
            p.PlayerCombatState.Hand.AddInternal(s.CreateCard<DefendIronclad>(p));
        }, 2);
        ChoiceCard<Prepared>((s, p) =>
        {
            p.PlayerCombatState!.DrawPile.AddInternal(s.CreateCard<StrikeIronclad>(p));
            p.PlayerCombatState.Hand.AddInternal(s.CreateCard<DefendIronclad>(p));
        }, 2);
        var message = new BotChoicePlanMessage { Sequence = 3, Action = 42, Bot = 51, Card = "HEADBUTT",
            Payload = System.Text.Json.JsonSerializer.Serialize(choices[0].Choices) };
        var writer = new MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketWriter(); message.Serialize(writer);
        var reader = new MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketReader(); reader.Reset(writer.Buffer);
        var decoded = new BotChoicePlanMessage(); decoded.Deserialize(reader);
        Check(decoded.Sequence == 3 && decoded.Action == 42 && decoded.Bot == 51 && decoded.Card == "HEADBUTT"
            && decoded.Payload == message.Payload && !decoded.Ack && !decoded.Clear, "Network plan packet roundtrip.");
        Console.WriteLine("PASS: P1-1 retrieval/upgrade/draw-discard choice branches and network packet roundtrip.");

        var ethereal = Encounter<SludgeSpinner>("RAGE_MOVE");
        for (var i = 0; i < 2; i++)
        {
            var p = ethereal.Party[i];
            ((DarkEmbracePower)ModelDb.Power<DarkEmbracePower>().ToMutable()).ApplyInternal(p.Creature, 1, true);
            for (var j = 0; j <= i; j++) p.PlayerCombatState!.Hand.AddInternal(ethereal.Combat.CreateCard<AscendersBane>(p));
            for (var j = 0; j < 5; j++) p.PlayerCombatState!.DrawPile.AddInternal(ethereal.Combat.CreateCard<StrikeIronclad>(p));
        }
        var etherealState = Finish(ethereal.Combat, ethereal.Party);
        Check(etherealState.Hand(ethereal.Party[0]).Count == 1 && etherealState.Hand(ethereal.Party[1]).Count == 2,
            "Ethereal-triggered draw must use each owner's count, not the team total: "
                + string.Join(',', ethereal.Party.Select(p => etherealState.Hand(p).Count)));
        Console.WriteLine("PASS: P1-1 owner-specific ethereal counts and end-turn Dark Embrace draw.");

        ExtraTurnBoundaries();
    }

    private static void ExtraTurnBoundaries()
    {
        // R2 2026-10-07: five production fault cycles RED1 -> exact source-byte
        // restoration -> GREEN0; --kernel-rounds-only --extra-round-only with
        // EnableKernelTests=true. Commands and hashes: extra-round-r2-results.json.
        // Raw System.Exception messages:
        // P1-1 extra-round: supported extra turn must settle at the finite default horizon
        // P1-1 extra-round: extra horizon must skip native Rage damage and Strength growth while consuming its source
        // P1-1 extra-round: extra-turn settlement must preserve the captured root and live source
        // P1-1 extra-round: Paels extra horizon must consume the native once-per-combat source without running Rage
        // P1-1 extra-round: unresolved extra preparation must fail before enemy execution and stay nonforkable
        // The max2 participant check also retains the native subset property
        // already guarded by LatestProjectionRepairScenarios and its roster R2.
        void Check(bool value, string reason) { if (!value) throw new Exception("P1-1 extra-round: " + reason); }
        var serial = 0;
        (CombatState Combat, Player[] Party, MegaCrit.Sts2.Core.Entities.Creatures.Creature Enemy) Board()
        {
            var party = Enumerable.Range(0, 3).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all,
                (ulong)(210 + serial * 3 + i))).ToArray();
            serial++;
            var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "EXTRA-ROUND-" + serial));
            foreach (var p in party)
            {
                p.ResetCombatState(); combat.AddPlayer(p);
                p.Creature.SetMaxHpInternal(100); p.Creature.SetCurrentHpInternal(100);
                p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
            }
            var enemy = combat.CreateCreature(ModelDb.Monster<SludgeSpinner>().ToMutable(), CombatSide.Enemy, "0");
            combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat();
            enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["RAGE_MOVE"], true);
            return (combat, party, enemy);
        }

        var extra = Board();
        var ambergris = (AmbergrisPower)ModelDb.Power<AmbergrisPower>().ToMutable();
        ambergris.ApplyInternal(extra.Party[0].Creature, 1, true);
        Check(ambergris.ShouldTakeExtraTurn(extra.Party[0]) && !ambergris.ShouldTakeExtraTurn(extra.Party[1]),
            "native Ambergris must grant its owner alone an extra turn");
        var root = KernelSession.Capture(extra.Combat);
        var rootText = root.PartyStateText();
        var extraState = root.Fork();
        Check(extraState.EndTurn(extra.Party[0], out _) && extraState.EndTurn(extra.Party[1], out _),
            "extra-turn readiness remains per actor");
        Check(extraState.EndTurn(extra.Party[2], out var reason) && reason.Length == 0
            && extraState.EnemyPhaseCompleted && extraState.HorizonClosed && extraState.RoundsAdvanced == 0,
            "supported extra turn must settle at the finite default horizon");
        Check(extra.Party.All(p => extraState.Hp(p.Creature) == 100)
            && extraState.Power<StrengthPower>(extra.Enemy) == 0
            && extraState.Power<AmbergrisPower>(extra.Party[0].Creature) == 0,
            "extra horizon must skip native Rage damage and Strength growth while consuming its source");
        Check(!extraState.EndTurn(extra.Party[0], out _) && extra.Party.All(p => extraState.Hp(p.Creature) == 100)
            && extraState.Power<StrengthPower>(extra.Enemy) == 0,
            "closed extra horizon must not repeat settlement");
        Check(root.PartyStateText() == rootText && ambergris.Amount == 1
            && extra.Party.All(p => p.Creature.CurrentHp == 100),
            "extra-turn settlement must preserve the captured root and live source");

        var resumed = root.Fork();
        foreach (var p in extra.Party) Check(resumed.EndTurn(p, 2, out reason), "supported extra continuation failed: " + reason);
        Check(resumed.RoundsAdvanced == 1 && !resumed.HorizonClosed && !resumed.EnemyPhaseCompleted
            && resumed.CanAct(extra.Party[0]) && extra.Party.Skip(1).All(resumed.IsReady)
            && resumed.StateText(extra.Party[0]).StartsWith("turn=2;", StringComparison.Ordinal)
            && extra.Party.Skip(1).All(p => resumed.StateText(p).StartsWith("turn=1;", StringComparison.Ordinal)),
            "one extra continuation must advance and activate only its actual participant");

        var paels = Board();
        var eye = (PaelsEye)ModelDb.Relic<PaelsEye>().ToMutable(); paels.Party[0].AddRelicInternal(eye);
        Check(eye.ShouldTakeExtraTurn(paels.Party[0]) && !eye.ShouldTakeExtraTurn(paels.Party[1]),
            "native unused Paels Eye must grant its non-playing owner alone an extra turn");
        var paelsRoot = KernelSession.Capture(paels.Combat);
        var paelsText = paelsRoot.PartyStateText();
        var paelsState = paelsRoot.Fork();
        foreach (var p in paels.Party) Check(paelsState.EndTurn(p, out reason), "Paels extra horizon failed: " + reason);
        Check(paelsState.HorizonClosed && paels.Party.All(p => paelsState.Hp(p.Creature) == 100)
            && paelsState.Power<StrengthPower>(paels.Enemy) == 0
            && paelsState.StateText(paels.Party[0]).Contains("PAELS_EYE/1/", StringComparison.Ordinal),
            "Paels extra horizon must consume the native once-per-combat source without running Rage");
        Check(paelsRoot.PartyStateText() == paelsText && !(bool)AccessTools.Field(typeof(PaelsEye), "_usedThisCombat").GetValue(eye)!
            && paels.Party.All(p => p.Creature.CurrentHp == 100),
            "Paels extra horizon must preserve captured and live relic state");

        // A false preparer result remains an unresolved transaction. Native
        // Ambergris is supported; it is not an example of an unknown source.
        // Paels preparation can fail when exhaust hooks leave a pending choice.
        // Inject only that contract result, not an invented vanilla extra power.
        var unknown = Board();
        var unknownRoot = KernelSession.Capture(unknown.Combat);
        var unknownText = unknownRoot.PartyStateText();
        var unknownState = unknownRoot.Fork();
        var harmony = new Harmony("coopbots.extra-round.unknown-preparation");
        var stateType = AccessTools.Property(typeof(KernelSession), "Combat").GetValue(unknownState)!.GetType();
        harmony.Patch(AccessTools.Method(stateType, "TryPrepareExtraPlayerTurn"),
            prefix: new HarmonyMethod(typeof(KernelRoundScenarios), nameof(UnresolvedExtraPreparation)));
        try
        {
            Check(unknownState.EndTurn(unknown.Party[0], out _) && unknownState.EndTurn(unknown.Party[1], out _),
                "unknown preparer must still wait for actual shared readiness");
            Check(!unknownState.EndTurn(unknown.Party[2], out reason)
                && reason.Contains("prepare-extra-player-turn", StringComparison.Ordinal)
                && !unknownState.CanFork && !unknownState.EnemyPhaseCompleted && !unknownState.HorizonClosed
                && unknown.Party.All(p => unknownState.Hp(p.Creature) == 100)
                && unknownState.Power<StrengthPower>(unknown.Enemy) == 0,
                "unresolved extra preparation must fail before enemy execution and stay nonforkable");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
        Check(unknownRoot.PartyStateText() == unknownText && unknown.Party.All(p => p.Creature.CurrentHp == 100),
            "unresolved extra preparation must preserve root and live state");
        Console.WriteLine("PASS: P1-1 native extra turns settle within the horizon; unresolved preparation stays isolated and explicit.");
    }

    private static bool UnresolvedExtraPreparation(ref bool __result, ref bool extraTurn, ref bool hasActiveEmotionChip)
    {
        extraTurn = false; hasActiveEmotionChip = false; __result = false;
        return false;
    }
}
