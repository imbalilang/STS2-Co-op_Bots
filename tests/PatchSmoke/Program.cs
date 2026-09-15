using System.Reflection;
using CoopBots;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models.Cards;

try
{
var harmony = new Harmony("cn.xiwa.sts2.coopbots.smoke");
LobbyBotService.ValidateRuntimeSchema();
harmony.PatchAll(typeof(ModEntry).Assembly);

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
    "RestSiteSynchronizer.BeginRestSite",
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

BuildValueScenarios.Run();
Console.WriteLine($"PASS: {patched.Count} Harmony patches applied; bot IDs and Genius tactical probes verified.");
foreach (var method in patched)
    Console.WriteLine($"  {method.DeclaringType?.FullName}.{method.Name}");
// Off by default: the deck tools are a tuning and validation loop, not a release
// gate. See DraftSimScenarios and CommunityValidationScenarios for the flags.
DraftSimScenarios.Run(args);
CommunityValidationScenarios.Run(args);
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}

