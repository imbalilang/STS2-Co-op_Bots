using System.Diagnostics;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CoopBots.Kernel;

/// <summary>
/// Search several actions jointly across seats, targets, card choices and bottles,
/// then judge retained prefixes by complete continuations. The beam's local score
/// never masquerades as a terminal result. All work is resumable between actions.
/// </summary>
internal sealed class KernelJointTurnRun
{
    private readonly KernelSession root;
    private readonly IReadOnlyList<Player> party;
    private readonly IReadOnlyList<Player> actors;
    private readonly TournamentOptions options;
    private readonly int budgetMs;
    private KernelTeamSearch? search;
    private IReadOnlyList<IReadOnlyList<KernelTeamSearch.Action>> paths = [];
    private int pathIndex;
    private int replayIndex;
    private KernelSession? replay;
    private RolloutRun? continuation;
    private readonly List<ulong[]> alive = new();
    private readonly List<Creature[]> enemies = new();
    private double computeMs;
    private readonly List<TerminalRecord> considered = new();
    private TournamentResult? best;
    internal bool Done { get; private set; }
    internal TournamentResult? Result => best;
    internal int Nodes { get; private set; }
    internal int Prefixes { get; private set; }
    internal int MaxPrefixActions { get; private set; }

    internal KernelJointTurnRun(KernelSession root, IReadOnlyList<Player> party,
        IReadOnlyList<Player> actors, TournamentOptions options, int budgetMs)
    {
        this.root = root; this.party = party; this.actors = actors;
        this.options = options; this.budgetMs = Math.Max(1, budgetMs);
    }

    internal bool Advance(TimeSpan slice)
    {
        if (Done) return true;
        var clock = Stopwatch.StartNew();
        try
        {
            do
            {
                if (search is null && paths.Count == 0 && pathIndex == 0)
                {
                    search = new KernelTeamSearch(root, actors,
                        s => KernelTacticalPolicy.ActionValue(root, s, party, actors),
                        new KernelTeamSearch.Options(Depth: 28, Width: 12, MaxNodes: 4096,
                            IncludeEndTurns: options.IncludeEndTurns, MaxRounds: 1)
                        {
                            IncludePotions = options.IncludePotions,
                            RequirePotionOpening = options.PotionsOnly,
                            TerminalObjective = options.Objective,
                            CaptureReplayStates = false,
                            // CompactStateKey omits piles/RNG/potions. Do not merge
                            // different choice or consumable histories using that key.
                            Deduplicate = false,
                        });
                }
                if (search is not null)
                {
                    search.Advance(TimeSpan.FromMilliseconds(1), () => true);
                    if (computeMs + clock.Elapsed.TotalMilliseconds >= budgetMs * .55)
                        search.FinishAtBudget();
                    if (!search.IsComplete) continue;
                    Nodes = search.ExpandedNodesSoFar;
                    paths = search.ContinuationPaths;
                    search.Dispose(); search = null;
                    if (paths.Count == 0) { Done = true; break; }
                }
                if (continuation is not null)
                {
                    continuation.Step();
                    if (!continuation.Done) continue;
                    Accept(continuation.Result!);
                    continuation = null; replay = null; pathIndex++;
                }
                if (pathIndex >= paths.Count || Prefixes > 0 && computeMs + clock.Elapsed.TotalMilliseconds >= budgetMs)
                { Done = true; break; }
                if (replay is null)
                {
                    replay = root.Fork(); replayIndex = 0; alive.Clear(); enemies.Clear();
                    replay.AutoResolveTurnStartChoices = true;
                    replay.AutoResolveEnemyChoices = true;
                }
                var path = paths[pathIndex];
                if (replayIndex < path.Count)
                {
                    alive.Add(party.Where(p => replay.Hp(p.Creature) > 0).Select(p => p.NetId).OrderBy(n => n).ToArray());
                    enemies.Add(replay.Enemies.ToArray());
                    if (!replay.ReplayPlanned(path[replayIndex], 80, out _))
                    { replay = null; pathIndex++; continue; }
                    replayIndex++;
                    continue;
                }
                continuation = new RolloutRun(replay, party, options.ContinuationActors ?? actors,
                    new RolloutOptions(RolloutPolicyKind.Tactical, Seed: options.Rollout?.Seed ?? 0));
            } while (clock.Elapsed < slice);
        }
        finally { computeMs += clock.Elapsed.TotalMilliseconds; }
        return Done;
    }

    private void Accept(RolloutOutcome outcome)
    {
        Prefixes++;
        var prefix = paths[pathIndex];
        MaxPrefixActions = Math.Max(MaxPrefixActions, prefix.Count);
        var record = outcome.Record with { ResourcesConsumed = prefix.Count(a => a.Potion is not null) };
        considered.Add(record);
        if (best?.Record is not null && TerminalComparer.Compare(record, best.Record, options.Objective) <= 0) return;
        best = new TournamentResult(prefix[0], record, considered, Prefixes,
            considered.Count(r => !r.Verified), "joint-turn")
        {
            Script = prefix.Concat(outcome.Actions).ToArray(),
            ScriptAliveStates = alive.Concat(outcome.AliveStates).ToArray(),
            ScriptEnemyStates = enemies.Concat(outcome.EnemyStates).ToArray(),
        };
    }
}
