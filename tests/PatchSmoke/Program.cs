using System.Reflection;
using CoopBots;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models.Cards;

try
{
// Match the real game's SOS value before any SmartFormat pool is constructed.
SmartFormat.Core.Settings.SmartSettings.IsThreadSafeMode = true;
var harmony = new Harmony("cn.xiwa.sts2.coopbots.smoke");
LobbyBotService.ValidateRuntimeSchema();
harmony.PatchAll(typeof(ModEntry).Assembly);

if (args.Contains("--community-package-only"))
{
    CommunityPackageScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--community-package-case="))?.Split('=', 2)[1]);
    return;
}

if (args.Contains("--expiry-only") || args.Contains("--expiry-record"))
{
    ExpiryValueScenarios.Run(args.Contains("--expiry-record"));
    return;
}

if (args.Contains("--party-setup-pending-only"))
{
    PartySetupPendingNativeScenarios.Run(args.FirstOrDefault(argument => argument.StartsWith("--party-setup-pending-case="))?.Split('=', 2)[1]); return;
}

if (args.Contains("--turn-start-choice-only"))
{
    TurnStartChoiceContractScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--turn-start-choice-case="))?.Split('=', 2)[1]); return;
}

if (args.Contains("--touch-choice-only"))
{
    if(args.Contains("--touch-choice-case=f28"))
        KernelTouchPotionScenarios.RunF28(args.Single(a=>a.StartsWith("--entry=")).Split('=',2)[1]);
    else KernelTouchPotionScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--touch-choice-case="))?.Split('=', 2)[1]); return;
}

if (args.Contains("--smartformat-parser-only"))
{
    SmartFormatParserConcurrencyScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--smartformat-parser-case="))?.Split('=', 2)[1], installGuard: false); return;
}
if (args.Length == 0) SmartFormatParserConcurrencyScenarios.Run(installGuard: false);
if (args.Contains("--pending-power-turn-only"))
{
    PendingPowerTurnBoundaryScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--pending-power-turn-case="))?.Split('=', 2)[1]); return;
}
if (args.Contains("--choice-budget-only"))
{
    ChoiceContinuationBudgetScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--choice-budget-case="))?.Split('=', 2)[1]); return;
}
if (args.Contains("--tournament-diagnostics-only"))
{
    TournamentDiagnosticsScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--tournament-diagnostics-case="))?.Split('=', 2)[1]); return;
}
if (args.Contains("--native-card-scope-only"))
{
    SzgNativeCardScopeScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--native-card-scope-case="))?.Split('=', 2)[1]); return;
}
if (args.Contains("--native-lifetime-only")) { NativeLifetimeScenarios.Run(); return; }
if (args.Contains("--request-lifecycle-only"))
{
    KernelRequestLifecycleScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--request-lifecycle-case="))?.Split('=', 2)[1]); return;
}
if (args.Contains("--planning-state-only"))
{
    KernelPlanningStateScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--planning-state-case="))?.Split('=', 2)[1]); return;
}
if (args.Contains("--planning-facts-only"))
{
    KernelPlanningFactsScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--planning-facts-case="))?.Split('=', 2)[1]); return;
}
if (args.Contains("--mixed-input-only"))
{
    MixedInputLifecycleScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--mixed-input-case="))?.Split('=', 2)[1]); return;
}
if (args.Contains("--planner-request-only"))
{
    KernelPlannerRequestScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--planner-request-case="))?.Split('=', 2)[1]); return;
}
if (args.Contains("--refinement-contract-only"))
{
    KernelRefinementContractScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--refinement-contract-case="))?.Split('=', 2)[1]); return;
}
if (args.Contains("--construction-calibration-only"))
{
    ConstructionNativeCalibrationScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--construction-calibration-case="))?.Split('=', 2)[1]); return;
}
if (args.Contains("--szg-choice-only"))
{
    SzgChoiceRepairScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--szg-choice-case="))?.Split('=', 2)[1]); return;
}
if (args.Contains("--szg-combat-only"))
{
    SzgCombatRepairScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--szg-combat-case="))?.Split('=', 2)[1]); return;
}
if (args.Contains("--attack-gain-array-only")) { AttackGainArrayScenarios.Run(); return; }
if (args.Contains("--attack-gain-array-isolated-only")) { AttackGainArrayScenarios.RunIsolated(); return; }
if (args.Contains("--potion-round-only"))
{
    PotionRoundRepairScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--potion-round-case="))?.Split('=', 2)[1]); return;
}
if (args.Contains("--empty-exhaust-opportunity-only")) { EmptyExhaustOpportunityScenarios.Run(); return; }
if (args.Contains("--empty-exhaust-opportunity-isolated-only"))
{
    NativeCorpusIsolation.Run("--empty-exhaust-opportunity-only", "Isolated exhaust opportunity corpus"); return;
}
if (args.Contains("--owned-card-reference-only")) { OwnedCardReferenceScenarios.Run(); return; }
if (args.Contains("--typed-risk-coverage-only")) { TypedRiskCoverageScenarios.Run(); return; }
if (args.Contains("--szg-build-only")) { SzgBuildRepairScenarios.Run(); return; }
if (args.Contains("--szg-model-only"))
{
    SzgModelRepairScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--szg-model-case="))?.Split('=', 2)[1]); return;
}

if (args.FirstOrDefault(a=>a.StartsWith("--construction-performance-record=")) is { } snapshotPath)
{
    TestEnvironment.Ensure();
    File.WriteAllText(snapshotPath.Split('=',2)[1],System.Text.Json.JsonSerializer.Serialize(ConstructionPerformanceScenarios.Measure(),new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
    return;
}
if (args.FirstOrDefault(a=>a.StartsWith("--construction-parallel-bench=")) is { } parallelOutput)
{
    TestEnvironment.Ensure(); ConstructionParallelScenarios.Benchmark(parallelOutput.Split('=',2)[1]); return;
}
if (args.FirstOrDefault(a=>a.StartsWith("--survivor-audit=")) is { } survivorInput)
{
    TestEnvironment.Ensure(); ConstructionParallelScenarios.SurvivorAudit(survivorInput.Split('=',2)[1]); return;
}
if (args.Contains("--canonical-event-only"))
{
    TestEnvironment.Ensure(); ConstructionParallelScenarios.CanonicalEventReward(); return;
}
if (args.Contains("--resource-startup-only"))
{
    TestEnvironment.Ensure(); ResourceStartupScenarios.Run(); return;
}
if (args.Contains("--sandpit-victory-probe"))
{
    TestEnvironment.Ensure(); SandpitVictoryProbe.Run(); return;
}
if (args.Contains("--treasure-planning-only"))
{
    TestEnvironment.Ensure(); TreasurePlanningScenarios.Run(); return;
}
if (args.Contains("--construction-parallel-only"))
{
    TestEnvironment.Ensure(); ConstructionParallelScenarios.Run(); return;
}
if (args.Contains("--shared-event-only"))
{
    TestEnvironment.Ensure(); SharedEventScenarios.Run(); return;
}
if (args.Contains("--slippery-bridge-only")) { TestEnvironment.Ensure(); SlipperyBridgeScenarios.Run(); return; }
if (args.Contains("--potion-value-only")) { TestEnvironment.Ensure(); PotionValueScenarios.Run(); return; }
if (args.Contains("--act2-priority-only")) { ActTwoPriorityScenarios.Run(); return; }
if (args.Contains("--act2-decisions-only")) { ActTwoDecisionScenarios.Run(); return; }
if (args.Contains("--enchantment-priorities-only")) { EnchantmentPriorityScenarios.Run(); return; }
if (args.Contains("--night-build-only")) { NightDecisionScenarios.Building(); return; }
if (args.Contains("--night-combat-only")) { NightDecisionScenarios.Combat(); return; }
if (args.Contains("--night-refinement-only")) { NightDecisionScenarios.Refinement(); return; }
if (args.Contains("--kernel-consistency-only")) { TestEnvironment.Ensure(); KernelConsistencyScenarios.Run(); return; }
if (args.Contains("--night-hold-only")) { NightDecisionScenarios.Initialize(); KernelRolloutScenarios.HoldOptionAppearsForLowThreatExhaust(); return; }
if (args.Contains("--transform-events-only"))
{
    TestEnvironment.Ensure(); TransformEventScenarios.Run(); NightDecisionScenarios.Building(); return;
}
if (args.Contains("--wood-generic-audit"))
{
    TestEnvironment.Ensure(); WoodGenericAudit.Run(); return;
}
if (args.Contains("--room-work-only"))
{
    TestEnvironment.Ensure(); RoomWorkScenarios.Run(); return;
}
if (args.Contains("--shop-shortcuts-only"))
{
    TestEnvironment.Ensure(); ShopShortcutScenarios.Run(); return;
}
if (args.Contains("--owner-policy-only")) { TestEnvironment.Ensure(); OwnerPolicyScenarios.Run(); return; }
if (args.Contains("--decision-victory-only")) { DecisionConsistencyScenarios.RunVictory(); return; }
if (args.Contains("--winning-plan-only")) { TestEnvironment.Ensure(); WinningPlanScenarios.Run(); return; }
if (args.Contains("--decision-consistency-only")) { DecisionConsistencyScenarios.Run(); return; }
if (args.Contains("--latest-policy-repairs-only")) { LatestPolicyRepairScenarios.Run(); return; }
if (args.Contains("--latest-choice-divergence-only")) { LatestChoiceDivergenceScenarios.Run(); return; }
if (args.Contains("--latest-combat-repair-only")) { LatestCombatRepairScenarios.Run(); return; }
if (args.Contains("--latest-run-search-only"))
{
    LatestRunSearchRepairScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--latest-run-search-case="))?.Split('=', 2)[1]); return;
}
if (args.Contains("--latest-run-choice-only"))
{
    LatestRunChoiceRepairScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--latest-run-choice-case="))?.Split('=', 2)[1]); return;
}
if (args.Contains("--latest-run-model-only"))
{
    LatestRunModelRepairScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--latest-run-model-case="))?.Split('=', 2)[1]); return;
}
if (args.Contains("--latest-run-build-only"))
{
    LatestRunBuildRepairScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--latest-run-build-case="))?.Split('=', 2)[1]); return;
}
if (args.Contains("--latest-projection-repair-only")) { LatestProjectionRepairScenarios.Run(); return; }
if (args.Contains("--post-decision-search-only"))
{
    PostDecisionSearchScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--post-decision-search-case="))?.Split('=', 2)[1]); return;
}
if (args.Contains("--post-decision-potion-only"))
{
    PostDecisionPotionScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--post-decision-potion-case="))?.Split('=', 2)[1]); return;
}
if (args.Contains("--post-decision-choice-only"))
{
    PostDecisionChoiceScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--post-decision-choice-case="))?.Split('=', 2)[1]); return;
}
if (args.Contains("--post-decision-projection-only"))
{
    PostDecisionProjectionScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--post-decision-projection="))?.Split('=', 2)[1]); return;
}
if (args.Contains("--temporary-stats-combat-only"))
{
    TemporaryStatsCombatScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--temporary-stats-combat-case="))?.Split('=', 2)[1]); return;
}
if (args.Contains("--temporary-stats-build-only"))
{
    TemporaryStatsBuildScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--temporary-stats-build-case="))?.Split('=', 2)[1]); return;
}
if (args.Contains("--latest-build-repair-only"))
{
    LatestBuildRepairScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--latest-build-case="))?.Split('=', 2)[1]); return;
}
if (args.Contains("--construction-performance-only"))
{
    TestEnvironment.Ensure(); ConstructionPerformanceScenarios.Run(); return;
}

if (args.Contains("--prepared-anticipate-audit"))
{
    TestEnvironment.Ensure(); PreparedAnticipateAudit.Run(args.FirstOrDefault(a=>a.StartsWith("--prepared-audit-output="))?.Split('=',2)[1]); return;
}
if (args.Contains("--robustness-only"))
{
    TestEnvironment.Ensure(); RobustnessScenarios.Run(); return;
}
if (args.Contains("--shared-status-only"))
{
    TestEnvironment.Ensure(); SharedStatusScenarios.Run(); return;
}
if (args.FirstOrDefault(a=>a.StartsWith("--shop-prior-audit=")) is { } shopPrior)
{
    TestEnvironment.Ensure(); DiscountRemovalAudit.ShopPriorAudit(double.Parse(shopPrior.Split('=',2)[1],System.Globalization.CultureInfo.InvariantCulture)); return;
}
if (args.Contains("--shop-removal-anchor"))
{
    TestEnvironment.Ensure(); ShopRemovalAnchorScenarios.Run(); return;
}
if (args.Contains("--shop-removal-audit"))
{
    TestEnvironment.Ensure(); ShopRemovalAnchorScenarios.Audit(); return;
}
if (args.FirstOrDefault(a=>a.StartsWith("--discount-removal-audit=")) is { } discountOutput)
{
    TestEnvironment.Ensure(); DiscountRemovalAudit.Run(discountOutput.Split('=',2)[1]); return;
}
if (args.Contains("--construction-outcome"))
{
    TestEnvironment.Ensure(); ConstructionOutcomeScenarios.Run(); return;
}
if (args.Contains("--energy-risk-only"))
{
    TestEnvironment.Ensure(); EnergyCommitmentScenarios.Run(); UnifiedDeckStrengthScenarios.Run(); return;
}
if (args.Contains("--bully-price-anchor"))
{
    TestEnvironment.Ensure(); BullyPriceAnchorScenarios.Run(args.Contains("--starter-only"), args.FirstOrDefault(a=>a.StartsWith("--anchor-output="))?.Split('=',2)[1]); return;
}
if (args.Contains("--unified-community"))
{
    TestEnvironment.Ensure(); UnifiedCommunityRegression.Run(args); return;
}
if (args.Contains("--unified-audit"))
{
    TestEnvironment.Ensure();
    var failures = new List<string>();
    foreach (var name in new[] { "UnifiedDeckStrengthScenarios", "BuildValueScenarios", "BuildingPolicyScenarios", "CommunityDraftScenarios", "ShopScenarios", "RestSiteScenarios", "RestSiteValueScenarios", "BuildingStrengthScenarios", "CounterfactualDraftScenarios", "SustainedBuildingScenarios", "DecisionQualityScenarios", "BossDraftScenarios", "RemovalProbeScenarios", "TeamEconomyScenarios", "DraftSemanticsScenarios", "MechanismCycleScenarios", "PowerPriorityScenarios", "ActOneFeedbackScenarios", "TabletEventScenarios" })
    {
        try { typeof(UnifiedDeckStrengthScenarios).Assembly.GetType(name)!.GetMethod("Run", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null); }
        catch (Exception ex) { failures.Add(name + ": " + (ex.InnerException ?? ex).Message); }
    }
    foreach (var failure in failures) Console.WriteLine("AUDIT FAIL " + failure);
    if (failures.Count > 0) throw new Exception($"Unified audit: {failures.Count} failed suites");
    return;
}
if (args.Contains("--unified-deck-only"))
{
    TestEnvironment.Ensure(); UnifiedDeckStrengthScenarios.Run(); return;
}
if (args.Contains("--shared-event-only"))
{
    TestEnvironment.Ensure(); SharedEventScenarios.Run(); return;
}

if (args.Contains("--decision-quality-only"))
{
    TestEnvironment.Ensure(); DecisionQualityScenarios.Run(); return;
}

if (args.Contains("--act-one-feedback-only"))
{
    TestEnvironment.Ensure(); ActOneFeedbackScenarios.Run(); return;
}

if (args.Contains("--mechanism-cycle-only"))
{
    TestEnvironment.Ensure(); MechanismCycleScenarios.Run(); SustainedBuildingScenarios.Run(); return;
}

if (args.Contains("--joint-diversity-only"))
{
    TestEnvironment.Ensure(); JointDiversityScenarios.Run(); JointSearchScenarios.Run(); return;
}

if (args.Contains("--power-priority-only"))
{
    TestEnvironment.Ensure(); PowerPriorityScenarios.Run(); return;
}
if (args.Contains("--class-powers-only"))
{
    TestEnvironment.Ensure(); ClassPowerScenarios.Run(); return;
}
if (args.Contains("--live-power-only"))
{
    TestEnvironment.Ensure(); LivePowerDecisionScenarios.Run(); return;
}
if (args.Contains("--pael-wing-only"))
{
    TestEnvironment.Ensure(); PaelWingScenarios.Run(); return;
}

if (args.Contains("--generated-potions-only"))
{
    TestEnvironment.Ensure(); GeneratedPotionScenarios.Run(); return;
}

if (args.Contains("--team-order-only"))
{
    TestEnvironment.Ensure(); TeamOrderingScenarios.Run(); return;
}

if (args.Contains("--dynamic-target-only"))
{
    TestEnvironment.Ensure(); DynamicTargetScenarios.Run(); ShivOpportunityScenarios.Run(); AttritionSequenceScenarios.Run(); return;
}

if (args.Contains("--attrition-sequence-only"))
{
    TestEnvironment.Ensure(); AttritionSequenceScenarios.Run(); return;
}

// MEASUREMENT ONLY, not a gate: recompute why the shop deleted a freshly forged +
// enchanted BULLET_TIME (live 2026-09-29). Prints BuildValue.Remove numbers, no PASS line.
if (args.Contains("--removal-probe-only"))
{
    TestEnvironment.Ensure(); RemovalProbeScenarios.Run(); return;
}

// MEASUREMENT ONLY, not a gate: sweep Ironclad/Silent cards and print what
// BuildCoverage.Of claims to read, so the "model says coverage 1.00 but the
// per-event scaling is absent from Facts" cards can be audited.
if (args.Contains("--coverage-probe"))
{
    TestEnvironment.Ensure(); CoverageProbeScenarios.Run(); return;
}

// MEASUREMENT ONLY: rebuild the 2026-10-02 live shop deck and print how the
// coverage fix moves TEAR_ASUNDER's removal value against the shop price bar.
if (args.Contains("--tear-removal-probe"))
{
    TestEnvironment.Ensure(); CoverageProbeScenarios.TearRemoval(); return;
}

// MEASUREMENT ONLY: ask the kernel for a boundary-free branch for every
// Ironclad/Silent card. Cards with none are exactly the set the live
// tournament can never play.
if (args.Contains("--kernel-cards-probe"))
{
    TestEnvironment.Ensure(); CoverageProbeScenarios.KernelCoverage(); return;
}

// MEASUREMENT ONLY: does the tournament path actually apply Nightmare's
// NightmarePower and next-turn copies?
if (args.Contains("--nightmare-probe"))
{
    TestEnvironment.Ensure(); CoverageProbeScenarios.NightmareProbe(); return;
}

// Focused gate for the discard-cleanup policy: ethereal no-downside statuses/curse
// (ASCENDERS_BANE, DAZED) must be left in hand to burn instead of being pitched.
if (args.Contains("--discard-cleanup-only"))
{
    TestEnvironment.Ensure(); DiscardCleanupScenarios.Run(); return;
}

// Focused gate for the live 2026-09-29 ENTOMANCER_ELITE over-estimate: a FLANKING
// (and KNOCKDOWN) multiplier that outlives its own turn doubles every later round
// of a cached script and turns a wipe into a "verified" VICTORY.
if (args.Contains("--flanking-lifetime-only"))
{
    TestEnvironment.Ensure(); FlankingLifetimeScenarios.Run(); return;
}

if (args.Contains("--potion-overlap-only"))
{
    TestEnvironment.Ensure(); PotionOverlapScenarios.Run(); return;
}
if (args.Contains("--combat-quality-only")) { CombatQualityScenarios.Run(); return; }
if (args.Contains("--decision-fixes-only")) { DecisionFixScenarios.Run(); return; }
if (args.Contains("--strategic-lookahead-only")) { StrategicLookaheadScenarios.Run(); return; }
if (args.Contains("--long-value-only")) { LongValueScenarios.Run(); return; }
if (args.Contains("--winning-plan-only"))
{
    TestEnvironment.Ensure(); WinningPlanScenarios.Run(); CombatFeedbackScenarios.Run(); return;
}
if (args.Contains("--ancient-options-only"))
{
    TestEnvironment.Ensure(); AncientOptionScenarios.Run(args.FirstOrDefault(a => a.StartsWith("--ancient-output="))?.Split('=',2)[1],args.Contains("--ancient-checks-only")); return;
}

if (args.Contains("--boss-draft-only"))
{
    TestEnvironment.Ensure(); BossDraftScenarios.Run(); return;
}

if (args.Contains("--attrition-only"))
{
    TestEnvironment.Ensure(); AttritionScenarios.Run(); SustainedBuildingScenarios.Run(); return;
}

if (args.Contains("--poison-potion-only"))
{
    TestEnvironment.Ensure(); PoisonPotionScenarios.Run(); return;
}

if (args.Contains("--draft-semantics-only"))
{
    TestEnvironment.Ensure(); DraftSemanticsScenarios.Run(); return;
}

if (args.Contains("--temporal-only"))
{
    TestEnvironment.Ensure(); TemporalResourceScenarios.Run(); return;
}
if (args.Contains("--cheese-only"))
{
    TestEnvironment.Ensure(); CheeseEventScenarios.Run(); return;
}

// PACKAGING SMOKE. The packager runs THIS instead of the whole suite. The full
// development suite is ~3.7 minutes and is a CALIBRATION gate, not a shipping
// gate: what packaging has to catch is "the artifact is broken". This keeps the
// drift guards, the event/edit regressions, the deck-strength invariants, the
// policy anchors and the valuation smoke, and drops the long construction /
// performance / attrition tail. The full suite stays available as the plain
// no-argument run of this same assembly.
if (args.Contains("--packaging-smoke"))
{
    TestEnvironment.Ensure();
    TransformEventScenarios.Run(); NightDecisionScenarios.Building();
    CooperativeScenarios.Run();
    // CodexCrossCheck is deliberately NOT here: it diffs the baked tables against
    // an EXTERNAL dump, which is a data-pipeline freshness gate, not "the artifact
    // is broken". It stays in the full suite.
    UnifiedDeckStrengthScenarios.Run();
    // The price anchors are policy, not calibration: a build that changes what a
    // removal costs must fail packaging, not just the nightly suite.
    BullyPriceAnchorScenarios.Run();
    ShopRemovalAnchorScenarios.Run();
    BuildValueScenarios.Run();
    SharedEventScenarios.Run();
    Console.WriteLine("PASS: packaging smoke -- drift guards, event edits, deck strength, "
        + "policy anchors, valuation and shared-event voting.");
    return;
}

if (args.Contains("--tablet-only"))
{
    TestEnvironment.Ensure(); TabletEventScenarios.Run(); return;
}

if (args.Contains("--camp-only"))
{
    TestEnvironment.Ensure();
    RestSiteScenarios.Run();
    return;
}

// Permanent deck-edit entry regressions only: drives BotBrain.SelectCards
// against real game models without running the wider patch/combat suite. The
// patches above are still required because the models only load headlessly with
// them applied.
if (args.Contains("--building-edits-only"))
{
BuildValueScenarios.Run();
BuildingPolicyScenarios.Run();
    CommunityDraftScenarios.Run();
    return;
}

// Focused community-Elo draft gate: the Elo-first reward/shop Add policy, the
// directional held-card affinity and the pure-Elo diagnostic baseline.
// TestEnvironment installs the headless model database the scenarios need
// before any model is constructed.
if (args.Contains("--community-draft-only"))
{
    TestEnvironment.Ensure();
    CommunityDraftScenarios.Run();
    return;
}

// Focused building-policy-v2 core gate: mechanism dependencies, continuous
// size relief, structure bottlenecks and contextual upgrades. TestEnvironment
// installs the headless model database the scenarios need before any model is
// constructed; the same Run() is also called in the normal suite below.
if (args.Contains("--building-policy-only"))
{
    TestEnvironment.Ensure();
    BuildingPolicyScenarios.Run();
    CommunityDraftScenarios.Run();
    return;
}

// Focused team-economy gate: the combat-start snapshot, bounded team bonus,
// bounded two-purchase shop comparison and the shared removal plan, followed by
// the existing build/shop/coordinator regressions they touch.
if (args.Contains("--team-economy-only"))
{
    TestEnvironment.Ensure();
    TeamEconomyScenarios.Run();
    BuildValueScenarios.Run();
    BuildingPolicyScenarios.Run();
    ShopScenarios.Run();
    CoordinatorScenarios.Run();
    return;
}

#if KERNEL_TESTS
if (args.Contains("--kernel-rounds-only")) { TestEnvironment.Ensure(); KernelRoundScenarios.Run(); return; }
if (args.Contains("--foreseer-only")) { TestEnvironment.Ensure(); ForeseerScenarios.Run(); return; }
if (args.Contains("--kernel-rollouts-only"))
{
    NightDecisionScenarios.Initialize(); KernelRolloutScenarios.Run(); return;
}
if (args.Contains("--mixed-endturn-wait-only"))
{
    NightDecisionScenarios.Initialize();
    KernelRolloutScenarios.EndTurnStepWaitsForTheHumansInsteadOfDroppingTheScript();
    return;
}
if (args.Contains("--script-cache-only"))
{
    NightDecisionScenarios.Initialize();
    KernelRolloutScenarios.TournamentScriptCacheKeepsTheCachedLine();
    return;
}
if (args.Contains("--strength-only") || args.Contains("--strength-probe"))
{
    TestEnvironment.Ensure();
    new Harmony("coopbots.test.kernel.presentation.strength").Patch(
        AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
        prefix: new HarmonyMethod(typeof(KernelEngineScenarios), "FormatText"));
    if (args.Contains("--strength-probe")) { KernelRolloutScenarios.StrengthProbe(); return; }
    KernelRolloutScenarios.StrengthRegressions();
    BuildingStrengthScenarios.Run();
    return;
}

// Focused power-route backport gate: TestEnvironment installs the headless
// model database/presentation stubs the full suite normally sets up in
// CooperativeScenarios, then KernelEngineScenarios runs
// KernelPowerRouteScenarios plus the existing kernel scenario regressions.
if (args.Contains("--kernel-power-only"))
{
    TestEnvironment.Ensure();
    KernelEngineScenarios.Run();
    return;
}

// MEASUREMENT ONLY, not a gate and not part of any suite: the cost of one joint
// full-bot search in the production opening shape, with the peak managed heap
// sampled rather than inferred from the cumulative allocation number. See
// SearchCostProbe for why the peak is the number that matters on an 8 GB box.
if (args.Contains("--perf-probe"))
{
    TestEnvironment.Ensure();
    if (args.Contains("--list-encounters"))
    {
        SearchCostProbe.ListEncounters(Array.IndexOf(args, "--list-encounters") + 1 < args.Length
            ? args[Array.IndexOf(args, "--list-encounters") + 1] : "");
        return;
    }
    SearchCostProbe.Run(args);
    return;
}
#endif

// Dedicated capture checks: default-off writer, real model serialization and
// the selection linkage. Runs after Harmony setup and does not touch the combat
// suite.
if (args.Contains("--building-capture-only"))
{
    BuildDecisionCaptureScenarios.Run(args);
    return;
}

// Tuning loop only: the patches above are what make the game models loadable
// headlessly, so they are required, but the assertions below are not. This skips
// straight to the deck tools.
if (args.Contains("--deck-sim-only") || args.Contains("--deck-sim-validate"))
{
    DraftSimScenarios.Run(args);
    CommunityValidationScenarios.Run(args);
    return;
}

var patched = Harmony.GetAllPatchedMethods()
    .Where(method => Harmony.GetPatchInfo(method)?.Owners.Contains("cn.xiwa.sts2.coopbots.smoke") == true)
    .OrderBy(method => method.DeclaringType?.FullName)
    .ThenBy(method => method.Name)
    .ToList();

var expected = new[]
{
    "NCharacterSelectScreen.OnSubmenuOpened",
    "NRun._Process",
    "ActionQueueSynchronizer.RequestEnqueue",
    "MapSelectionSynchronizer.PlayerVotedForMapCoord",
    "EventSynchronizer.PlayerVotedForSharedOptionIndex",
    "PlatformUtil.GetPlayerNameRaw",
    "RewardsSet.Offer",
    "PlayerChoiceSynchronizer.WaitForRemoteChoice",
    "RestSiteRoom.EnterInternal",
    "CombatStateSynchronizer.CheckSyncCompleted",
    "CombatManager.SetReadyToBeginEnemyTurn",
    "ActChangeSynchronizer.OnPlayerReady",
    "LoadRunLobby.AddLocalHostPlayer",
    "LoadRunLobby.HandleClientLoadJoinRequestMessage",
    "RunLobby..ctor",
    "RunLobby.HandleClientRejoinRequestMessage",
};

foreach (var name in expected)
{
    var found = patched.Any(method => $"{method.DeclaringType?.Name}.{method.Name}" == name);
    if (!found)
        throw new InvalidOperationException($"Required patch was not applied: {name}");
}

if (!BotRegistry.IsBot(BotRegistry.CreateId(BotDifficulty.Pro, 2, 3)))
    throw new InvalidOperationException("Bot id round-trip failed.");
if (BotRegistry.Difficulty(BotRegistry.CreateId(BotDifficulty.Pro, 1, 0)) != BotDifficulty.Pro)
    throw new InvalidOperationException("Pro difficulty round-trip failed.");
if (BotRegistry.Difficulty(BotRegistry.CreateId(BotDifficulty.Cheated, 1, 0)) != BotDifficulty.Cheated
    || !BotRegistry.IsCheated(BotRegistry.CreateId(BotDifficulty.Cheated, 1, 0)))
    throw new InvalidOperationException("Cheated difficulty round-trip failed.");
// Old saves encoded Smart=2 / Genius=3 in the same tier field. Both were full
// bots, so they must read back as Pro and never as the new cheated tier.
foreach (var legacyTier in new[] { 2UL, 3UL })
    if (BotRegistry.Difficulty(0xB07B_0000_0000_0000UL | (legacyTier << 16)) != BotDifficulty.Pro)
        throw new InvalidOperationException("A legacy tier id must clamp to Pro.");

var strategyType = typeof(BotBrain).Assembly.GetType("CoopBots.GeniusCombatStrategy")
    ?? throw new InvalidOperationException("Genius strategy type was not found.");
var usefulBlockMethod = strategyType.GetMethod("UsefulBlockForTest", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("Genius useful-block probe was not found.");
double UsefulBlock(double incoming, double currentBlock, double cardBlock)
    => (double)usefulBlockMethod.Invoke(null, new object[] { incoming, currentBlock, cardBlock })!;
if (UsefulBlock(20, 7, 8) != 8 || UsefulBlock(5, 7, 8) != 0)
    throw new InvalidOperationException("Genius intent-aware block calculation failed.");

var analyzeMethod = strategyType.GetMethod("Analyze", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("Genius card-analysis probe was not found.");
object Analyze(object card, int energy = 3, int enemies = 1)
{
    return analyzeMethod.Invoke(null, new[] { card, null, (object)energy, enemies })
        ?? throw new InvalidOperationException("Genius card analysis returned null.");
}
double Fact(object facts, string name)
    => (double)(facts.GetType().GetProperty(name)?.GetValue(facts)
        ?? throw new InvalidOperationException($"Genius card analysis did not expose {name}."));
if (Fact(Analyze(new TwinStrike()), "TotalDamage") != 10)
    throw new InvalidOperationException("Genius strategy did not recognize Twin Strike as a two-hit attack.");
if (Fact(Analyze(new DaggerSpray(), enemies: 2), "TotalDamage") != 16)
    throw new InvalidOperationException("Genius strategy did not value two-hit AOE across both enemies.");
var bashFacts = Analyze(new Bash());
if (Fact(bashFacts, "TotalDamage") != 8 || Fact(bashFacts, "Vulnerable") != 2)
    throw new InvalidOperationException("Genius strategy did not recognize Bash damage plus Vulnerable setup.");
var adrenalineFacts = Analyze(new Adrenaline());
if (Fact(adrenalineFacts, "Draw") != 2 || Fact(adrenalineFacts, "ImmediateEnergy") != 1)
    throw new InvalidOperationException("Genius strategy did not recognize Adrenaline as a draw/energy opener.");
if (Fact(Analyze(new DefendIronclad()), "Block") != 5)
    throw new InvalidOperationException("Genius strategy did not recognize base block.");
if (Fact(Analyze(new Hyperbeam()), "Focus") != 0)
    throw new InvalidOperationException("Genius strategy treated Hyperbeam's Focus loss as a buff.");
// Turn-scoped enemy Strength loss must be a first-class fact, otherwise these
// cards look like no-effect skills and are never played.
if (Fact(Analyze(new PiercingWail()), "StrengthDown") != 6)
    throw new InvalidOperationException("Genius strategy did not recognize Piercing Wail's temporary Strength loss.");
if (Fact(Analyze(new DarkShackles()), "StrengthDown") != 9)
    throw new InvalidOperationException("Genius strategy did not recognize Dark Shackles' Strength loss.");
if (Fact(Analyze(new EnfeeblingTouch()), "StrengthDown") != 8)
    throw new InvalidOperationException("Genius strategy did not recognize Enfeebling Touch's Strength loss.");
if (Fact(Analyze(new DefendIronclad()), "StrengthDown") != 0)
    throw new InvalidOperationException("Genius strategy must not invent Strength loss on a plain block card.");

var gate = new CooperationGate();
var encounter = new object();
gate.Observe(encounter, 1);
if (gate.Paused) throw new Exception("New combat must allow automatic paced actions.");
gate.TogglePause();
if (!gate.Paused) throw new Exception("Explicit pause must take effect.");
gate.Observe(encounter, 2);
if (!gate.Paused) throw new Exception("Round change must preserve pause.");
gate.TogglePause();
if (gate.Paused) throw new Exception("Resume must allow paced actions again.");
gate.TogglePause();
gate.Observe(new object(), 1);
if (gate.Paused) throw new Exception("New combat leaked the previous encounter's pause.");
PacingScenarios.Run();
if (Fact(Analyze(new Vicious()), "Draw") != 0)
    throw new Exception("Conditional Vicious draw must not count as immediate draw.");
// Deliberately above CooperativeScenarios: that one currently fails first, and a check
// registered with the later blocks would never execute in a red suite.
// Initialize the native corpus after bare card-analysis constructors, and
// before native combat scenarios JIT the observed sampling wrappers.
AttackGainArrayScenarios.RunIsolated();
NativeCorpusIsolation.Run("--empty-exhaust-opportunity-only", "Isolated exhaust opportunity corpus");
NativeCorpusIsolation.Run("--potion-round-only", "Isolated potion round repair corpus");
AutoPilotScenarios.Run();
// Which policy decides an all-bot fight (the 0.38.0 switch): tournament by default,
// segmented search on a mixed table, and the override forces either.
ChoicePolicyScenarios.Run();
// The seat question behind the reported card-choice stalls (Survivor, choice potions):
// answered for every seat the bot drives, declined for a seat it does not.
ChoiceSeatScenarios.Run();
// The room continue is enabled before the room is done in two rooms (the shop and the
// terminal reward screen); pressing either early skips work that room still owed the run.
RoomProceedScenarios.Run();
RoomWorkScenarios.Run();
TransformEventScenarios.Run(); NightDecisionScenarios.Building();
CooperativeScenarios.Run();
// Runs before the build-value scenarios: it publishes this build's card ids for
// the bake scripts, and a stale generated table fails the guards below on
// purpose so the table gets regenerated.
CodexCrossCheckScenarios.Run();
// The version reported in the log must come from the shipped manifest. Where the
// manifest is not next to the assembly (this harness) the compiled fallback is
// used, so asserting the fallback catches a manifest bump that forgot the code.
try
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
    {
        var candidate = Path.Combine(directory.FullName, "src", "CoopBots", "mod_manifest.json");
        if (!File.Exists(candidate)) continue;
        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(candidate));
        var manifestVersion = manifest.RootElement.GetProperty("version").GetString();
        if (ModEntry.Version != manifestVersion)
            throw new InvalidOperationException($"CoopBots version mismatch: code reports {ModEntry.Version}, manifest says {manifestVersion}.");
        break;
    }
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
{
    // An unreadable manifest is not a reason to fail the suite.
}

CommunityPackageScenarios.Run();
UnifiedDeckStrengthScenarios.Run();
EnergyCommitmentScenarios.Run();
ConstructionOutcomeScenarios.Run();
RobustnessScenarios.Run();
ConstructionPerformanceScenarios.Run();
ConstructionParallelScenarios.Run();
ResourceStartupScenarios.Run();
TreasurePlanningScenarios.Run();
SharedStatusScenarios.Run();
// Run the remaining regressions even when calibration anchors fail. Keep the
// suite red and report every anchor error at the end; no acceptance is waived.
var policyAnchorFailures = new List<Exception>();
foreach (var anchor in new Action[] { () => BullyPriceAnchorScenarios.Run(), ShopRemovalAnchorScenarios.Run })
{
    try { anchor(); }
    catch (Exception error) { policyAnchorFailures.Add(error); Console.Error.WriteLine("FAILED POLICY ANCHOR: " + error); }
}
OwnerPolicyScenarios.Run();
DecisionConsistencyScenarios.Run();
LatestPolicyRepairScenarios.Run();
LatestChoiceDivergenceScenarios.Run();
LatestCombatRepairScenarios.Run();
LatestRunSearchRepairScenarios.Run();
LatestRunChoiceRepairScenarios.Run();
LatestRunModelRepairScenarios.Run();
SzgChoiceRepairScenarios.Run();
SzgCombatRepairScenarios.Run();
SzgBuildRepairScenarios.Run();
SzgModelRepairScenarios.Run();
SzgNativeCardScopeScenarios.Run();
PendingPowerTurnBoundaryScenarios.Run();
KernelRequestLifecycleScenarios.Run();
ChoiceContinuationBudgetScenarios.Run();
TournamentDiagnosticsScenarios.Run();
KernelPlanningStateScenarios.Run();
KernelPlanningFactsScenarios.Run();
MixedInputLifecycleScenarios.Run();
KernelPlannerRequestScenarios.Run();
KernelRefinementContractScenarios.Run();
RefinementQualificationScenarios.Run();
GeneratedAttackCostScenarios.Run();
KnowledgeConditionalScenarios.Run();
TouchOpportunityScenarios.Run();
HellraiserFactsNativeScenarios.Run("withering");
SlowCounterFactsNativeScenarios.Run();
SlipperyBridgeValueNativeScenarios.Run();
ConstructionNativeCalibrationScenarios.Run();
SzgPotionReferenceRepairScenarios.Run();
OwnedCardReferenceScenarios.Run();
TypedRiskCoverageScenarios.Run();
ExpiryValueScenarios.Run(false);
LatestRunBuildRepairScenarios.Run();
LatestProjectionRepairScenarios.Run();
PostDecisionSearchScenarios.Run();
PostDecisionPotionScenarios.Run();
PostDecisionChoiceScenarios.Run();
PostDecisionProjectionScenarios.Run();
TemporaryStatsCombatScenarios.Run();
TemporaryStatsBuildScenarios.Run();
LatestBuildRepairScenarios.Run();
NativeLifetimeScenarios.Run();
ShopShortcutScenarios.Run();
BuildValueScenarios.Run();
BuildingPolicyScenarios.Run();
CommunityDraftScenarios.Run();
TeamEconomyScenarios.Run();
DraftSemanticsScenarios.Run();
PoisonPotionScenarios.Run();
SharedEventScenarios.Run();
TeamOrderingScenarios.Run();
AttritionScenarios.Run();
DynamicTargetScenarios.Run();
ShivOpportunityScenarios.Run();
FlankingLifetimeScenarios.Run();
MusicBoxForkScenarios.Run();
DiscardCleanupScenarios.Run();
RemovalProbeScenarios.Run();
AttritionSequenceScenarios.Run();
PotionOverlapScenarios.Run();
WinningPlanScenarios.Run();
LongValueScenarios.Run();
    StrategicLookaheadScenarios.Run();
CombatQualityScenarios.Run();
DecisionFixScenarios.Run();
ActTwoPriorityScenarios.Run();
NightDecisionScenarios.Combat();
NightDecisionScenarios.Refinement();
ActTwoDecisionScenarios.Run();
EnchantmentPriorityScenarios.Run();
AncientOptionScenarios.Run();
BossDraftScenarios.Run();
SustainedBuildingScenarios.Run();
MechanismCycleScenarios.Run();
JointDiversityScenarios.Run();
PowerPriorityScenarios.Run();
LivePowerDecisionScenarios.Run();
PaelWingScenarios.Run();
ClassPowerScenarios.Run();
ActOneFeedbackScenarios.Run();
DecisionQualityScenarios.Run();
GeneratedPotionScenarios.Run();
PartySetupPendingNativeScenarios.Run();
Console.WriteLine($"PASS: {patched.Count} Harmony patches applied; bot IDs and Genius tactical probes verified.");
foreach (var method in patched)
    Console.WriteLine($"  {method.DeclaringType?.FullName}.{method.Name}");
// Off by default: the deck tools are a tuning and validation loop, not a release
// gate. See DraftSimScenarios and CommunityValidationScenarios for the flags.
DraftSimScenarios.Run(args);
CommunityValidationScenarios.Run(args);
if (policyAnchorFailures.Count > 0) throw new AggregateException("Policy calibration anchors failed", policyAnchorFailures);
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}
