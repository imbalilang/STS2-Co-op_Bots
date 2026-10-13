using CoopBots.Building;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// 2026-09-30 policy migration: Elo remains evidence inside a deck potential.
// Old exact Add-only formula assertions are superseded by edit identities and
// the frozen HistoricalBuildValue comparison on recorded community decisions.
internal static class CommunityDraftScenarios
{
    internal static void Run()
    {
        TestEnvironment.Ensure();
        void Check(bool ok, string message) { if (!ok) throw new Exception("Community draft regression: " + message); }
        Check(BakedCardElo.CardCount > 0 && CommunityDraft.IsFinite(BakedCardElo.SkipElo)
            && BakedCardElo.Source.Contains("spire-codex.com/api/runs/scores/cards")
            && BakedCardElo.SnapshotSha256.Length == 64, "community prior must have a finite skip reference and provenance");
        var p = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var combat = new CombatState(runState: RunState.CreateForTest([p], seed: "COMMUNITY-STRENGTH"));
        p.ResetCombatState(); combat.AddPlayer(p); p.MaxEnergy = 3;
        for (var i = 0; i < 5; i++) p.Deck.AddInternal(combat.CreateCard<StrikeSilent>(p));
        for (var i = 0; i < 4; i++) p.Deck.AddInternal(combat.CreateCard<DefendSilent>(p));
        var curse = BuildValue.Add(combat.CreateCard<Decay>(p), p);
        Check(curse.Total == -1000 && curse.Reason == "curse", "curse acquisition veto must survive unification");
        var star = BuildValue.Add(combat.CreateCard<SevenStars>(p), p);
        // Owner 2026-10-01 replaces the Add-only zero with a large common-deck penalty.
        // R2: Penalty=0 -> "missing stars must receive the common deck startup penalty"
        // (work/crossclass-audit-20261001/r2-community.log, exit 1); restored green.
        Check(star.Total < -500 && star.Reason.Contains("resource-startup:-1000"),
            "missing stars must receive the common deck startup penalty");
        var gambit = BuildValue.Add(combat.CreateCard<TheGambit>(p), p);
        Check(gambit.Total == -100, "owner-requested Gambit blacklist must survive unification");
        Console.WriteLine("PASS: community source provenance and acquisition vetoes survive the unified model.");
        var bash = BuildValue.AddDetailed(combat.CreateCard<Bash>(p), p);
        Check(bash.UsedFallback && !bash.GuardRefusal && bash.Reason.Contains("deck-strength:"),
            "missing community observations must still use the common deck model");
        var adrenaline = BuildValue.AddDetailed(combat.CreateCard<Adrenaline>(p), p);
        var lift = BuildValue.AddDetailed(combat.CreateCard<Lift>(p), p);
        Check(adrenaline.Total > lift.Total, "affordable draw and energy must outrank an unsupported payoff");
        Check(MultiplayerPickLift.Term("POMMEL_STRIKE", 1) == 0 && MultiplayerPickLift.Term("POMMEL_STRIKE", 4) > 0,
            "population priors must retain their party-size scope");
        Console.WriteLine("PASS: unseen cards retain a model estimate; community evidence remains scoped to party size.");
        var offered = combat.CreateCard<BulletTime>(p);
        var before = p.Deck.Cards.ToArray();
        var score = BuildValue.Add(offered, p);
        Check(score == BuildValue.Add(offered, p) && p.Deck.Cards.SequenceEqual(before), "unmodeled scoring must be deterministic and read-only");
        var after = before.Append(offered).ToArray();
        Check(Math.Abs(score.Total + BuildValue.Remove(offered, p, after).Total) < 1e-8,
            "an unmodeled acquisition and its removal must have opposite values");
        Console.WriteLine("PASS: unmodeled card priors obey the same addition/removal identity.");
    }
}
