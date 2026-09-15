using MegaCrit.Sts2.Core.Map;

namespace DeckSim;

/// <summary>
/// The ruleset the draft pipeline plays by. Everything here is either copied from
/// the shipped game (with the decompiled source named in a comment) or is an
/// explicitly labelled modelling choice for the parts the pipeline does not
/// simulate — HP, combat and the map graph.
///
/// Keeping the A10 numbers in one place matters more than it looks: the whole
/// point of the pipeline is to answer "did this parameter change make drafting
/// better", and that answer is only comparable across runs if the pressure the
/// draft is under never moves underneath it.
/// </summary>
internal sealed record SimConfig
{
    /// <summary>Which characters to draft. Any real <c>CharacterModel</c> with a card pool.</summary>
    public string[] Characters { get; init; } = ["Ironclad", "Silent", "Defect"];

    /// <summary>Runs per character. Each run is its own seed, so the whole sweep is fixed.</summary>
    public int RunsPerCharacter { get; init; } = 12;

    /// <summary>Seed of the first run; run N uses <c>Seed + N</c>, so a sweep is reproducible.</summary>
    public ulong Seed { get; init; } = 20260915UL;

    /// <summary>
    /// Ascension 10 is every level 1..10 switched on (AscensionManager.maxAscensionAllowed = 10,
    /// AscensionLevel is a cumulative enum). The levels that touch drafting are applied by
    /// <see cref="Ascension10"/>; the two that only change combat (ToughEnemies, DeadlyEnemies)
    /// reach the pipeline through <see cref="References"/> instead.
    /// </summary>
    public bool Ascension10 { get; init; } = true;

    /// <summary>
    /// Whether a camp is spent on an upgrade. The real choice is heal-versus-smith and is
    /// decided from HP (<c>RestSitePatches.SmithHpFloor</c>) — the one thing this pipeline
    /// deliberately does not model. Rest sites are therefore always smiths, and the deck
    /// trajectory they produce is the "no healing needed" upper bound. The *target* is still
    /// chosen by the production rule, so it is the smith decision under test, not a copy.
    /// </summary>
    public bool SmithAtCamp { get; init; } = true;

    /// <summary>Events that hand over a curse, as a share of all event nodes.</summary>
    public double CurseEventShare { get; init; } = 0.35;

    /// <summary>
    /// Reference fights the deck is scored against, one per act. Damage and HP are
    /// per act; the par values are per character and are filled in by
    /// <see cref="ReferenceFor"/>.
    /// </summary>
    public ReferenceFight[] References { get; init; } = SimConfig.DefaultReferences;

    /// <summary>
    /// What a real Ascension 10 winner of this character produces against the final
    /// act boss. Measured, not assumed — see <see cref="ParByCharacter"/>.
    /// </summary>
    public IReadOnlyDictionary<string, Par> Par { get; init; } = SimConfig.DefaultPar;

    /// <summary>How the three score components are combined.</summary>
    public ScoreWeights Weights { get; init; } = new();

    /// <summary>Turn cap of one scoring play-out. Has to clear the slowest <see cref="References"/> target.</summary>
    public int ScoreTurns { get; init; } = 12;

    /// <summary>Draws (different shuffles of the same deck) averaged per score.</summary>
    public int ScoreShuffles { get; init; } = 8;

    // ---- Reference fights ---------------------------------------------------
    //
    // A deck is scored by playing it out, so it needs something to play against:
    // a pool of HP to chew through and a block demand to survive. These are per
    // act and deliberately blunt — the pipeline is measuring the deck, not the
    // encounter — but they have to be the right order of magnitude or "more
    // damage" and "more block" stop trading off correctly.
    //
    // HP and damage are the shipped act bosses at A10 (ToughEnemies / DeadlyEnemies
    // raise both), read from the monster models. HP: Vantom 183 / Ceremonial Beast
    // 262, Insatiable 341 / Knowledge Demon 399, Queen 419 / Aeonglass 535 — each
    // reference is the middle of its act's range.
    //
    // Damage per turn is the boss's *effective* rate, and it is calibrated against
    // real runs rather than read off the boss's biggest move.
    //
    // The first version used peak turns (act 3 at 32: Aeonglass's Ebb 26 plus Eye
    // Lasers 12). No real winning deck blocks 32 a turn, so every real deck died on
    // turn four and a damage-only draft outscored all of them. Sweeping the number
    // against 69 real A10 winning Ironclad decks (--deck-sim-validate --calibrate)
    // puts their survival at 99% for 8 a turn, 92% at 10, 74% at 12, 49% at 14.
    // Bosses alternate attacks with buff, block and debuff turns, so the honest
    // average is roughly a third of the peak; 11 is where a deck that really won
    // really gets through the fight. Acts 1-2 scale by boss HP, which tracks how
    // much smaller both the fight and the deck still are.
    internal static readonly ReferenceFight[] DefaultReferences =
    [
        new("act 1 boss", EnemyHp: 220, IncomingPerTurn: 6, ParDamagePerTurn: 0, ParBlockPerTurn: 0, ParUpgrades: 0, ParEfficiency: 0, PlayerHp: 75),
        new("act 2 boss", EnemyHp: 370, IncomingPerTurn: 9, ParDamagePerTurn: 0, ParBlockPerTurn: 0, ParUpgrades: 0, ParEfficiency: 0, PlayerHp: 75),
        new("act 3 boss", EnemyHp: 480, IncomingPerTurn: 11, ParDamagePerTurn: 0, ParBlockPerTurn: 0, ParUpgrades: 0, ParEfficiency: 0, PlayerHp: 75),
    ];

    internal static SimConfig Default => new();

    /// <summary>
    /// The fight a deck is scored against for one character and act.
    ///
    /// Act 3 carries the measured numbers; the earlier acts scale the par down by
    /// the act's boss HP, which is a proxy for how much smaller the deck still is.
    /// Without that, an act-1 deck would be judged against a finished deck's output
    /// and every run would look like it was getting better only because it grew.
    /// </summary>
    internal ReferenceFight ReferenceFor(string character, int act)
    {
        var index = Math.Clamp(act, 0, References.Length - 1);
        var reference = References[index];
        var par = Par.TryGetValue(character, out var found) ? found : PooledPar;
        var scale = reference.EnemyHp / References[^1].EnemyHp;
        return reference with
        {
            ParDamagePerTurn = par.Damage * scale,
            ParBlockPerTurn = par.Block * scale,
            // Card-count and efficiency par are per deck, not per turn: a deck is
            // not "less upgraded" for having fewer turns in front of it.
            ParUpgrades = par.Upgrades,
            ParEfficiency = par.Efficiency,
            PlayerHp = PlayerHpFor(character),
        };
    }

    /// <summary>
    /// HP the deck has for this one fight, which is the whole survival budget: the
    /// pipeline models no relics, potions or healing, and those are a real part of
    /// how a run survives a boss. A character's own starting HP is the closest
    /// honest number available for it.
    /// </summary>
    private static double PlayerHpFor(string character)
    {
        try
        {
            var model = MegaCrit.Sts2.Core.Models.ModelDb.AllCharacters
                .FirstOrDefault(candidate => string.Equals(candidate.Id.Entry, character, StringComparison.OrdinalIgnoreCase));
            if (model is not null) return model.StartingHp;
        }
        catch { /* the model database is loaded by the harness; fall through */ }
        return 75;
    }

    /// <summary>Used for characters the community sample is too thin to pin, and as the fallback.</summary>
    internal static readonly Par PooledPar = new(Damage: 18.2, Block: 8.8, Upgrades: 12, Efficiency: 0.90);

    /// <summary>
    /// Par output per character, measured by playing real Ascension 10 winning
    /// decks through <see cref="DeckScorer"/> — regenerate with
    /// <c>--deck-sim-validate --par</c> after fetching more decks.
    ///
    /// These are the numbers a scorer cannot guess: the play-out measures a deck's
    /// output in its own terms, so "enough damage" only means anything next to what
    /// decks that actually won produced. Silent's par is far below Ironclad's, and
    /// a scorer that ignored that would rate every Silent build as failing.
    /// </summary>
    internal static readonly Dictionary<string, Par> DefaultPar = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IRONCLAD"] = new(Damage: 27.0, Block: 6.5, Upgrades: 12, Efficiency: 0.93),
        ["SILENT"] = new(Damage: 19.6, Block: 11.0, Upgrades: 12, Efficiency: 0.91),
        ["DEFECT"] = new(Damage: 19.7, Block: 8.4, Upgrades: 12, Efficiency: 0.83),
        ["NECROBINDER"] = new(Damage: 14.5, Block: 6.9, Upgrades: 12, Efficiency: 0.91),
        ["REGENT"] = new(Damage: 10.4, Block: 11.2, Upgrades: 11, Efficiency: 0.82),
    };

    /// <summary>Short identity of this configuration, used to name report files.</summary>
    public string Label(string prefix)
        => $"{prefix}-{string.Join('+', Characters)}-runs{RunsPerCharacter}-seed{Seed}";
}

/// <summary>
/// What a real winner of a character looks like in the reference fight.
///
/// Damage and block are per turn; <paramref name="Upgrades"/> is how many upgraded
/// cards the finished deck holds and <paramref name="Efficiency"/> is the share of
/// its energy the fight actually spends (1 - waste). All four are medians of real
/// A10 winning decks, and all four are here because the community sample says they
/// separate winners from decks that died to the act-3 boss — see
/// <c>--deck-sim-validate --features</c>. Raw damage is the one that points the
/// wrong way, which is why it is the smallest weight in the score.
/// </summary>
internal sealed record Par(double Damage, double Block, double Upgrades, double Efficiency);

/// <summary>
/// One fight the deck is scored against.
///
/// <paramref name="EnemyHp"/> and <paramref name="IncomingPerTurn"/> describe the
/// fight; <paramref name="ParDamagePerTurn"/> and <paramref name="ParBlockPerTurn"/>
/// describe what winning looks like in it. Offence and defence are both measured
/// against par rather than against absolutes, so a deck is judged by "is this what
/// it takes to win", not "is the number big".
/// </summary>
internal sealed record ReferenceFight(
    string Name,
    double EnemyHp,
    double IncomingPerTurn,
    double ParDamagePerTurn,
    double ParBlockPerTurn,
    double ParUpgrades,
    double ParEfficiency,
    double PlayerHp);

/// <summary>
/// Weights of the composite deck score.
///
/// These are not a taste call any more. Each weight is set from how well the
/// quantity behind it separates real A10 winners from real A10 decks that died to
/// the act-3 boss (<c>--deck-sim-validate --features</c>, ~300 winners against
/// ~150 deep losses across five characters):
///
///   upgrades      AUC 0.62   (0.56 0.63 0.61 0.60 0.70 — every character)
///   efficiency    AUC 0.62   (0.58 0.70 0.59 0.59 0.62 — every character)
///   defence       AUC 0.54   (0.54 0.63 0.48 0.51 0.55)
///   consistency   AUC 0.47
///   offence       AUC 0.40   (0.43 0.49 0.37 0.41 0.31 — every character below a coin flip)
///
/// Offence is therefore the *smallest* term, not the largest. That is the opposite
/// of the first version of this score, which gave it half the weight and, as a
/// result, ranked the decks that lost above the decks that won. It is kept at all
/// because a deck that cannot kill is not a deck, so it still has to be able to
/// score — but the sample is unambiguous that piling damage on past that point is
/// what losing decks do.
/// </summary>
internal sealed record ScoreWeights(
    double Defence = 30,
    double Upgrades = 25,
    double Efficiency = 20,
    double Consistency = 13,
    double Offence = 12)
{
    public double Total => Offence + Defence + Upgrades + Efficiency + Consistency;
}

/// <summary>
/// What Ascension 10 does to a draft, taken from the shipped source. The
/// pipeline applies each of these itself rather than asking
/// <c>AscensionHelper</c>, because that helper reads the live
/// <c>RunManager</c> — which is not in progress in a test process and would
/// silently hand back the non-ascension value.
/// </summary>
internal static class Ascension10
{
    /// <summary>Ascender's Bane is added at run start (AscensionManager.ApplyEffectsTo).</summary>
    public const string StartingCurse = "ASCENDERS_BANE";

    /// <summary>Card removal costs 100 + 50 per removal used, not 75 + 25 (MerchantCardRemovalEntry, Inflation).</summary>
    public const int RemovalBaseCost = 100;
    public const int RemovalCostIncrease = 50;

    /// <summary>Gold is paid at 75% (AscensionHelper.PovertyAscensionGoldMultiplier).</summary>
    public const double GoldMultiplier = 0.75;

    /// <summary>
    /// Reward upgrade odds are halved: <c>act * 0.125</c> instead of <c>act * 0.25</c>
    /// (CardFactory.UpgradedCardOddScaling, Scarcity). Rare cards are never upgraded on drop.
    /// </summary>
    public const double UpgradeOddsPerAct = 0.125;

    /// <summary>An extra potion slot is gone (AscensionManager, TightBelt) — carried here for completeness.</summary>
    public const int PotionSlotPenalty = 1;

    /// <summary>Enemies hit harder and have more HP (ToughEnemies / DeadlyEnemies) — see <see cref="SimConfig.References"/>.</summary>
    public const double EnemyHpScale = 1.05;
}

/// <summary>
/// One act walked as a single path. The map itself holds 3 shops and 8 elites
/// under SwarmingElites (MapPointTypeCounts), but a path visits at most one node
/// per row, so what a run actually meets is a fraction of that. The sequences
/// below are a representative A10 path per act: they keep the map's ordering
/// rules (no two elites, camps or shops in a row; camps and the chest in the
/// back half) with counts a bot that fights most elites would see.
/// </summary>
internal static class ActPath
{
    internal static readonly MapPointType[][] Blueprint =
    [
        // Act 1 — Overgrowth: 15 rooms, minus one for multiplayer.
        [
            MapPointType.Monster, MapPointType.Monster, MapPointType.Unknown, MapPointType.Monster,
            MapPointType.Shop, MapPointType.Monster, MapPointType.Monster, MapPointType.Elite,
            MapPointType.Unknown, MapPointType.Monster, MapPointType.Treasure, MapPointType.RestSite,
            MapPointType.Unknown, MapPointType.RestSite,
        ],
        // Act 2 — Hive: 14 rooms, minus one for multiplayer.
        [
            MapPointType.Monster, MapPointType.Unknown, MapPointType.Monster, MapPointType.Shop,
            MapPointType.Monster, MapPointType.Elite, MapPointType.Monster, MapPointType.Unknown,
            MapPointType.Monster, MapPointType.Treasure, MapPointType.RestSite, MapPointType.Monster,
            MapPointType.RestSite,
        ],
        // Act 3 — Glory: 13 rooms, minus one for multiplayer. Double Boss adds a second boss floor.
        [
            MapPointType.Monster, MapPointType.Monster, MapPointType.Elite, MapPointType.Shop,
            MapPointType.Monster, MapPointType.Unknown, MapPointType.RestSite, MapPointType.Elite,
            MapPointType.Monster, MapPointType.Treasure, MapPointType.Monster, MapPointType.RestSite,
        ],
    ];

    internal static int Acts => Blueprint.Length;

    /// <summary>A10 adds a second act-3 boss (AscensionLevel.DoubleBoss).</summary>
    internal static int BossesInAct(int act) => act == Acts - 1 ? 2 : 1;

    /// <summary>Gold an encounter pays before the ascension multiplier (EncounterModel.Min/MaxGoldReward).</summary>
    internal static (int Min, int Max) GoldReward(MapPointType type) => type switch
    {
        MapPointType.Monster => (10, 20),
        MapPointType.Elite => (35, 45),
        MapPointType.Boss => (100, 100),
        _ => (0, 0),
    };
}
