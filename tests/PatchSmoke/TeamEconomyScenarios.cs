using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using CoopBots;
using CoopBots.Building;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Second-phase focused regressions: the combat-start team snapshot and bounded
// team bonus, the bounded two-purchase shop comparison, and the shared removal
// plan / conservative reachable-risk used by the rest site. Every probe drives
// the production entry points against real game models.
internal static class TeamEconomyScenarios
{
    internal static void Run()
    {
        TestEnvironment.Ensure();
        void Check(bool ok, string message) { if (!ok) throw new Exception("Team economy regression: " + message); }

        // The snapshot lives on the Harmony patch applied to the engine's combat
        // setup; registration is checked without actually running a battle.
        var setup = AccessTools.Method(typeof(CombatManager), "SetUpCombat", new[] { typeof(CombatState) });
        Check(setup is not null, "CombatManager.SetUpCombat must be discoverable for the snapshot patch.");
        var patchInfo = Harmony.GetPatchInfo(setup);
        Check(patchInfo is not null && patchInfo.Prefixes.Any(patch =>
            patch.PatchMethod.DeclaringType == typeof(TeamBuildingContextPatch)),
            "The team-context snapshot prefix must be registered on SetUpCombat.");
        Console.WriteLine("PASS: the combat-start team snapshot is registered as a SetUpCombat prefix.");

        LifecyclePatchesRegistered(Check);
        SnapshotCaptureAndInvalidation(Check);
        RejoinLifecycleInvalidation(Check);
        NoSnapshotFallbackDeterministic(Check);
        StableRewardOrder(Check);
        SharedDebuffSaturation(Check);
        WeakCoverageIsSeparateFromVulnerable(Check);
        WeakOwnDeckDoesNotStackSupport(Check);
        UnknownAllySupportIsBounded(Check);
        HypotheticalOwnContext(Check);
        SaturationExcludesOnlyTheSameInstance(Check);
        TwoItemShopBeatsGreedySingle(Check);
        MawBankFreeFirstPair(Check);
        SequentialShopReplan(Check);
        SecondActionGates(Check);
        PairFeasibility(Check);
        SharedRemovalPlan(Check);
        NegativeRemovalIsNotInflated(Check);
        ReachableRiskIsOrderInvariant(Check);
        DecisionCostMilliseconds(Check);

        Console.WriteLine("PASS: team economy scenarios (snapshot safety, bounded team bonus, two-purchase shop, shared removal plan, reachable risk).");
    }

    private static void ClearDeck(Player player)
    {
        foreach (var card in player.Deck.Cards.ToArray()) player.Deck.RemoveInternal(card);
    }

    private static void Give(Player player, CardModel card) => player.Deck.AddInternal(card);

    private static void LifecyclePatchesRegistered(Action<bool, string> check)
    {
        void Registered(Type owner, string method, bool prefix, string label)
        {
            var target = AccessTools.Method(owner, method);
            check(target is not null, label + " must exist for the lifecycle poison patch.");
            var info = target is null ? null : Harmony.GetPatchInfo(target);
            static bool Owned(IEnumerable<Patch> patches) => patches.Any(patch =>
                patch.PatchMethod.DeclaringType is { } declaring
                && declaring.Name.StartsWith("TeamBuilding", StringComparison.Ordinal));
            // The client handler must invalidate BEFORE the native body invokes the
            // PlayerRejoined listeners, which can resume actions; the host and
            // saved-run paths invalidate after they flip the run state.
            var found = info is not null && (prefix
                ? Owned(info.Prefixes) && !Owned(info.Postfixes)
                : Owned(info.Postfixes) && !Owned(info.Prefixes));
            check(found, label + (prefix
                ? " must carry a TeamBuilding lifecycle prefix so the poison lands before PlayerRejoined listeners resume actions."
                : " must carry a TeamBuilding lifecycle postfix."));
        }
        // Verified native rejoin/load paths: the host sends the rejoin message,
        // the loading client initializes the saved run, and existing clients
        // handle PlayerRejoinedMessage.
        Registered(typeof(RunManager), "GetRejoinMessage", prefix: false, "RunManager.GetRejoinMessage");
        Registered(typeof(RunManager), "InitializeSavedRun", prefix: false, "RunManager.InitializeSavedRun");
        Registered(typeof(RunLobby), "HandlePlayerRejoinedMessage", prefix: true, "RunLobby.HandlePlayerRejoinedMessage");
        Console.WriteLine("PASS: the rejoin/load lifecycle poison patches are registered on the verified native paths (client prefix, host/load postfix).");
    }

    private static void SnapshotCaptureAndInvalidation(Action<bool, string> check)
    {
        var a = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
        var b = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 2, 1));
        a.MaxEnergy = 3; b.MaxEnergy = 3;
        var party = new[] { a, b };
        var run = RunState.CreateForTest(party, seed: "TEAM-SNAPSHOT");
        ClearDeck(a); ClearDeck(b);
        for (var i = 0; i < 5; i++) Give(a, run.CreateCard<StrikeIronclad>(a));
        for (var i = 0; i < 4; i++) Give(a, run.CreateCard<DefendIronclad>(a));
        Give(b, run.CreateCard<Bash>(b));
        TeamBuildingContext.Capture(run, party);

        var before = TeamBuildingContext.TryGet(run, a.NetId);
        check(before is not null && before.Attacks == 5 && before.Blocks == 4,
            $"capture must record the permanent deck facts (got {before?.Attacks}/{before?.Blocks}).");
        // Mutating the live deck afterwards must not change the captured summary.
        Give(a, run.CreateCard<StrikeIronclad>(a));
        var afterMutation = TeamBuildingContext.TryGet(run, a.NetId);
        check(afterMutation is not null && afterMutation.Attacks == before!.Attacks && afterMutation.Size == before.Size,
            "a captured snapshot must be immutable against later live-deck edits.");

        // A location change (a new act here) invalidates the snapshot rather than
        // silently serving the previous room's facts.
        run.CurrentActIndex = 1;
        check(TeamBuildingContext.TryGet(run, a.NetId) is null,
            "a snapshot must not be served from a different run location.");
        run.CurrentActIndex = 0;
        // Ordinary room versioning stays act/floor/room: a new room invalidates.
        TeamBuildingContext.Capture(run, party);
        run.PushRoom(new MerchantRoom());
        check(TeamBuildingContext.TryGet(run, a.NetId) is null,
            "a new room must invalidate the snapshot even at the same act and floor.");

        // A temporary combat pile is not the permanent deck: capture must ignore it.
        a.ResetCombatState();
        var combat = new CombatState(runState: run);
        combat.AddPlayer(a); combat.AddPlayer(b);
        var temporary = combat.CreateCard<StrikeIronclad>(a);
        a.PlayerCombatState!.Hand.AddInternal(temporary);
        TeamBuildingContext.Capture(combat);
        var permanentOnly = TeamBuildingContext.TryGet(run, a.NetId);
        check(permanentOnly is not null && permanentOnly.Size == a.Deck.Cards.Count,
            "the snapshot must be the permanent deck, not the temporary hand.");
        a.PlayerCombatState.Hand.RemoveInternal(temporary);
        TeamBuildingContext.Clear(run);
        Console.WriteLine("PASS: the snapshot is immutable, location-versioned, permanent-deck-only and clears on demand.");
    }

    // Two peers are separate run instances with separate context lifetimes. A
    // rejoin poisons the current act/floor on both, the saved combat's SetUpCombat
    // must not recapture at that floor, and a later act/floor resumes normally.
    private static void RejoinLifecycleInvalidation(Action<bool, string> check)
    {
        var hostA = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
        var hostB = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 2, 1));
        var clientA = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
        var clientB = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 2, 1));
        hostA.MaxEnergy = clientA.MaxEnergy = 3;
        hostB.MaxEnergy = clientB.MaxEnergy = 3;
        var hostRun = RunState.CreateForTest(new[] { hostA, hostB }, seed: "TEAM-REJOIN");
        var clientRun = RunState.CreateForTest(new[] { clientA, clientB }, seed: "TEAM-REJOIN");
        foreach (var player in new[] { hostA, hostB, clientA, clientB }) ClearDeck(player);
        for (var i = 0; i < 5; i++)
        {
            Give(hostA, hostRun.CreateCard<StrikeIronclad>(hostA));
            Give(clientA, clientRun.CreateCard<StrikeIronclad>(clientA));
        }
        Give(hostB, hostRun.CreateCard<Bash>(hostB));
        Give(clientB, clientRun.CreateCard<Bash>(clientB));

        // Both peers captured the same shared combat start and agree.
        TeamBuildingContext.Capture(hostRun, new[] { hostA, hostB });
        TeamBuildingContext.Capture(clientRun, new[] { clientA, clientB });
        check(TeamBuildingContext.TryGet(hostRun, hostA.NetId) is not null
            && TeamBuildingContext.TryGet(clientRun, clientA.NetId) is not null,
            "both peers must hold the shared combat-start snapshot before the rejoin.");
        var hostLift = hostRun.CreateCard<Lift>(hostA);
        var clientLift = clientRun.CreateCard<Lift>(clientA);
        check(Math.Abs(TeamCoordinator.TeamBonus(hostLift, hostA) - TeamCoordinator.TeamBonus(clientLift, clientA)) < 0.0001,
            "peers with matching decks must agree before the rejoin.");

        // Host sends the rejoin; the client loads the saved run; existing clients
        // handle PlayerRejoinedMessage. All poison their own act/floor.
        TeamBuildingContext.Invalidate(hostRun);
        TeamBuildingContext.Invalidate(clientRun);
        check(TeamBuildingContext.TryGet(hostRun, hostA.NetId) is null
            && TeamBuildingContext.TryGet(clientRun, clientA.NetId) is null,
            "a rejoin must drop the snapshot on every peer.");

        // The saved combat's own SetUpCombat runs at the poisoned floor and must
        // not recapture a deck the host never shared.
        TeamBuildingContext.Capture(hostRun, new[] { hostA, hostB });
        TeamBuildingContext.Capture(clientRun, new[] { clientA, clientB });
        check(TeamBuildingContext.TryGet(hostRun, hostA.NetId) is null
            && TeamBuildingContext.TryGet(clientRun, clientA.NetId) is null,
            "the poison must persist through a same-floor saved-combat SetUpCombat.");

        var hostAfter = TeamCoordinator.TeamBonus(hostLift, hostA);
        var clientAfter = TeamCoordinator.TeamBonus(clientLift, clientA);
        check(Math.Abs(hostAfter - clientAfter) < 0.0001 && hostAfter > 0,
            $"both peers must fall back to the same conservative bonus while poisoned ({hostAfter:F2} vs {clientAfter:F2}).");

        // The next act/floor is a real shared combat start again.
        hostRun.CurrentActIndex = 1;
        clientRun.CurrentActIndex = 1;
        TeamBuildingContext.Capture(hostRun, new[] { hostA, hostB });
        TeamBuildingContext.Capture(clientRun, new[] { clientA, clientB });
        check(TeamBuildingContext.TryGet(hostRun, hostA.NetId) is not null
            && TeamBuildingContext.TryGet(clientRun, clientA.NetId) is not null,
            "a later act/floor must resume the shared snapshot on both peers.");
        TeamBuildingContext.Clear(hostRun);
        TeamBuildingContext.Clear(clientRun);
        Console.WriteLine("PASS: rejoin/load poisons the current act/floor on every peer, blocks same-floor recapture, and resumes later.");
    }

    private static void NoSnapshotFallbackDeterministic(Action<bool, string> check)
    {
        var a = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
        var b = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 2, 1));
        a.MaxEnergy = 3; b.MaxEnergy = 3;
        var run = RunState.CreateForTest(new[] { a, b }, seed: "TEAM-NOSNAP");
        ClearDeck(a); ClearDeck(b);
        for (var i = 0; i < 5; i++) Give(a, run.CreateCard<StrikeIronclad>(a));
        Give(b, run.CreateCard<Bash>(b));
        TeamBuildingContext.Clear(run);

        var lift = run.CreateCard<Lift>(a);
        var first = TeamCoordinator.TeamBonus(lift, a);
        // Teammate edits cannot leak into a no-snapshot decision.
        for (var i = 0; i < 4; i++) Give(b, run.CreateCard<DefendIronclad>(b));
        var second = TeamCoordinator.TeamBonus(lift, a);
        check(first > 0 && Math.Abs(first - second) < 0.0001,
            $"without a snapshot the fallback must be deterministic and teammate-independent ({first:F2} vs {second:F2}).");
        Console.WriteLine($"PASS: a missing snapshot degrades to a deterministic own-deck/roster bonus ({first:F2}).");
    }

    private static void StableRewardOrder(Action<bool, string> check)
    {
        var a = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
        var b = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 2, 1));
        a.MaxEnergy = 3; b.MaxEnergy = 3;
        var run = RunState.CreateForTest(new[] { a, b }, seed: "TEAM-ORDER");
        ClearDeck(a); ClearDeck(b);
        for (var i = 0; i < 5; i++) Give(a, run.CreateCard<StrikeIronclad>(a));
        for (var i = 0; i < 3; i++) Give(b, run.CreateCard<StrikeIronclad>(b));
        Give(b, run.CreateCard<Bash>(b));
        var lift = run.CreateCard<Lift>(a);

        TeamBuildingContext.Capture(run, new[] { a, b });
        var first = TeamCoordinator.TeamBonus(lift, a);
        // Re-capture with the teammate's deck in a different order: the numeric
        // summary is order-independent, so the current offer cannot change.
        var bCards = b.Deck.Cards.ToList();
        ClearDeck(b);
        for (var i = bCards.Count - 1; i >= 0; i--) Give(b, bCards[i]);
        TeamBuildingContext.Capture(run, new[] { a, b });
        var second = TeamCoordinator.TeamBonus(lift, a);
        check(Math.Abs(first - second) < 0.0001,
            $"teammate deck order must not change the current offer ({first:F2} vs {second:F2}).");
        Console.WriteLine("PASS: a stable snapshot makes teammate deck order irrelevant to the current offer.");
    }

    private static void SharedDebuffSaturation(Action<bool, string> check)
    {
        var a = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
        var b = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 2, 1));
        a.MaxEnergy = 3; b.MaxEnergy = 3;
        var run = RunState.CreateForTest(new[] { a, b }, seed: "TEAM-DEBUFF");
        ClearDeck(a); ClearDeck(b);
        for (var i = 0; i < 5; i++) Give(a, run.CreateCard<StrikeIronclad>(a));
        var bash = run.CreateCard<Bash>(a);

        TeamBuildingContext.Capture(run, new[] { a, b });
        var missing = TeamCoordinator.TeamBonus(bash, a);
        for (var i = 0; i < 6; i++) Give(b, run.CreateCard<Bash>(b));
        TeamBuildingContext.Capture(run, new[] { a, b });
        var saturated = TeamCoordinator.TeamBonus(bash, a);
        check(saturated < missing,
            $"a saturated shared debuff setup must be worth less than a missing one ({saturated:F2} vs {missing:F2}).");
        Console.WriteLine($"PASS: shared debuff value falls once the party is saturated ({missing:F2} -> {saturated:F2}).");
    }

    // Weak coverage is not Vulnerable coverage. A teammate rich in Weak must not
    // reduce a missing Vulnerable bonus, while the same channel saturates.
    private static void WeakCoverageIsSeparateFromVulnerable(Action<bool, string> check)
    {
        var a = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
        var b = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 2, 1));
        a.MaxEnergy = 3; b.MaxEnergy = 3;
        var run = RunState.CreateForTest(new[] { a, b }, seed: "TEAM-CHANNEL");
        ClearDeck(a); ClearDeck(b);
        for (var i = 0; i < 5; i++) Give(a, run.CreateCard<StrikeIronclad>(a));
        var bash = run.CreateCard<Bash>(a);
        var sucker = run.CreateCard<SuckerPunch>(a);

        TeamBuildingContext.Capture(run, new[] { a, b });
        var missingVulnerable = TeamCoordinator.TeamBonus(bash, a);
        // The teammate now holds only Weak setup (Sucker Punch), no Vulnerable.
        for (var i = 0; i < 6; i++) Give(b, run.CreateCard<SuckerPunch>(b));
        TeamBuildingContext.Capture(run, new[] { a, b });
        var vulnerableWithWeakMate = TeamCoordinator.TeamBonus(bash, a);
        var saturatedWeak = TeamCoordinator.TeamBonus(sucker, a);
        check(Math.Abs(missingVulnerable - vulnerableWithWeakMate) < 0.001,
            $"Weak coverage must not reduce a missing Vulnerable bonus ({missingVulnerable:F2} vs {vulnerableWithWeakMate:F2}).");
        check(saturatedWeak < missingVulnerable,
            $"the same Weak channel must still saturate ({saturatedWeak:F2} vs {missingVulnerable:F2}).");
        Console.WriteLine($"PASS: Weak and Vulnerable are scored separately "
            + $"(missing Vulnerable {missingVulnerable:F2} kept, Weak {saturatedWeak:F2} saturated).");
    }

    private static void WeakOwnDeckDoesNotStackSupport(Action<bool, string> check)
    {
        var a = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
        var b = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 2, 1));
        a.MaxEnergy = 3; b.MaxEnergy = 3;
        var run = RunState.CreateForTest(new[] { a, b }, seed: "TEAM-SUPPORT");
        ClearDeck(a); ClearDeck(b);
        // A deck short of its own offence: pure support must not stack.
        for (var i = 0; i < 2; i++) Give(a, run.CreateCard<StrikeIronclad>(a));
        var lift = run.CreateCard<Lift>(a);

        TeamBuildingContext.Capture(run, new[] { a, b });
        var first = TeamCoordinator.TeamBonus(lift, a);
        for (var i = 0; i < 3; i++) Give(a, run.CreateCard<Lift>(a));
        TeamBuildingContext.Capture(run, new[] { a, b });
        var stacked = TeamCoordinator.TeamBonus(lift, a);
        check(first > 0 && stacked < first,
            $"a weak deck must not keep stacking support it cannot cash ({first:F2} -> {stacked:F2}).");
        Console.WriteLine($"PASS: redundant support on a weak own deck loses value ({first:F2} -> {stacked:F2}).");
    }

    private static void UnknownAllySupportIsBounded(Action<bool, string> check)
    {
        var a = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
        var b = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 2, 1));
        a.MaxEnergy = 3; b.MaxEnergy = 3;
        var run = RunState.CreateForTest(new[] { a, b }, seed: "TEAM-CONCOCT");
        ClearDeck(a); ClearDeck(b);
        for (var i = 0; i < 5; i++) Give(a, run.CreateCard<StrikeIronclad>(a));
        TeamBuildingContext.Capture(run, new[] { a, b });

        var modeled = TeamCoordinator.TeamBonus(run.CreateCard<Lift>(a), a);
        var unknown = TeamCoordinator.TeamBonus(run.CreateCard<Concoct>(a), a);
        check(modeled > unknown && unknown <= 2.5,
            $"an unmodeled AnyAlly card must not earn a large team bonus (Lift={modeled:F2}, Concoct={unknown:F2}).");
        Console.WriteLine($"PASS: an unmodeled ally support stays bounded (Lift={modeled:F2}, Concoct={unknown:F2}).");
    }

    private static void HypotheticalOwnContext(Action<bool, string> check)
    {
        var a = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
        var b = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 2, 1));
        a.MaxEnergy = 3; b.MaxEnergy = 3;
        var run = RunState.CreateForTest(new[] { a, b }, seed: "TEAM-HYPOTHETICAL");
        ClearDeck(a); ClearDeck(b);
        for (var i = 0; i < 6; i++) Give(a, run.CreateCard<StrikeIronclad>(a));
        var lift = run.CreateCard<Lift>(a);
        var live = TeamCoordinator.TeamBonus(lift, a);
        // The hypothetical deck lacks the minimum offence, so pure support is
        // discounted even though the live deck does have it.
        var hypothetical = new List<CardModel> { run.CreateCard<StrikeIronclad>(a) };
        var hypotheticalBonus = TeamCoordinator.TeamBonus(lift, a, hypothetical, null);
        check(hypotheticalBonus < live,
            $"a hypothetical own deck must be the context TeamBonus uses ({live:F2} -> {hypotheticalBonus:F2}).");
        Console.WriteLine($"PASS: TeamBonus honours a hypothetical own deck ({live:F2} -> {hypotheticalBonus:F2}).");
    }

    private static void SaturationExcludesOnlyTheSameInstance(Action<bool, string> check)
    {
        var a = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
        a.MaxEnergy = 3;
        var run = RunState.CreateForTest(new[] { a }, seed: "TEAM-SATURATION");
        ClearDeck(a);
        for (var i = 0; i < 4; i++) Give(a, run.CreateCard<StrikeIronclad>(a));
        for (var i = 0; i < 4; i++) Give(a, run.CreateCard<DefendIronclad>(a));
        var actual = a.Deck.Cards.First(card => card.Id.Entry == "STRIKE_IRONCLAD");
        // At four same-id damage cards the role is saturated (Decay 4 -> 0.7).
        // A NEW offered copy is a different instance and must not subtract one of
        // the existing copies; only the actual instance excludes itself.
        var external = run.CreateCard<StrikeIronclad>(a);
        var actualValue = BuildValue.Marginal(actual, a).Total;
        var externalValue = BuildValue.Marginal(external, a).Total;
        check(externalValue < actualValue,
            $"a new offered copy must not subtract an existing copy at saturation "
            + $"(external={externalValue:F2}, actual={actualValue:F2}).");
        Console.WriteLine($"PASS: saturation excludes only the actual instance (external={externalValue:F2} < actual={actualValue:F2}).");
    }

    private static void TwoItemShopBeatsGreedySingle(Action<bool, string> check)
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 3, 1));
        bot.MaxEnergy = 3;
        var run = RunState.CreateForTest(new[] { bot }, seed: "TEAM-SHOP-PAIR");
        ClearDeck(bot);
        for (var i = 0; i < 4; i++) Give(bot, run.CreateCard<StrikeIronclad>(bot));
        for (var i = 0; i < 4; i++) Give(bot, run.CreateCard<DefendIronclad>(bot));
        var gold = 500;
        bot.Gold = gold;
        var room = new MerchantRoom();
        run.PushRoom(room);
        var inventory = new MerchantInventory(bot);
        room.Inventories.Add(inventory);

        double GoldValue(CardModel card) => Math.Max(0, BuildValue.Add(card, bot).Total) * BotShopPlanner.GoldPerDeckValue;
        List<CardModel> DeckPlus(CardModel card)
        {
            var list = bot.Deck.Cards.ToList();
            list.Add(card);
            return list;
        }

        CardModel? chosenA = null, chosenB = null;
        double p = 0, maxSingle = 0;
        int costA = 0, costB = 0;
        var pool = new CardModel[]
        {
            run.CreateCard<TwinStrike>(bot), run.CreateCard<PommelStrike>(bot),
            run.CreateCard<Anger>(bot), run.CreateCard<Inflame>(bot),
            run.CreateCard<Breakthrough>(bot), run.CreateCard<ShrugItOff>(bot),
            run.CreateCard<Bash>(bot),
        };
        foreach (var candidateA in pool)
        {
            foreach (var candidateB in pool)
            {
                if (ReferenceEquals(candidateA, candidateB)) continue;
                var valueA = GoldValue(candidateA);
                var valueB = GoldValue(candidateB);
                if (valueA < 10 || valueB < 10) continue;
                var mediumCostA = (int)Math.Ceiling(valueA * 0.4);
                var mediumCostB = (int)Math.Ceiling(valueB * 0.4);
                if (mediumCostA + mediumCostB > gold) continue;
                // Price the second item against the deck the first would leave,
                // exactly as the shop does.
                var valueBAfterA = Math.Max(0, BuildValue.Add(candidateB, bot, DeckPlus(candidateA)).Total) * BotShopPlanner.GoldPerDeckValue;
                var valueAAfterB = Math.Max(0, BuildValue.Add(candidateA, bot, DeckPlus(candidateB)).Total) * BotShopPlanner.GoldPerDeckValue;
                var pairNet = Math.Max(valueA + valueBAfterA - mediumCostA - mediumCostB,
                    valueB + valueAAfterB - mediumCostA - mediumCostB);
                var bestNet = Math.Max(valueA - mediumCostA, valueB - mediumCostB);
                if (pairNet > bestNet + 5)
                {
                    chosenA = candidateA; chosenB = candidateB;
                    p = pairNet; maxSingle = bestNet;
                    costA = mediumCostA; costB = mediumCostB;
                    break;
                }
            }
            if (chosenA is not null) break;
        }
        check(chosenA is not null && chosenB is not null, "the fixture needs two worthwhile medium cards whose pair beats either alone.");

        // The expensive item is a variable-price relic, so it can only ever be a
        // single purchase: a pair involving it is conservatively excluded.
        var maw = ModelDb.Relic<MawBank>().ToMutable();
        maw.Owner = bot;
        var relicValue = Math.Max(0, HumanCoopAdvisor.RelicValue(maw, bot).Score) * 7;
        var target = (maxSingle + p) / 2;
        var costC = (int)Math.Max(0, Math.Ceiling(relicValue - target));
        var singleNetC = relicValue - costC;
        check(singleNetC > maxSingle + 1 && singleNetC < p - 1 && costC > 0 && costC <= gold,
            $"the expensive single must sit between the pair and the best medium "
            + $"(single={singleNetC:F1}, pair={p:F1}, medium={maxSingle:F1}).");

        var entryA = new MerchantCardEntry(bot, inventory, Array.Empty<CardModel>(), chosenA!.Type);
        typeof(MerchantCardEntry).GetProperty("CreationResult")!.SetValue(entryA, new CardCreationResult(chosenA));
        typeof(MerchantEntry).GetField("_cost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(entryA, costA);
        ((List<MerchantCardEntry>)inventory.CharacterCardEntries).Add(entryA);
        var entryB = new MerchantCardEntry(bot, inventory, Array.Empty<CardModel>(), chosenB!.Type);
        typeof(MerchantCardEntry).GetProperty("CreationResult")!.SetValue(entryB, new CardCreationResult(chosenB));
        typeof(MerchantEntry).GetField("_cost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(entryB, costB);
        ((List<MerchantCardEntry>)inventory.CharacterCardEntries).Add(entryB);
        var relicEntry = (MerchantRelicEntry)RuntimeHelpers.GetUninitializedObject(typeof(MerchantRelicEntry));
        typeof(MerchantEntry).GetField("_player", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(relicEntry, bot);
        typeof(MerchantEntry).GetField("_cost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(relicEntry, costC);
        typeof(MerchantRelicEntry).GetProperty("Model")!.SetValue(relicEntry, maw);
        inventory.AddRelicEntry(relicEntry);

        var plan = BotShopPlanner.ChooseDetailed(inventory);
        check(plan.FromPair, "two affordable medium improvements must beat the greedy best single.");
        check(plan.PairFirst is 0 or 1 && plan.PairSecond is 0 or 1 && plan.PairFirst != plan.PairSecond,
            $"the winning pair must be the two medium cards, got {plan.PairFirst}/{plan.PairSecond}.");
        check(plan.Choice is not null && plan.Choice.Index == plan.PairFirst,
            "the chosen action must be the first card of the winning pair.");
        check(plan.PairNet > plan.SingleNet,
            $"the pair net must exceed the best single ({plan.PairNet:F1} vs {plan.SingleNet:F1}).");
        check(costA + costB <= gold, "the pair must fit the purse.");
        Console.WriteLine($"PASS: the shop buys {chosenA!.Id.Entry}+{chosenB!.Id.Entry} as a pair ({plan.PairNet:F1}) "
            + $"over the greedy single MawBank ({plan.SingleNet:F1}); first action {plan.Choice!.Index}.");
    }

    // The native MawBank.AfterItemPurchased ignores a zero-gold purchase
    // (goldSpent <= 0 returns without disabling), so a FREE first item leaves the
    // bank active and the first PAID item in the pair is the one that consumes the
    // 24-gold income. This drives the real ChooseDetailed on a real inventory once
    // with an active MawBank and once with the same deck and no bank, and checks
    // the pair nets differ by exactly one charge. The two offers are the same card
    // type, so the two purchase orders tie and the planner keeps the earlier
    // (free-first) order, which is the order the bug mishandled.
    private static void MawBankFreeFirstPair(Action<bool, string> check)
    {
        (Player Bot, RunState Run, MerchantInventory Inventory) Setup(int slot, bool withBank)
        {
            var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, slot, 1));
            bot.MaxEnergy = 3;
            var run = RunState.CreateForTest(new[] { bot }, seed: "TEAM-MAW-" + slot);
            ClearDeck(bot);
            // An attack-less deck makes a Twin Strike a large, clearly positive
            // improvement, so the pair comfortably beats the best single.
            for (var i = 0; i < 8; i++) Give(bot, run.CreateCard<DefendIronclad>(bot));
            bot.Gold = 500;
            var room = new MerchantRoom();
            run.PushRoom(room);
            var inventory = new MerchantInventory(bot);
            room.Inventories.Add(inventory);
            if (withBank) bot.AddRelicInternal(ModelDb.Relic<MawBank>().ToMutable());
            return (bot, run, inventory);
        }

        static void AddCard(Player bot, MerchantInventory inventory, CardModel card, int cost)
        {
            var entry = new MerchantCardEntry(bot, inventory, Array.Empty<CardModel>(), card.Type);
            typeof(MerchantCardEntry).GetProperty("CreationResult")!.SetValue(entry, new CardCreationResult(card));
            typeof(MerchantEntry).GetField("_cost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(entry, cost);
            ((List<MerchantCardEntry>)inventory.CharacterCardEntries).Add(entry);
        }

        var (activeBot, activeRun, activeInv) = Setup(21, withBank: true);
        var (plainBot, plainRun, plainInv) = Setup(22, withBank: false);
        var activeFree = activeRun.CreateCard<TwinStrike>(activeBot);
        var activePaid = activeRun.CreateCard<TwinStrike>(activeBot);
        var plainFree = plainRun.CreateCard<TwinStrike>(plainBot);
        var plainPaid = plainRun.CreateCard<TwinStrike>(plainBot);
        var freeValue = Math.Max(0, BuildValue.Add(activeFree, activeBot).Total) * BotShopPlanner.GoldPerDeckValue;
        var paidValue = Math.Max(0, BuildValue.Add(activePaid, activeBot).Total) * BotShopPlanner.GoldPerDeckValue;
        check(freeValue > 26 && Math.Abs(freeValue - paidValue) < 0.001,
            $"the MawBank fixture needs a worthwhile card with equal order values ({freeValue:F1}/{paidValue:F1}).");
        const int paidCost = 1;
        AddCard(activeBot, activeInv, activeFree, 0);
        AddCard(activeBot, activeInv, activePaid, paidCost);
        AddCard(plainBot, plainInv, plainFree, 0);
        AddCard(plainBot, plainInv, plainPaid, paidCost);

        var activePlan = BotShopPlanner.ChooseDetailed(activeInv);
        var plainPlan = BotShopPlanner.ChooseDetailed(plainInv);
        check(activePlan.FromPair && plainPlan.FromPair,
            $"a free item plus a one-gold item must be compared as a pair (active={activePlan.FromPair}, plain={plainPlan.FromPair}).");
        check(activePlan.PairFirst == 0 && activePlan.PairSecond == 1,
            $"the free item must be the first action of the winning pair, got {activePlan.PairFirst}/{activePlan.PairSecond}.");
        // Both plans are the same pair on the same deck; only the active bank
        // charges the one 24-gold income the second purchase would consume.
        check(Math.Abs((plainPlan.PairNet - activePlan.PairNet) - 24) < 0.001,
            $"an active MawBank must charge exactly one 24 income on the free-first pair "
            + $"(plain={plainPlan.PairNet:F2}, active={activePlan.PairNet:F2}).");
        // Free first purchase leaves the bank alive: executing it on the live
        // inventory and replanning makes the paid card the single purchase, still
        // charged the 24.
        Give(activeBot, activeFree);
        typeof(MerchantCardEntry).GetProperty("CreationResult")!
            .SetValue(activeInv.AllEntries.ElementAt(activePlan.Choice!.Index), null);
        var followUp = BotShopPlanner.ChooseDetailed(activeInv);
        var paidAfterFree = Math.Max(0, BuildValue.Add(activePaid, activeBot).Total) * BotShopPlanner.GoldPerDeckValue;
        check(!followUp.FromPair && followUp.Choice is { Index: 1 },
            $"after the free purchase the driver must replan onto the paid card, got {followUp.Choice?.Index.ToString() ?? "(none)"}.");
        check(Math.Abs(followUp.SingleNet - (paidAfterFree - 24 - paidCost)) < 0.001,
            $"the free-first purchase must leave the bank active and still charge the paid card "
            + $"(net={followUp.SingleNet:F2}, expected={paidAfterFree - 24 - paidCost:F2}).");
        Console.WriteLine($"PASS: MawBank free-first pair charges one 24 income "
            + $"(plain pair {plainPlan.PairNet:F2} vs active {activePlan.PairNet:F2}; follow-up single {followUp.SingleNet:F2}).");
    }

    // The driver buys one item per visit and replans. This drives the real
    // Choose, executes the projected first transaction on the real inventory
    // (deck, gold, stock), and checks the follow-up Choose takes the second card.
    private static void SequentialShopReplan(Action<bool, string> check)
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 8, 1));
        bot.MaxEnergy = 3;
        var run = RunState.CreateForTest(new[] { bot }, seed: "TEAM-SEQ");
        ClearDeck(bot);
        for (var i = 0; i < 4; i++) Give(bot, run.CreateCard<StrikeIronclad>(bot));
        for (var i = 0; i < 4; i++) Give(bot, run.CreateCard<DefendIronclad>(bot));
        bot.Gold = 400;
        var room = new MerchantRoom();
        run.PushRoom(room);
        var inventory = new MerchantInventory(bot);
        room.Inventories.Add(inventory);

        MerchantCardEntry Add(CardModel card)
        {
            var entry = new MerchantCardEntry(bot, inventory, Array.Empty<CardModel>(), card.Type);
            typeof(MerchantCardEntry).GetProperty("CreationResult")!.SetValue(entry, new CardCreationResult(card));
            var value = Math.Max(0, BuildValue.Add(card, bot).Total) * BotShopPlanner.GoldPerDeckValue;
            var cost = (int)Math.Ceiling(value * 0.4);
            typeof(MerchantEntry).GetField("_cost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(entry, cost);
            ((List<MerchantCardEntry>)inventory.CharacterCardEntries).Add(entry);
            return entry;
        }

        Add(run.CreateCard<TwinStrike>(bot));
        Add(run.CreateCard<PommelStrike>(bot));

        var firstPlan = BotShopPlanner.ChooseDetailed(inventory);
        check(firstPlan.Choice is not null, "a worthwhile card must be chosen.");
        check(firstPlan.FromPair && firstPlan.PairSecond is not null,
            "two affordable medium cards must be compared as a pair.");
        var firstIndex = firstPlan.Choice!.Index;
        var firstEntry = inventory.AllEntries.ElementAt(firstIndex);
        var firstCard = firstEntry is MerchantCardEntry { CreationResult: not null } entry
            ? entry.CreationResult!.Card : null;
        check(firstCard is not null, "the chosen first action must be a real card.");

        // The first real transaction: the deck grows, gold falls and the entry sells out.
        Give(bot, firstCard!);
        bot.Gold -= firstPlan.Choice.Cost;
        typeof(MerchantCardEntry).GetProperty("CreationResult")!.SetValue(firstEntry, null);

        var secondPlan = BotShopPlanner.ChooseDetailed(inventory);
        check(secondPlan.Choice is not null && secondPlan.Choice.Index != firstIndex,
            $"the driver must replan onto the second item, got {secondPlan.Choice?.Index.ToString() ?? "(none)"}.");
        check(inventory.AllEntries.ElementAt(secondPlan.Choice!.Index) is MerchantCardEntry { CreationResult: not null },
            "the follow-up purchase must be the other stocked card, not the sold one.");
        Console.WriteLine($"PASS: the driver replans after the first purchase and takes the second card "
            + $"(first #{firstIndex}, follow-up #{secondPlan.Choice.Index}).");
    }

    // The second action is re-checked against the existing single-item rules on
    // the projected state: item threshold, reserve, affordability and potion
    // capacity. A pair the driver would refuse is not claimed.
    private static void SecondActionGates(Action<bool, string> check)
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 9, 1));
        var run = RunState.CreateForTest(new[] { bot }, seed: "TEAM-SECOND-GATE");
        PurchaseComparison.Offer Offer(int cost, double value, bool potion = false)
            => new(0, -1, null!, value, cost, potion, false);

        check(BotShopPlanner.SecondExecutable(Offer(40, 60), 60, bot, goldAfterFirst: 100, projectedPotionCount: 0),
            "a second purchase that clears the item threshold and reserve is executable.");
        check(!BotShopPlanner.SecondExecutable(Offer(60, 50), 50, bot, goldAfterFirst: 100, projectedPotionCount: 0),
            "a second purchase below its item threshold is not executable.");
        check(!BotShopPlanner.SecondExecutable(Offer(60, 90), 90, bot, goldAfterFirst: 80, projectedPotionCount: 0),
            "a second purchase that would break the reserve after the first is not executable.");
        check(!BotShopPlanner.SecondExecutable(Offer(40, 60, potion: true), 60, bot,
                goldAfterFirst: 100, projectedPotionCount: bot.MaxPotionCount),
            "a second potion with no remaining slot is not executable.");
        Console.WriteLine("PASS: the second shop action is re-checked against threshold, reserve, purse and potion capacity.");
    }

    private static void PairFeasibility(Action<bool, string> check)
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 4, 1));
        var run = RunState.CreateForTest(new[] { bot }, seed: "TEAM-PAIR-FEASIBLE");
        PurchaseComparison.Offer Offer(int index, int cost, bool removal = false, bool potion = false,
            bool variable = false, bool relic = false)
        {
            MerchantEntry? entry = removal ? new MerchantCardRemovalEntry(bot) : null;
            return new PurchaseComparison.Offer(index, removal ? 0 : -1, entry!, cost, cost, potion, variable, relic);
        }

        var left = Offer(0, 60);
        var right = Offer(1, 60);
        check(!PurchaseComparison.Feasible(left, right, gold: 100, openPotionSlots: 3), "a pair must not overspend the purse.");
        check(!PurchaseComparison.Feasible(Offer(0, 30, removal: true), Offer(1, 30, removal: true), 100, 3),
            "a pair must not contain two removals.");
        check(!PurchaseComparison.Feasible(Offer(0, 30, potion: true), Offer(1, 30, potion: true), 100, 1),
            "a pair must not overfill potion slots.");
        check(PurchaseComparison.Feasible(Offer(0, 30, potion: true), Offer(1, 30, potion: true), 100, 2),
            "two potions fit when two slots are open.");
        check(!PurchaseComparison.Feasible(Offer(0, 30), Offer(1, 30, variable: true), 100, 3),
            "a pair must exclude a variable-price relic.");
        check(!PurchaseComparison.Feasible(Offer(0, 30, relic: true), Offer(1, 30), 100, 3),
            "a pair must exclude any relic, whose side effects cannot be modelled.");
        check(PurchaseComparison.Feasible(Offer(0, 30), Offer(1, 30), 100, 0),
            "a card pair needs no potion capacity.");
        Console.WriteLine("PASS: pair feasibility rejects overspending, duplicate removals, overfilled potions, variable-price and plain relics.");
    }

    private static void SharedRemovalPlan(Action<bool, string> check)
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 5, 1));
        bot.MaxEnergy = 3;
        var run = RunState.CreateForTest(new[] { bot }, seed: "TEAM-REMOVAL");
        ClearDeck(bot);
        for (var i = 0; i < 2; i++) Give(bot, run.CreateCard<DefendIronclad>(bot));
        for (var i = 0; i < 12; i++) Give(bot, run.CreateCard<StrikeIronclad>(bot));
        var before = bot.Deck.Cards.ToList();

        var forced = BotBrain.SelectCards(bot, before, 2, 2, "FromDeckForRemoval");
        var removable = bot.Deck.Cards.Where(card => card.IsRemovable).ToList();
        var plan = RemovalPlan.Choose(bot, removable, 2);
        check(plan.Steps.Count == 2 && forced.Count == 2, "two removals must return two cards.");
        check(plan.Steps.Select(step => step.Card).SequenceEqual(forced),
            "the Cook removal plan and the forced two-card BotBrain selection must be the same ordered cards.");
        check(plan.Steps.Count(step => step.Card.Id.Entry == "DEFEND_IRONCLAD") <= 1,
            "the last block role must stay protected across two removals.");
        check(bot.Deck.Cards.Count == before.Count
            && before.Zip(bot.Deck.Cards).All(pair => ReferenceEquals(pair.First, pair.Second)),
            "the real deck must be untouched and unbranded by a removal plan.");
        Console.WriteLine($"PASS: RemovalPlan matches BotBrain's forced two removals "
            + $"({string.Join(",", plan.Steps.Select(step => step.Card.Id.Entry))}) and leaves the deck unchanged.");
    }

    private static void NegativeRemovalIsNotInflated(Action<bool, string> check)
    {
        var patch = typeof(BotBrain).Assembly.GetType("CoopBots.BotRestSitePatch")!;
        var cookValue = patch.GetMethod("CookValue", BindingFlags.Static | BindingFlags.NonPublic)!;
        double Cook(Player player) => (double)cookValue.Invoke(null, new object[] { player })!;

        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 6, 1));
        bot.MaxEnergy = 3;
        var run = RunState.CreateForTest(new[] { bot }, seed: "TEAM-COOK");
        ClearDeck(bot);
        // Two strong, unprotected cards: removing them is a real cost, so the
        // signed removal term must pull the choice below the +5 max HP baseline.
        Give(bot, run.CreateCard<Inflame>(bot));
        Give(bot, run.CreateCard<Inflame>(bot));
        var forcedLoss = Cook(bot);
        check(forcedLoss < 10 - 0.001 && forcedLoss >= 0,
            $"a harmful forced removal must not claim the free +10, got {forcedLoss:F2}.");

        // A forced step onto an explicitly protected card is the planner
        // refusing; Cook is declined (zero), not valued as a free removal.
        ClearDeck(bot);
        Give(bot, run.CreateCard<DefendIronclad>(bot));
        Give(bot, run.CreateCard<Breakthrough>(bot));
        var forcedProtected = Cook(bot);
        check(Math.Abs(forcedProtected) < 0.001,
            $"a protected required step must decline Cook outright, got {forcedProtected:F2}.");

        // A deck that can be thinned without touching a protected role keeps the
        // ordinary +5 max HP baseline at least.
        ClearDeck(bot);
        for (var i = 0; i < 8; i++) Give(bot, run.CreateCard<StrikeIronclad>(bot));
        for (var i = 0; i < 4; i++) Give(bot, run.CreateCard<DefendIronclad>(bot));
        var evaluable = Cook(bot);
        check(evaluable >= 10 - 0.001,
            $"an evaluable Cook plan must keep at least the +10 max HP value, got {evaluable:F2}.");
        Console.WriteLine($"PASS: Cook removal value is signed and protected steps decline "
            + $"(harmful {forcedLoss:F2}, protected {forcedProtected:F2}, evaluable {evaluable:F2}).");
    }

    private static void ReachableRiskIsOrderInvariant(Action<bool, string> check)
    {
        var forward = new[] { MapPointType.Monster, MapPointType.Elite, MapPointType.Shop };
        var backward = forward.Reverse().ToArray();
        var a = BotRestSitePatch.MaxReachableRisk(0, forward, lastCampBeforeBoss: false);
        var b = BotRestSitePatch.MaxReachableRisk(0, backward, lastCampBeforeBoss: false);
        check(Math.Abs(a - b) < 0.0001,
            $"reachable risk must not depend on child order ({a:F2} vs {b:F2}).");
        check(Math.Abs(a - BotRestSitePatch.RiskFor(MapPointType.Elite, 0)) < 0.0001,
            $"the maximum reachable risk must be the worst child ({a:F2}).");
        check(Math.Abs(BotRestSitePatch.MaxReachableRisk(0, null, false) - 1) < 0.0001,
            "an unknown map must stay neutral.");
        var bossGuard = BotRestSitePatch.MaxReachableRisk(0, forward, lastCampBeforeBoss: true);
        check(Math.Abs(bossGuard - BotRestSitePatch.RiskFor(MapPointType.Boss, 0)) < 0.0001,
            "the last-camp guard must keep its own act's boss risk.");
        Console.WriteLine($"PASS: reachable risk is order invariant ({a:F2}) with a neutral unknown map and boss guard ({bossGuard:F2}).");
    }

    // Documentation only: rough headless cost after warmup. No wall-clock assert
    // is made, and no runtime promise follows from these numbers.
    private static void DecisionCostMilliseconds(Action<bool, string> check)
    {
        // Three real bots with their own 25-card decks, so the reward loop scores
        // three different players rather than repeating one bot.
        var players = new Player[3];
        for (var i = 0; i < players.Length; i++)
        {
            players[i] = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 10 + i, 1));
            players[i].MaxEnergy = 3;
        }
        var run = RunState.CreateForTest(players, seed: "TEAM-COST");
        Func<Player, CardModel>[] makers =
        {
            p => run.CreateCard<StrikeIronclad>(p), p => run.CreateCard<DefendIronclad>(p),
            p => run.CreateCard<Bash>(p), p => run.CreateCard<Inflame>(p),
            p => run.CreateCard<PommelStrike>(p), p => run.CreateCard<TwinStrike>(p),
            p => run.CreateCard<ShrugItOff>(p), p => run.CreateCard<Uppercut>(p),
        };
        foreach (var player in players)
        {
            ClearDeck(player);
            for (var i = 0; i < 25; i++) Give(player, makers[i % makers.Length](player));
        }
        TeamBuildingContext.Capture(run, players);
        var candidates = players.Select(player => new CardModel[]
        {
            run.CreateCard<Bash>(player), run.CreateCard<Inflame>(player), run.CreateCard<ShrugItOff>(player),
        }).ToArray();
        // One warm reward decision per player, then time exactly one each.
        for (var i = 0; i < players.Length; i++) _ = BuildValue.BestReward(players[i], candidates[i], allowSkip: true);
        var rewardWatch = Stopwatch.StartNew();
        for (var i = 0; i < players.Length; i++) _ = BuildValue.BestReward(players[i], candidates[i], allowSkip: true);
        rewardWatch.Stop();

        // A real pair-search stress fixture: 16 individually worthwhile stocked
        // card offers at 1 gold against a 25-card damage-deficit deck (20 Defend
        // + 5 Injury), with enough gold. This forces the bounded 16-offer pair
        // loop rather than a one-item single probe.
        var shopper = players[0];
        ClearDeck(shopper);
        for (var i = 0; i < 20; i++) Give(shopper, run.CreateCard<DefendIronclad>(shopper));
        for (var i = 0; i < 5; i++) Give(shopper, run.CreateCard<Injury>(shopper));
        shopper.Gold = 2000;
        var room = new MerchantRoom();
        run.PushRoom(room);
        var inventory = new MerchantInventory(shopper);
        room.Inventories.Add(inventory);
        for (var i = 0; i < 16; i++)
        {
            var card = run.CreateCard<TwinStrike>(shopper);
            var entry = new MerchantCardEntry(shopper, inventory, Array.Empty<CardModel>(), card.Type);
            typeof(MerchantCardEntry).GetProperty("CreationResult")!.SetValue(entry, new CardCreationResult(card));
            typeof(MerchantEntry).GetField("_cost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(entry, 1);
            ((List<MerchantCardEntry>)inventory.CharacterCardEntries).Add(entry);
        }
        var stocked = inventory.AllEntries.Count(entry => entry.IsStocked);
        var probe = BotShopPlanner.ChooseDetailed(inventory);
        check(probe.FromPair && probe.PairFirst is not null && probe.PairSecond is not null,
            $"the timing shop fixture must exercise the bounded pair loop (FromPair={probe.FromPair}, stock={stocked}).");
        check(stocked == 16 && shopper.Deck.Cards.Count == 25,
            $"the timing fixture must stock 16 offers against a 25-card deck (stock={stocked}, deck={shopper.Deck.Cards.Count}).");
        var shopWatch = Stopwatch.StartNew();
        _ = BotShopPlanner.ChooseDetailed(inventory);
        shopWatch.Stop();

        check(rewardWatch.ElapsedMilliseconds >= 0 && shopWatch.ElapsedMilliseconds >= 0, "timing must be readable.");
        Console.WriteLine($"COST: one reward decision for each of 3 real bots (25-card decks) = {rewardWatch.Elapsed.TotalMilliseconds:F1} ms; "
            + $"bounded 16-offer pair shop on a 25-card deck (20 Defend + 5 Injury, gold 2000) = {shopWatch.Elapsed.TotalMilliseconds:F1} ms "
            + $"(pair={probe.FromPair}, first/second={probe.PairFirst}/{probe.PairSecond}, stock={stocked}, documentation only).");
    }
}
