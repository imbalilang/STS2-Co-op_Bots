using CoopBots;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor.Engine.Common;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class PostDecisionPotionScenarios
{
    // Scheduling forecasts below intentionally supplied as cached WIPE/cutoff;
    // the native assertions concern this side boundary, not a fabricated full
    // fight victory. The 6HP/Defend/Dex sample matches the logged R2 arithmetic.
    internal static void Run(string? caseName = null)
    {
        TestEnvironment.Ensure();
        var presentation = new Harmony("coopbots.post-decision-potion.presentation");
        presentation.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(PostDecisionPotionScenarios), nameof(Format)));
        presentation.Patch(AccessTools.Method(typeof(RunManager), nameof(RunManager.HasAscension)),
            prefix: new HarmonyMethod(typeof(PostDecisionPotionScenarios), nameof(A10)));
        try
        {
            var cases = new (string Name, Action Run)[] {
                ("cached-dex", CachedDex), ("cutoff-energy", CutoffEnergy),
                ("tainted-multihit", TaintedMultiHit), ("frail-hpcost", Frail), ("hp-cost", HpCost),
                ("extra-turn", ExtraTurn), ("high-incoming", HighIncoming),
                ("no-rescue-cache", NoRescueCache), ("winning-cache", WinningCache),
                ("witness-bounds", WitnessBounds), ("no-potion-repair", NoPotionRepair),
                ("stale-rescue", StaleRescue), ("seat-swap", SeatSwap),
                ("boss-opening-block-expiry-native", BossOpeningBlockExpiryNative) };
            if (caseName is not null && !cases.Any(c => c.Name == caseName))
                throw new InvalidOperationException("Unknown post-decision potion case: " + caseName);
            foreach (var c in cases.Where(c => caseName is null || c.Name == caseName))
            { c.Run(); Console.WriteLine("PASS: post-decision potion " + c.Name); }
        }
        finally { presentation.UnpatchAll(presentation.Id); }
    }
    private static bool Format(ref string __result) { __result = ""; return false; }

    // R2 actual 2026-10-10: both isolated production mutations compiled 0,
    // named native RED1, exact source/six-artifact restore, native GREEN0.
    // work/private-block-expiry-v1/execution-20261009T230341937384Z/evidence/boss-opening-block-expiry-native/red/run.log:
    // System.InvalidOperationException: Post-decision potion: boss-opening-block-expiry-native: completed BECKON/no-retention root must retain its zero-utility BlockPotion
    // work/private-block-expiry-v1/execution-20261009T230527335606Z/evidence/boss-opening-block-order-native/red/run.log:
    // System.InvalidOperationException: Post-decision potion: boss-opening order must resolve a real Colorless chooser before probing temporary BlockPotion
    // V2 third physical R2 actual 2026-10-10: remove only the unknown-domain
    // guard; forced Rebuild0/native RED1, exact two-source/six-artifact restore
    // and native GREEN0. Prior two v1 physical proofs above are reused unchanged.
    // work/private-block-expiry-v2/execution-20261009T232623385911Z/evidence/boss-opening-block-domain-native/red/run.log:
    // System.InvalidOperationException: Post-decision potion: boss-opening-block-domain-native: active Calamity generation from plain Strike must preserve unknown BlockPotion utility; rejected domains=Calamity generation,non-null Enchantment,non-null Affliction
    // V2 zero policy witness is real Axebot BOOT_UP. SoulFysh's card-pile
    // lifecycle is now Unknown; its native BECKON expiry is not a zero proof.
    // Current native Colorless actually creates Bolas, so the settled fresh root
    // is Unknown and still allows Block. The original Fysh016 L870 unresolved
    // chooser and the complete live opening trace are not zero-utility proofs.
    private static void BossOpeningBlockExpiryNative()
    {
        static IDisposable Original(CombatState c) => (IDisposable)Activator.CreateInstance(
            AccessTools.Inner(typeof(KernelPlanningFactsScenarios), "NativeFactsState"),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic, null, [c], null)!;
        static (Player[] party, CombatState c, Creature e) NativeBoard(string move = "BOOT_UP_MOVE", bool ironclad = false)
        {
            var p = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 107));
            // Existing native fixture contract: an empty-inventory player with
            // the actual native character avoids starting-relic SaveManager
            // presentation access. CardPool and Calamity rules remain native.
            if (ironclad) AccessTools.Field(typeof(Player), "<Character>k__BackingField")
                .SetValue(p, ModelDb.Character<Ironclad>());
            var c = new CombatState(runState: RunState.CreateForTest([p], ascensionLevel: 10,
                seed: "BOSS-BLOCK-EXPIRY-NATIVE")) { RoundNumber = 1 };
            p.MaxEnergy = 3; p.ResetCombatState(); c.AddPlayer(p);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
            p.PlayerCombatState.IncrementTurnNumber();
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
            p.PlayerCombatState.Hand.AddInternal(c.CreateCard<StrikeSilent>(p));
            for (var i = 0; i < 6; i++) p.PlayerCombatState.DrawPile.AddInternal(c.CreateCard<StrikeSilent>(p));
            MonsterModel m = move == "BOOT_UP_MOVE" ? ModelDb.Monster<Axebot>().ToMutable()
                : ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.SoulFysh>().ToMutable();
            var e = c.CreateCreature(m, CombatSide.Enemy, "native-fysh"); c.AddCreature(e); m.SetUpForCombat();
            e.SetMaxHpInternal(800); e.SetCurrentHpInternal(800);
            m.SetMoveImmediate((MoveState)m.MoveStateMachine!.States[move], true);
            return ([p], c, e);
        }
        static void Use(PotionModel q, Creature target) => q.OnUseWrapper(
            new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(), target).GetAwaiter().GetResult();
        static void EnemyThenClear(Player[] party, CombatState c, Creature e)
        {
            c.CurrentSide = CombatSide.Enemy;
            e.Monster!.NextMove.PerformMove(party.Select(p => p.Creature).ToArray()).GetAwaiter().GetResult();
            c.CurrentSide = CombatSide.Player; c.RoundNumber++;
            foreach (var p in party) { p.PlayerCombatState!.IncrementTurnNumber(); p.Creature.BeforeTurnStart(CombatSide.Player); }
            var participants = party.Select(p => p.Creature).ToArray();
            MegaCrit.Sts2.Core.Hooks.Hook.BeforeSideTurnStart(c, CombatSide.Player, participants).GetAwaiter().GetResult();
            foreach (var p in party) p.Creature.AfterTurnStart(CombatSide.Player).GetAwaiter().GetResult();
            foreach (var p in party) MegaCrit.Sts2.Core.Hooks.Hook.AfterBlockCleared(c, p.Creature).GetAwaiter().GetResult();
            MegaCrit.Sts2.Core.Hooks.Hook.AfterSideTurnStart(c, CombatSide.Player, participants).GetAwaiter().GetResult();
        }
        static KernelPotionOpportunity.Choice? Opening(CombatState c, Player[] party)
        {
            var root = KernelSession.Capture(c); var before = root.PlanningState(party.Select(p => p.NetId));
            var run = (RunState)c.RunState; var shuffle = run.Rng.Shuffle.CaptureState();
            var selection = run.Rng.CombatCardSelection.CaptureState();
            var nativeCards = party.SelectMany(p => p.PlayerCombatState!.AllPiles)
                .Select(pile => pile.Cards.ToArray()).ToArray();
            var nativeBottles = party.Select(p => p.Potions.ToArray()).ToArray();
            var choice = KernelPotionOpportunity.Choose(root, party, party, bossOpening: true,
                request: new KernelDecisionRequest(107, 6400, 1));
            var after = root.PlanningState(party.Select(p => p.NetId));
            var fresh = KernelSession.Capture(c).PlanningState(party.Select(p => p.NetId));
            // Unknown roots may be unqualified for cache reuse. Matches then
            // intentionally rejects even identical evidence; verify immutable
            // evidence and physical native objects without granting reuse.
            if (!before.Reusable) Console.WriteLine("NATIVE unchanged Unknown root " + before.FirstMismatch(after));
            Check(after.Semantics == before.Semantics && fresh.Semantics == before.Semantics
                && after.SearchMetadata == before.SearchMetadata && fresh.SearchMetadata == before.SearchMetadata
                && after.Reusable == before.Reusable && fresh.Reusable == before.Reusable
                && party.SelectMany(p => p.PlayerCombatState!.AllPiles).Select((pile, i) =>
                    pile.Cards.SequenceEqual(nativeCards[i], ReferenceEqualityComparer.Instance)).All(same => same)
                && party.Select((p, i) => p.Potions.SequenceEqual(nativeBottles[i], ReferenceEqualityComparer.Instance)).All(same => same)
                && run.Rng.Shuffle.CaptureState() == shuffle && run.Rng.CombatCardSelection.CaptureState() == selection,
                "boss-opening block probe must leave original root, native piles/inventory, HP and both RNG streams unchanged: "
                    + before.FirstMismatch(after) + "; fresh=" + before.FirstMismatch(fresh));
            return choice;
        }

        var relatedHooks = (System.Reflection.MethodInfo[])AccessTools.Field(typeof(KernelPotionOpportunity),
            "UnresolvedOpeningBlockHooks").GetValue(null)!;
        Check(relatedHooks.All(h => h.DeclaringType == typeof(AbstractModel) && h.IsVirtual && !h.IsSpecialName)
            && relatedHooks.Any(h => h.Name == nameof(AbstractModel.BeforeCardPlayed))
            && relatedHooks.Any(h => h.Name == nameof(AbstractModel.AfterCardPlayed)),
            "actual native lifecycle domain must select virtual hooks and exclude properties/non-hook base methods");
        Console.WriteLine("NATIVE unresolved block-domain hooks=" + string.Join(',', relatedHooks.Select(h => h.Name).Order()));
        var rejectedUnknownDomains = new List<string>();

        // Independent actual native consumption/expiry, not KernelSession.EndTurn.
        // SoulFysh's native AfterCardChangedPilesLate is also Unknown under the
        // conservative v2 domain; no encounter exception. Axebot BOOT_UP is the
        // plain no-related-hook zero witness, calibrated against the current DLL.
        var (zeroParty, zeroCombat, zeroEnemy) = NativeBoard();
        using (Original(zeroCombat))
        {
            var p = zeroParty[0]; var q = Bottle<BlockPotion>(p);
            Check(Opening(zeroCombat, zeroParty) is null && p.Potions.Contains(q) && p.Creature.Block == 0,
                "boss-opening-block-expiry-native: completed plain native zero-threat root must retain its zero-utility BlockPotion");
            var run = (RunState)zeroCombat.RunState; var shuffle = run.Rng.Shuffle.CaptureState();
            var selection = run.Rng.CombatCardSelection.CaptureState();
            Use(q, p.Creature);
            Check(!p.Potions.Contains(q) && p.Creature.Block == 12 && p.Creature.CurrentHp == 80
                && run.Rng.Shuffle.CaptureState() == shuffle && run.Rng.CombatCardSelection.CaptureState() == selection,
                "original BlockPotion must consume its native bottle and grant exactly twelve block without RNG use");
            EnemyThenClear(zeroParty, zeroCombat, zeroEnemy);
            Check(p.Creature.CurrentHp == 80 && p.Creature.Block == 0,
                "original Axebot BOOT_UP must not damage players and original player start must expire potion block");
            Console.WriteLine("NATIVE boss-opening block expiry Axebot BOOT_UP hp=80 block=0 bottle=consumed");
        }
        {
            var (party, c, e) = NativeBoard(ironclad: true); using var native = Original(c); var p = party[0];
            ModelDb.Power<CalamityPower>().ToMutable().ApplyInternal(p.Creature, 1, true); var q = Bottle<BlockPotion>(p);
            if (Opening(c, party)?.Action.Potion != q) rejectedUnknownDomains.Add("Calamity generation");
            var strike = p.PlayerCombatState!.Hand.Cards.OfType<StrikeSilent>().Single();
            AccessTools.Method(typeof(KernelPlanningFactsScenarios), "NativePaidCard").Invoke(null, [strike, e]);
            var generated = p.PlayerCombatState.Hand.Cards.Single();
            Check(generated.Type == CardType.Attack && !ReferenceEquals(generated, strike)
                && e.CurrentHp == 794 && p.PlayerCombatState.Energy == 2 && p.Potions.Contains(q),
                "original paid Strike with actual Calamity must generate a new native attack and retain the bottle until dispatch");
            Check(p.Character.CardPool.GetUnlockedCards(p.UnlockState, p.RunState.CardMultiplayerConstraint).Any(card => card is BodySlam),
                "actual native Calamity attack pool must include block-converting BodySlam, without predicting its random selection");
            Console.WriteLine($"NATIVE boss-opening unknown Calamity paidStrike generated={generated.Id.Entry} energy=2 BodySlamInNativePool=True potion=allowed");
        }
        {
            var (party, c, e) = NativeBoard(); using var native = Original(c); var p = party[0];
            var strike = p.PlayerCombatState!.Hand.Cards.OfType<StrikeSilent>().Single();
            var corrupted = ModelDb.Enchantment<MegaCrit.Sts2.Core.Models.Enchantments.Corrupted>().ToMutable();
            strike.EnchantInternal(corrupted, 1); corrupted.ModifyCard(); var q = Bottle<BlockPotion>(p);
            if (Opening(c, party)?.Action.Potion != q) rejectedUnknownDomains.Add("non-null Enchantment");
            Check(strike.Enchantment == corrupted, "actual native Corrupted attachment must be installed on the plain Strike");
            Use(q, p.Creature);
            AccessTools.Method(typeof(KernelPlanningFactsScenarios), "NativePaidCard").Invoke(null, [strike, e]);
            Check(p.Creature.CurrentHp == 78 && p.Creature.Block == 12 && e.CurrentHp == 791,
                "actual Corrupted self-damage is unblockable: it is a domain control, not potion benefit evidence");
            Console.WriteLine("NATIVE boss-opening unknown Corrupted paidStrike hp=78 block=12 enemyHP=791 unblockable-domain-only");
        }
        {
            var (party, c, _) = NativeBoard(); using var native = Original(c); var p = party[0];
            var defend = Hand<DefendSilent>(c, p, false);
            var tainted = ModelDb.Affliction<Tainted>().ToMutable(); defend.AfflictInternal(tainted, 2); var q = Bottle<BlockPotion>(p);
            if (Opening(c, party)?.Action.Potion != q) rejectedUnknownDomains.Add("non-null Affliction");
            Check(defend.Affliction is Tainted, "actual native Tainted attachment must be installed on the plain Defend");
            AccessTools.Method(typeof(KernelPlanningFactsScenarios), "NativePaidCard").Invoke(null, [defend, null]);
            Check(p.PlayerCombatState!.Energy == 2 && p.Creature.Block == 5 && p.Potions.Contains(q),
                "native attached Defend must settle actual paid card while the domain-control potion remains held");
            Console.WriteLine("NATIVE boss-opening unknown Tainted paidDefend energy=2 block=5 potion=allowed-domain-only");
        }
        Check(rejectedUnknownDomains.Count == 0,
            "boss-opening-block-domain-native: active Calamity generation from plain Strike must preserve unknown BlockPotion utility; rejected domains="
            + string.Join(',', rejectedUnknownDomains));
        {
            var (party, c, e) = NativeBoard("BECKON_MOVE"); using var native = Original(c); var p = party[0];
            var q = Bottle<BlockPotion>(p);
            Check(Opening(c, party)?.Action.Potion == q,
                "native SoulFysh Card-pile lifecycle override must remain Unknown without an encounter whitelist");
            Use(q, p.Creature); EnemyThenClear(party, c, e);
            Check(p.Creature.CurrentHp == 80 && p.Creature.Block == 0
                && p.PlayerCombatState!.DrawPile.Cards.OfType<Beckon>().Count() == 1
                && p.PlayerCombatState.DiscardPile.Cards.OfType<Beckon>().Count() == 1,
                "original SoulFysh BECKON still independently proves expiry, without claiming policy zero over its unknown hook");
            Console.WriteLine("NATIVE boss-opening SoulFysh Card-pile hook unknown-allowed BECKON hp=80 block=0 status=1/1");
        }
        var (attackParty, attackCombat, attackEnemy) = NativeBoard("DE_GAS_MOVE");
        using (Original(attackCombat))
        {
            var p = attackParty[0]; var q = Bottle<BlockPotion>(p);
            Check(Opening(attackCombat, attackParty)?.Action.Potion == q,
                "boss-opening block must remain eligible for native imminent attack damage");
            Use(q, p.Creature); EnemyThenClear(attackParty, attackCombat, attackEnemy);
            Check(p.Creature.CurrentHp == 74 && p.Creature.Block == 0,
                "original A10 DE_GAS18 must leave 74HP after actual twelve-block potion");
            Console.WriteLine("NATIVE boss-opening block attack DE_GAS18 hp=74 block=0");
        }
        foreach (var blur in new[] { false, true })
        {
            var (party, c, e) = NativeBoard(); using var native = Original(c); var p = party[0];
            PowerModel power = blur ? ModelDb.Power<BlurPower>().ToMutable() : ModelDb.Power<BarricadePower>().ToMutable();
            power.ApplyInternal(p.Creature, 1, true); var q = Bottle<BlockPotion>(p);
            Check(Opening(c, party)?.Action.Potion == q, "native retained block must remain eligible without an attack intent");
            Use(q, p.Creature); EnemyThenClear(party, c, e);
            Check(p.Creature.CurrentHp == 80 && p.Creature.Block == 12,
                "original Barricade/Blur must retain actual potion block after BECKON and native player start");
            Console.WriteLine($"NATIVE boss-opening block retention {power.GetType().Name} block=12");
        }
        {
            var (party, c, e) = NativeBoard(); using var native = Original(c); var p = party[0];
            ModelDb.Power<JuggernautPower>().ToMutable().ApplyInternal(p.Creature, 6, true); var q = Bottle<BlockPotion>(p);
            Check(Opening(c, party)?.Action.Potion == q, "native on-block damage must remain eligible without an attack intent");
            Use(q, p.Creature);
            Check(e.CurrentHp == 794 && p.Creature.Block == 12, "original Juggernaut must deal six damage on actual potion block gain");
            Console.WriteLine("NATIVE boss-opening block Juggernaut enemyHP=794 block=12");
        }
        {
            var (party, c, e) = NativeBoard(); using var native = Original(c); var p = party[0];
            var slam = Hand<BodySlam>(c, p, tainted: false); var q = Bottle<BlockPotion>(p);
            Check(Opening(c, party)?.Action.Potion == q, "payable native BodySlam must preserve block offense without an attack intent");
            Use(q, p.Creature);
            AccessTools.Method(typeof(KernelPlanningFactsScenarios), "NativePaidCard").Invoke(null, [slam, e]);
            Check(e.CurrentHp == 788 && p.PlayerCombatState!.Energy == 2 && !p.PlayerCombatState.Hand.Cards.Contains(slam),
                "original paid BodySlam must consume one energy and convert actual potion block into twelve damage");
            Console.WriteLine("NATIVE boss-opening block BodySlam enemyHP=788 energy=2");
        }
        foreach (var drink in new[] { false, true })
        {
            var (party, c, e) = NativeBoard(); using var native = Original(c); var p = party[0];
            ModelDb.Power<ThornsPower>().ToMutable().ApplyInternal(e, 3, true); var q = Bottle<BlockPotion>(p);
            Check(Opening(c, party)?.Action.Potion == q,
                "paid Strike counterattack must preserve potion utility even when BECKON has no attack intent");
            if (drink) Use(q, p.Creature);
            var strike = p.PlayerCombatState!.Hand.Cards.OfType<StrikeSilent>().Single();
            AccessTools.Method(typeof(KernelPlanningFactsScenarios), "NativePaidCard").Invoke(null, [strike, e]);
            Check(e.CurrentHp == 794 && p.PlayerCombatState.Energy == 2
                && p.Creature.CurrentHp == (drink ? 80 : 77) && p.Creature.Block == (drink ? 9 : 0),
                "original paid Strike must trigger native Thorns3, blocked only by the actual consumed BlockPotion");
            Console.WriteLine($"NATIVE boss-opening block paid-Strike Thorns3 drink={drink} hp={p.Creature.CurrentHp} block={p.Creature.Block}");
        }
        var basicCards = new (string Name, Func<CombatState, Player, CardModel> Create, int Damage, int Block, int Energy)[] {
            ("StrikeSilent", (c, p) => Hand<StrikeSilent>(c, p, false), 6, 0, 1),
            ("StrikeIronclad", (c, p) => Hand<StrikeIronclad>(c, p, false), 6, 0, 1),
            ("DefendSilent", (c, p) => Hand<DefendSilent>(c, p, false), 0, 5, 1),
            ("DefendIronclad", (c, p) => Hand<DefendIronclad>(c, p, false), 0, 5, 1),
            ("Shiv", (c, p) => Hand<Shiv>(c, p, false), 4, 0, 0) };
        foreach (var known in basicCards)
        {
            var (party, c, e) = NativeBoard(); using var native = Original(c); var p = party[0];
            var card = known.Create(c, p); var q = Bottle<BlockPotion>(p);
            Check(Opening(c, party) is null && p.Potions.Contains(q),
                "known plain native card must not invent future block utility: " + known.Name);
            var paid = card.SpendResources().GetAwaiter().GetResult();
            Check(paid.Item1 == known.Energy, "plain native control must pay its actual energy: " + known.Name);
            card.OnPlayWrapper(new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(),
                known.Damage > 0 ? e : null, false,
                new ResourceInfo { EnergySpent = paid.Item1, EnergyValue = paid.Item1,
                    StarsSpent = paid.Item2, StarValue = paid.Item2 }, skipCardPileVisuals: true).GetAwaiter().GetResult();
            Check(e.CurrentHp == 800 - known.Damage && p.Creature.Block == known.Block,
                "native plain attack/block must settle its independent payment and result: " + known.Name);
            Use(q, p.Creature); EnemyThenClear(party, c, e);
            Check(p.Creature.CurrentHp == 80 && p.Creature.Block == 0,
                "native plain card plus actual BlockPotion must still expire without damage: " + known.Name);
            Console.WriteLine($"NATIVE boss-opening known {known.Name} paid={paid.Item1} damage={known.Damage} plainBlock={known.Block} finalBlock=0");
        }
        {
            var (party, c, e) = NativeBoard(); using var native = Original(c); var p = party[0];
            var dance = Hand<BladeDance>(c, p, tainted: false); dance.UpgradeInternal(); dance.FinalizeUpgradeInternal();
            var cloak = Hand<CloakAndDagger>(c, p, tainted: false); cloak.UpgradeInternal(); cloak.FinalizeUpgradeInternal();
            var q = Bottle<BlockPotion>(p);
            Check(Opening(c, party) is null && p.Potions.Contains(q),
                "known native Shiv generators must not invent block-conversion utility for a zero-threat opening");
            AccessTools.Method(typeof(KernelPlanningFactsScenarios), "NativePaidCard").Invoke(null, [dance, null]);
            AccessTools.Method(typeof(KernelPlanningFactsScenarios), "NativePaidCard").Invoke(null, [cloak, null]);
            Check(p.PlayerCombatState!.Hand.Cards.OfType<Shiv>().Count() == 6 && p.PlayerCombatState.Energy == 1
                && e.CurrentHp == 800 && p.Potions.Contains(q),
                "original paid BladeDance+/CloakAndDagger+ must generate six actual Shiv and retain the potion");
            Console.WriteLine("NATIVE boss-opening known generators BladeDance+/CloakAndDagger+ shiv=6 energy=1");
        }
        {
            var (party, c, _) = NativeBoard(); using var native = Original(c); var p = party[0];
            Hand<CrimsonMantle>(c, p, tainted: false); var q = Bottle<BlockPotion>(p);
            Check(Opening(c, party)?.Action.Potion == q, "an unmodeled native card must preserve unknown block utility");
        }
        {
            var (party, c, _) = NativeBoard(); using var native = Original(c); var p = party[0];
            var block = Bottle<BlockPotion>(p); var colorless = Bottle<ColorlessPotion>(p);
            Check(Opening(c, party)?.Action.Potion == colorless && p.Potions.Contains(block),
                "boss-opening order must resolve a real Colorless chooser before probing temporary BlockPotion");
            var chooser = new MegaCrit.Sts2.Core.TestSupport.TestCardSelector();
            // The current native path resolves to Bolas despite this selector's
            // skip request. Assert the actual result, never an assumed skip or
            // the original Fysh016 offers. Bolas is unknown to the finite guard.
            chooser.PrepareToSelect(Array.Empty<CardModel>());
            using (MegaCrit.Sts2.Core.Commands.CardSelectCmd.PushSelector(chooser)) Use(colorless, p.Creature);
            var fresh = KernelSession.Capture(c);
            Console.WriteLine($"NATIVE chooser fresh hand={string.Join(',', p.PlayerCombatState!.Hand.Cards.Select(card => card.GetType().Name))} bottles={string.Join(',', p.Potions.Select(q => q.GetType().Name))} canFork={fresh.CanFork} projection={string.Join('|', fresh.ProjectionDifferences)} risk={fresh.LastActionHadEnvironmentalRisk} threat={fresh.Threat(p).HpLoss}");
            Console.WriteLine("NATIVE chooser simulated piles=" + string.Join(';', fresh.Simulator.State.GetPlayerCombatState(p).AllPiles.Select(pile => pile.Type + ":" + string.Join(',', pile.Cards.Select(card => card.Preview.GetType().Name)))));
            Check(!p.Potions.Contains(colorless) && p.Potions.Contains(block)
                && p.PlayerCombatState.Hand.Cards.OfType<Bolas>().Count() == 1
                && fresh.Hand(p).Any(card => card is Bolas)
                && Opening(c, party)?.Action.Potion == block,
                "actual Colorless result Bolas must survive fresh capture and preserve unknown BlockPotion utility");
            Console.WriteLine("NATIVE boss-opening order Colorless nativeResult=Bolas then Block=unknown-allowed");
        }
    }
    private static bool A10(AscensionLevel level, ref bool __result) { __result = (int)level <= 10; return false; }
    private static void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException("Post-decision potion: " + why); }
    private static T Field<T>(object owner, string name) => (T)AccessTools.Field(owner.GetType(), name).GetValue(owner)!;

    private static (Player[] Party, CombatState Combat, Creature Enemy) Board(int hp, int energy,
        string? prismMove = "RADIATE_MOVE", int count = 1, int damage = 20)
    {
        var party = Enumerable.Range(0, count).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, i + 1, 73))).ToArray();
        var c = new CombatState(runState: RunState.CreateForTest(party, ascensionLevel: 10, seed: "POST-DECISION-POTION"));
        foreach (var p in party)
        {
            p.MaxEnergy = 3; p.ResetCombatState(); c.AddPlayer(p);
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(p == party[^1] ? hp : 80);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(p == party[^1] ? energy : 0);
        }
        MonsterModel monster = prismMove is null ? ModelDb.Monster<MockAttackMonster>().ToMutable()
            : ModelDb.Monster<InfestedPrism>().ToMutable();
        var e = c.CreateCreature(monster, CombatSide.Enemy, "0"); c.AddCreature(e); monster.SetUpForCombat();
        e.SetMaxHpInternal(820); e.SetCurrentHpInternal(820);
        monster.SetMoveImmediate((MoveState)monster.MoveStateMachine!.States[prismMove ?? "ATTACK"], true);
        if (prismMove is not null)
        {
            ModelDb.Power<VitalSparkPower>().ToMutable().ApplyInternal(e, 3, true);
            ModelDb.Power<WeakPower>().ToMutable().ApplyInternal(e, 2, true);
        }
        else if (damage > 1) ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(e, damage - 1, true);
        return (party, c, e);
    }
    private static CardModel Hand<T>(CombatState c, Player p, bool tainted = true) where T : CardModel
    {
        var card = c.CreateCard<T>(p); p.PlayerCombatState!.Hand.AddInternal(card);
        if (tainted && card.Type == CardType.Skill) card.AfflictInternal(ModelDb.Affliction<Tainted>().ToMutable(), 3);
        return card;
    }
    private static PotionModel Bottle<T>(Player p) where T : PotionModel
    { var q = ModelDb.Potion<T>().ToMutable(); p.AddPotionInternal(q, silent: true); return q; }
    private static KernelTeamSearch.Action[] Script(CombatState c, Player p, params CardModel[] cards)
    {
        var root = KernelSession.Capture(c);
        var inputs = cards.Select(card => new KernelTeamSearch.Action(p, card, root.Targets(card).FirstOrDefault()))
            .Concat(c.Players.Select(owner => new KernelTeamSearch.Action(owner, null, null, EndTurn: true)));
        return TurnStartFixtureCapture.Record(root, inputs);
    }
    private static TerminalRecord Forecast(Player[] party, bool cutoff = false, bool win = false)
        => new(win, cutoff ? EvidenceKind.EstimatedCutoff : EvidenceKind.VerifiedTerminal,
            cutoff ? "fixture-incomplete-comparison" : "", party.Select(_ => 0).ToArray(),
            cutoff ? 0 : party.Length, 0, RoundsSurvived: 5);
    private static KernelCombatPlanner Cached(CombatState c, KernelTeamSearch.Action[] script, TerminalRecord forecast)
    {
        var planner = new KernelCombatPlanner();
        var result = new TournamentResult(script[0], forecast, [], 1, 0, "fixture") {
            Script = script, ScriptAliveStates = script.Select(_ => c.Players.Select(p => p.NetId).Order().ToArray()).ToArray(),
            ScriptEnemyStates = script.Select(_ => c.Enemies.ToArray()).ToArray(), JointNodes = 1 };
        AccessTools.Method(typeof(KernelCombatPlanner), "AdoptTournamentScript").Invoke(planner, [result, c]);
        AccessTools.Field(typeof(KernelCombatPlanner), "tournamentScriptIndex").SetValue(planner, 0);
        return planner;
    }
    private static bool Offer(KernelCombatPlanner planner, CombatState c, Player[] party, TerminalRecord forecast,
        KernelTeamSearch.Action[] script)
    {
        for (var tick = 0; tick < 2000; tick++)
        {
            var offered = (bool)AccessTools.Method(typeof(KernelCombatPlanner), "TryPotionOpportunity")
                .Invoke(planner, [c, party, forecast, script])!;
            if (offered) return true;
            if (!Field<bool>(planner, "emergencyPotionPending")) return false;
        }
        throw new InvalidOperationException("Post-decision potion: bounded rescue must finish");
    }
    private static KernelSession Replay(KernelSession root, IReadOnlyList<KernelTeamSearch.Action> script)
    {
        var after = root.ForkForReplay();
        // This damage witness ends at the enemy phase, while the cached plan
        // records a full next-turn transition. Record this witness's own horizon.
        foreach (var action in TurnStartFixtureCapture.Record(root, script, maxRounds: 1))
        {
            if (after.HasTerminal || after.EnemyPhaseCompleted) break;
            Check(after.ReplayPlanned(action, 1, out var why), "native witness must replay: " + why);
        }
        return after;
    }

    private static void CachedDex()
    {
        var (party, c, _) = Board(6, 1, count: 4); var p = party[^1];
        var defend = Hand<DefendSilent>(c, p); var q = Bottle<DexterityPotion>(p);
        var script = Script(c, p, defend); var root = KernelSession.Capture(c);
        Check(root.CurrentAttacks().Single().Raw == 13 && AscensionHelper.HasAscension(AscensionLevel.DeadlyEnemies),
            "native cached-Dex fixture must preserve A10 Radiate13");
        var native = Replay(root, script);
        Check(native.Hp(p.Creature) == 0, "native 6HP Defend must die to weak Radiate with Tainted3");
        var forecast = Forecast(party); var planner = Cached(c, script, forecast);
        // Drive the real tournament entry so sliced pending work cannot be
        // silently abandoned in favor of emitting the cached losing card.
        for (var tick = 0; tick < 2000 && planner.ConfirmedPotion is null; tick++)
        {
            var outcome = AccessTools.Method(typeof(KernelCombatPlanner), "TryTournamentDecision")
                .Invoke(planner, [c, party, true, "CACHED-DEX-ENTRY", null]);
            if (outcome?.ToString() == "Decided") break;
        }
        Check(planner.ConfirmedPotion?.Potion == q, "cached WIPE must offer the native 6HP Dexterity rescue before its next card");
        var repaired = Field<IReadOnlyList<KernelTeamSearch.Action>>(planner, "tournamentScript");
        var after = Replay(root, repaired);
        Check(after.Hp(p.Creature) == 1 && repaired.Any(a => a.Card == defend),
            "Dex rescue must retain the native Defend continuation and finish Radiate at 1HP");
        Check(p.Creature.CurrentHp == 6 && p.Potions.Contains(q) && p.PlayerCombatState!.Energy == 1,
            "rescue trial must isolate live HP, inventory and energy");
    }
    private static void CutoffEnergy()
    {
        var (party, c, _) = Board(6, 0); var p = party[0];
        Hand<DefendSilent>(c, p); Hand<DefendSilent>(c, p); var q = Bottle<EnergyPotion>(p);
        var script = Script(c, p); var forecast = Forecast(party, cutoff: true); var planner = Cached(c, script, forecast);
        Check(Offer(planner, c, party, forecast, script) && planner.ConfirmedPotion?.Potion == q,
            "incomplete compared plan must not suppress a native Energy rescue");
        var repaired = Field<IReadOnlyList<KernelTeamSearch.Action>>(planner, "tournamentScript");
        Check(repaired.Count(a => a.Card is DefendSilent) == 2
            && Replay(KernelSession.Capture(c), repaired).Hp(p.Creature) == 2,
            "Energy rescue must replace the losing EndTurn with both payable native Defends");
    }
    private static void TaintedMultiHit()
    {
        var (party, c, _) = Board(12, 1, "WHIRLWIND_MOVE"); var p = party[0];
        var a = Hand<DefendSilent>(c, p); Hand<DefendSilent>(c, p); Hand<DefendSilent>(c, p);
        Bottle<EnergyPotion>(p); var script = Script(c, p, a); var forecast = Forecast(party);
        var planner = Cached(c, script, forecast);
        Check(!Offer(planner, c, party, forecast, script) && planner.ConfirmedPotion is null,
            "extra Energy must not be consumed when Tainted repeats make every native defensive line fatal");
        Check(planner.HasPendingStep, "failed rescue must preserve the cached line");
    }
    private static void Frail()
    {
        var (party, c, _) = Board(6, 1); var p = party[0];
        var a = Hand<DefendSilent>(c, p); Bottle<DexterityPotion>(p);
        ModelDb.Power<FrailPower>().ToMutable().ApplyInternal(p.Creature, 1, true);
        var script = Script(c, p, a); var forecast = Forecast(party);
        Check(!Offer(Cached(c, script, forecast), c, party, forecast, script),
            "positive Dex block under Frail must not be mistaken for a native surviving boundary");
    }
    private static void HpCost()
    {
        var (other, d, _) = Board(3, 0, prismMove: null, damage: 20); var owner = other[0];
        Hand<Bloodletting>(d, owner, tainted: false); Hand<DefendIronclad>(d, owner, tainted: false);
        Bottle<DexterityPotion>(owner); var ending = Script(d, owner); var loss = Forecast(other);
        Check(!Offer(Cached(d, ending, loss), d, other, loss, ending),
            "self-lethal Bloodletting must not manufacture a rescuable paid defense");
    }
    private static void ExtraTurn()
    {
        var (party, c, _) = Board(1, 0, prismMove: null, count: 2, damage: 100);
        party[0].Creature.SetCurrentHpInternal(2); var q = Bottle<Ambergris>(party[^1]);
        var script = Script(c, party[^1]); var forecast = Forecast(party); var planner = Cached(c, script, forecast);
        Check(Offer(planner, c, party, forecast, script) && planner.ConfirmedPotion?.Potion == q,
            "native Ambergris extra turn must rescue the whole party by skipping the enemy side");
        var after = Replay(KernelSession.Capture(c), Field<IReadOnlyList<KernelTeamSearch.Action>>(planner, "tournamentScript"));
        Check(party.All(p => after.Hp(p.Creature) > 0) && after.EnemyPhaseCompleted,
            "extra-turn rescue must settle its native side boundary without fabricating an enemy attack");
    }
    private static void HighIncoming()
    {
        var (party, c, _) = Board(50, 1, prismMove: null, damage: 60); var p = party[0];
        var a = Hand<DefendIronclad>(c, p, tainted: false); var q = Bottle<BlockPotion>(p);
        var script = Script(c, p, a); var forecast = Forecast(party, cutoff: true); var planner = Cached(c, script, forecast);
        Check(Offer(planner, c, party, forecast, script) && planner.ConfirmedPotion?.Potion == q,
            "current high native incoming must trigger rescue even above the 30 percent cheap HP guard");
        Check(Replay(KernelSession.Capture(c), Field<IReadOnlyList<KernelTeamSearch.Action>>(planner, "tournamentScript")).Hp(p.Creature) > 0,
            "high-incoming potion witness must survive its real native enemy phase");
    }
    private static void NoRescueCache()
    {
        var (party, c, _) = Board(6, 1, prismMove: null, damage: 20); var p = party[0];
        var a = Hand<DefendSilent>(c, p, tainted: false); Bottle<DexterityPotion>(p);
        var script = Script(c, p, a); var forecast = Forecast(party); var planner = Cached(c, script, forecast);
        var cached = Field<IReadOnlyList<KernelTeamSearch.Action>>(planner, "tournamentScript");
        Check(!Offer(planner, c, party, forecast, script), "fatal Dex trial must not blindly spend the bottle");
        var captures = Field<int>(planner, "emergencyPotionCaptures"); var actions = Field<int>(planner, "emergencyPotionActions");
        for (var i = 0; i < 100; i++) Check(!Offer(planner, c, party, forecast, script), "same-state no-rescue cache must remain negative");
        Check(captures == 1 && Field<int>(planner, "emergencyPotionCaptures") == captures
            && Field<int>(planner, "emergencyPotionActions") == actions && ReferenceEquals(cached,
                Field<IReadOnlyList<KernelTeamSearch.Action>>(planner, "tournamentScript")),
            "no-rescue cache must avoid recapture/native replay while retaining the old script");
        c.RunState.Rng.CombatTargets.NextInt(0, 2);
        Check(!Offer(planner, c, party, forecast, script)
            && Field<int>(planner, "emergencyPotionCaptures") == captures + 1,
            "combat RNG movement must invalidate an otherwise unchanged no-rescue cache");
        p.Potions.Single().DynamicVars.Values.First().BaseValue += 1;
        Check(!Offer(planner, c, party, forecast, script)
            && Field<int>(planner, "emergencyPotionCaptures") == captures + 2,
            "changed native potion dose must invalidate an otherwise unchanged no-rescue cache");
        var different = Script(c, p);
        Check(!Offer(planner, c, party, forecast, different)
            && Field<int>(planner, "emergencyPotionCaptures") == captures + 3,
            "a different compared action tail must invalidate the same-board no-rescue cache");
    }
    private static void WinningCache()
    {
        var (party, c, _) = Board(6, 1); var p = party[0];
        var a = Hand<DefendSilent>(c, p); Bottle<DexterityPotion>(p);
        var script = Script(c, p, a); var forecast = Forecast(party, win: true); var planner = Cached(c, script, forecast);
        Check(!Offer(planner, c, party, forecast, script) && planner.HasPendingStep
            && Field<int>(planner, "emergencyPotionCaptures") == 0,
            "verified victory must keep its modeled bottle and cached fast path");
    }
    private static void WitnessBounds()
    {
        var (party, c, _) = Board(6, 1);
        var witness = new KernelPotionOpportunity.CurrentTurnWitness(KernelSession.Capture(c).ForkForReplay(), party, party, RolloutPolicyKind.Defend);
        var options = Field<RolloutOptions>(Field<RolloutRun>(witness, "run"), "o");
        Check(options.MaxSteps == 48 && options.MaxRounds == 1 && !options.InvestmentLookahead && !options.SequenceLookahead,
            "production rescue witness must lock one native side, finite steps and disable whole-fight investment/order probes");
        while (!witness.Done) witness.Step();
        Check(witness.State.HasTerminal || witness.State.EnemyPhaseCompleted,
            "bounded witness must expose an actual native settled boundary");
    }
    private static void NoPotionRepair()
    {
        var (party, c, _) = Board(6, 1, prismMove: null, damage: 8); var p = party[0];
        var defend = Hand<DefendSilent>(c, p, tainted: false); Bottle<DexterityPotion>(p);
        var script = Script(c, p); var forecast = Forecast(party); var planner = Cached(c, script, forecast);
        Check(Replay(KernelSession.Capture(c), script).Hp(p.Creature) == 0, "unrepaired EndTurn fixture must be fatal");
        Check(!Offer(planner, c, party, forecast, script) && planner.ConfirmedPotion is null,
            "a native surviving no-potion continuation must retain the bottle");
        var repair = Field<IReadOnlyList<KernelTeamSearch.Action>>(planner, "tournamentScript");
        Check(repair.Any(a => a.Card == defend) && Replay(KernelSession.Capture(c), repair).Hp(p.Creature) == 3,
            "saving the bottle must install the native surviving no-potion cards instead of continuing the fatal cache");
    }
    private static void StaleRescue()
    {
        var (party, c, _) = Board(6, 1); var p = party[0];
        var defend = Hand<DefendSilent>(c, p); Bottle<DexterityPotion>(p);
        var script = Script(c, p, defend); var planner = Cached(c, script, Forecast(party));
        var cached = Field<IReadOnlyList<KernelTeamSearch.Action>>(planner, "tournamentScript");
        var root = KernelSession.Capture(c);
        var iterator = ((IEnumerable<bool>)AccessTools.Method(typeof(KernelCombatPlanner), "EmergencyPotionComparisons")
            .Invoke(planner, [root, party, script])!).GetEnumerator();
        Check(iterator.MoveNext(), "stale rescue fixture must have a real native action suspended between slices");
        var stamp = AccessTools.Method(typeof(KernelCombatPlanner), "EmergencyPotionStamp").Invoke(planner, [c, party]);
        foreach (var (name, value) in new (string, object?)[] {
            ("emergencyPotionTrials", iterator), ("emergencyPotionCombat", c), ("emergencyPotionActors", party),
            ("emergencyPotionStamp", stamp), ("emergencyPotionPending", true) })
            AccessTools.Field(typeof(KernelCombatPlanner), name).SetValue(planner, value);
        c.RunState.Rng.CombatTargets.NextInt(0, 2);
        for (var tick = 0; tick < 2000 && Field<bool>(planner, "emergencyPotionPending"); tick++)
            AccessTools.Method(typeof(KernelCombatPlanner), "ResumeEmergencyPotion").Invoke(planner, [c]);
        Check(!Field<bool>(planner, "emergencyPotionPending") && planner.ConfirmedPotion is null
            && ReferenceEquals(cached, Field<IReadOnlyList<KernelTeamSearch.Action>>(planner, "tournamentScript")),
            "a changed full live stamp must cancel sliced rescue before potion or stale continuation adoption");
    }
    private static void SeatSwap()
    {
        var (party, c, _) = Board(3, 0, prismMove: null, count: 3, damage: 5);
        party[0].Creature.SetCurrentHpInternal(3); party[1].Creature.SetCurrentHpInternal(4);
        party[0].PlayerCombatState!.GainEnergy(1); party[1].PlayerCombatState!.GainEnergy(1);
        var a = Hand<DefendSilent>(c, party[0], tainted: false);
        var b = Hand<DefendSilent>(c, party[1], tainted: false);
        Hand<Bloodletting>(c, party[0], tainted: false);
        var q = Bottle<BlockPotion>(party[2]); var root = KernelSession.Capture(c);
        var original = Script(c, party[0], a); var originalEnd = Replay(root, original);
        var alternate = Script(c, party[1], b); var alternateEnd = Replay(root, alternate);
        var candidate = Replay(root, new[] { new KernelTeamSearch.Action(party[2], null, party[2].Creature, q) }.Concat(alternate).ToArray());
        Check(originalEnd.Hp(party[0].Creature) == 3 && alternateEnd.Hp(party[0].Creature) == 0
            && alternateEnd.Hp(party[1].Creature) == 4 && candidate.Hp(party[2].Creature) == 3,
            "seat-swap counterfactuals must be actual native original/alternate/potion enemy boundaries");
        Check((bool)AccessTools.Method(typeof(KernelCombatPlanner), "BetterEmergencyBoundary")
                .Invoke(null, [alternateEnd, originalEnd, party])!, "higher same-survivor-count HP must expose the alternate-baseline comparison");
        Check(!(bool)AccessTools.Method(typeof(KernelCombatPlanner), "RescuesEmergencyBoundary")
                .Invoke(null, [originalEnd, alternateEnd, candidate, party])!,
            "rescue must also preserve original cached survivors after the no-potion baseline changes seats");
        var compare = AccessTools.Method(typeof(KernelCombatPlanner), "EmergencyPotionComparisons");
        var generated = compare.GetCustomAttributes(typeof(System.Runtime.CompilerServices.IteratorStateMachineAttribute), false)
            .Cast<System.Runtime.CompilerServices.IteratorStateMachineAttribute>().Single().StateMachineType;
        var il = AccessTools.Method(generated, "MoveNext").GetMethodBody()!.GetILAsByteArray()!;
        var token = BitConverter.GetBytes(AccessTools.Method(typeof(KernelCombatPlanner), "RescuesEmergencyBoundary").MetadataToken);
        Check(Enumerable.Range(0, il.Length - 3).Any(i => il.AsSpan(i, 4).SequenceEqual(token)),
            "production potion iterator must call the original-survivor rescue predicate");
        var forecast = Forecast(party); var planner = Cached(c, original, forecast);
        // The normal policies may find a better line holding Bloodletting.
        // Whichever witness they choose, adoption must retain the cached survivor.
        if (Offer(planner, c, party, forecast, original))
            Check(Replay(root, Field<IReadOnlyList<KernelTeamSearch.Action>>(planner, "tournamentScript")).Hp(party[0].Creature) > 0,
                "production potion comparison must preserve the original surviving seat in its adopted native witness");
    }
}
