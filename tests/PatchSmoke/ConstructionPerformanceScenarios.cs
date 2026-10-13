using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoopBots;
using CoopBots.Building;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class ConstructionPerformanceScenarios
{
    // R2 2026-10-01: five production faults exit 1; restored run exits 0.
    // Commands + raw logs: work/reward-performance/r2-results.json.
    // hand-value: System.Exception: Construction performance: sampled hand results or stable tie order changed
    // deck-value / ally-scope: System.Exception: Construction performance: full deck scores or decision-scope invalidation changed
    // reward-scope: System.Exception: Construction performance: reward plus trace must evaluate four decks once; robustness calls=40, expected=16
    // hand-allocations: System.Exception: Construction performance: repeated hand searches allocated 67874304 bytes, budget=2097152
    internal sealed record Snapshot(string Hands, DeckStrength.Estimate[] Decks);
    private static int evaluations;
    private static void CountEvaluation() => evaluations++;
    private static (Player[] Players, RunState Run) Team()
    {
        var ps=Enumerable.Range(1,4).Select(i=>Player.CreateForNewRun<Deprived>(UnlockState.all,(ulong)i)).ToArray();
        for(var i=0;i<ps.Length;i++)typeof(Player).GetField("<Character>k__BackingField",BindingFlags.NonPublic|BindingFlags.Instance)!
            .SetValue(ps[i],ModelDb.AllCharacters.First(c=>c.Id.Entry==(i<2?"SILENT":"IRONCLAD")));
        var run=RunState.CreateForTest(ps,ascensionLevel:10,seed:"CONSTRUCTION-PERFORMANCE");
        foreach(var p in ps)
        {
            p.MaxEnergy=3;p.Creature.SetMaxHpInternal(80);p.Creature.SetCurrentHpInternal(80);
            foreach(var c in p.Deck.Cards.ToArray())p.Deck.RemoveInternal(c);
            foreach(var c in p.Character.StartingDeck)p.Deck.AddInternal(Card(c.Id.Entry,p));
            p.Deck.AddInternal(Card("ASCENDERS_BANE",p));
        }
        return (ps,run);
    }
    private static CardModel Card(string id,Player p)
    { var c=ModelDb.AllCards.First(c=>c.Id.Entry==id).ToMutable();c.Owner=p;return c; }

    // The original performance golden came from frozen DLL 357DD79C. Rebased
    // 2026-10-03 for intentional semantics: generated Shivs now do damage in
    // ConstructionHand, including the opening hand model. The
    // previous golden and measured deltas are archived in work/decision-fixes-20261003.
    // Independent native resource/choice witnesses live in DecisionFixScenarios.
    // R2 innate rollback exited 1: System.Exception: Construction performance: full deck scores or decision-scope invalidation changed
    // 2026-10-04: native Innate ordering in Silent growth forecasts intentionally
    // changes opening/cycle estimates; 768 hand hashes remain identical. Old
    // snapshot and all 22 vector deltas: work/act2-priority-20261004/construction-*.json.
    // Each hand result
    // includes masks, targets, energy, timing, utility and the stable tie choice.
    // 2026-10-04: replace relative robustness loss by nonnegative absolute
    // scenario weighting. Only Total, Robustness and its propagated Growth
    // change in these 22 vectors; all other fields and 768 hand hashes match.
    // Reviewed old/new/deltas: work/decision-consistency-20261004/construction-*.json.
    // R2: restore the old relative-difference formula -> exit 1:
    // System.Exception: Construction performance: full deck scores or decision-scope invalidation changed
    // Restored exit 0; command in that directory's sequence-r2.json.
    // 2026-10-07: native poison first tick follows held statuses and precedes
    // enemy attacks; a two-tick poison credit is not immediate attack damage.
    // Frozen 10/05 DLLs exactly reproduce the previous complete fixture. The
    // old/new 768 rows retain the SAME 18 serialized fields (new internal
    // diagnostics are excluded); only 69 rows containing DeadlyPoison in the
    // sampled nine-card window change. All 603 nonpoison rows, 96 remaining
    // poison rows, and all fields of all 22 deck vectors are exactly unchanged.
    // Only Hands is rebased, with full old/new rows and per-field differences
    // in work/decision-repair-20261007/performance-{frozen,current,deltas}*.json.
    // Cache/reward-scope, stable tie masks/targets and the 2MiB guard stay intact.
    // Rebase R2: restoring poison into immediate damage, and independently
    // perturbing nonpoison utility by .001, each exited 1 with raw text:
    // System.Exception: Construction performance: sampled hand results or stable tie order changed
    // Exact source-byte restoration then exits 0 for each. Commands, source
    // hashes and complete logs: performance-rebase-r2-results.json in that directory.
    // 2026-10-07 owner temporary-stat/upgrade audit: real action order applies
    // temporary Strength/Dexterity only to later hits/blocks; Fiend Fire reuses
    // its realized temporary exhaust bonus once. The two new diagnostics remain
    // INTERNAL, preserving the same eighteen serialized result fields.
    // Frozen pre-audit source and current source reproduce identical inputs
    // and contexts for all 768 rows. Only 29 Coordinate/Anticipate windows
    // change; all 471 windows without temporary stats and 268 other temporary
    // windows remain identical, including masks, targets and stable tie choice.
    // Of 22 complete vectors, only ordinary/upgraded Anticipate (#3/#4) Growth
    // and corresponding Total change. All their other fields and all other
    // twenty vectors are unchanged. The limited rebase changes only Hands and
    // these four vector fields; reward-cache, stable ties and 2MiB guards stay.
    // Complete old/new rows, per-field deltas and old fixture are archived in
    // work/incombat-repair-20261007/temporary-stats/build/performance-*.json.
    // Source-mirror temporal/nonstat regressions and exact restores are recorded
    // alongside them in r2-results.json; the guard still compares every field.
    // Temporal and independent nonstat .001 rebase mutations each actually
    // compile exit0 and assert exit1 with raw original exception:
    // System.Exception: Construction performance: sampled hand results or stable tie order changed
    // Exact source-byte restore then full performance GREEN0 for each; 32-case
    // source audit and command/error records are in the above r2-results.json.
    // 2026-10-08 latest-run exhaust-control audit: identical original seeded
    // inputs and eighteen serialized fields; only rows 98/471/664/682/767
    // change, all containing TrueGrit+ in the nine-card window. The other
    // 763 rows and every field of all 22 deck vectors remain exact. The five
    // changes preserve later-payable inventory when choosing exhaust lines;
    // immediate Utility is still returned without a second retention charge.
    // Native ExhaustControl independently proves the legal Wound/core choice
    // and actual random core-loss outcome; synthetic mixed-role rows are not
    // claimed to be full native combat optima. Frozen baseline reproduces the
    // old golden; disabling only retention reproduces all old rows/targets.
    // Only Hands is rebased. Stable masks/ties, 16 evaluation cache checks and
    // 64 calls/2MiB allocation guard stay unchanged. Full old/new/attribution
    // rows and commands: work/latest-run-repair-20261008/performance-review.
    // Two independent actual production-source R2 faults compile exit0, then
    // assert exit1: disable-retention-red.log and nonexhaust-utility-red.log.
    // Both raw exceptions are exactly:
    // System.Exception: Construction performance: sampled hand results or stable tie order changed
    // Exact source-byte restoration then compiles exit0 and runs the complete
    // performance method GREEN0 for each (allocations 62976 bytes/64 calls).
    // Mutant/restored source SHA and raw commands are recorded alongside the
    // three dumps in performance-rebase-r2-results.json/restoration-audit.json.
    internal static Snapshot Measure()
    {
        var (ps,run)=Team();var p=ps[0];
        string[] ids=["STRIKE_IRONCLAD","DEFEND_IRONCLAD","BASH","BULLY","BLUDGEON","PREPARED","ANTICIPATE",
            "BURNING_PACT","TRUE_GRIT","CINDER","SECOND_WIND","FIEND_FIRE","PURITY","CALCULATED_GAMBLE",
            "SURVIVOR","ACROBATICS","DAGGER_THROW","TACTICIAN","REFLEX","BLADE_DANCE","COORDINATE",
            "DEADLY_POISON","NEUTRALIZE","WOUND","ASCENDERS_BANE"];
        var pool=ids.Select(id=>Card(id,p)).ToList();
        foreach(var c in pool.ToArray().Where(c=>c.IsUpgradable))
        { var upgrade=(CardModel)c.MutableClone();upgrade.UpgradeInternal();upgrade.FinalizeUpgradeInternal();pool.Add(upgrade); }
        // Exercise nested discard branches, not just native non-cleaner Sly.
        foreach(var id in new[]{"PREPARED","DAGGER_THROW"})
        { var sly=Card(id,p);sly.AddKeyword(CardKeyword.Sly);pool.Add(sly); }
        var random=new Random(20261001);var serialized=new StringBuilder();
        for(var n=0;n<768;n++)
        {
            var slots=Enumerable.Range(0,random.Next(3,13)).Select(_=>
            {
                if(random.Next(8)==0)return new ConstructionHand.Slot(null,null,true,3*random.Next(1,4));
                var c=pool[random.Next(pool.Count)];var f=CardProfile.Of(c,3) with {Damage=CardProfile.Of(c,3).Damage+n%3};
                return new ConstructionHand.Slot(c,f,f.Unplayable,PlayBlock:n%2);
            }).ToArray();
            var context=new ConstructionHand.Context(n%6,n%3*12,18+n%4*40,n%2+1,n%2==0,
                n%3*3,n%6,n%3==0?1:0,n%4==0?3:0,n%7==0,n%3);
            serialized.Append(JsonSerializer.Serialize(ConstructionHand.Solve(slots,n%9+1,context)));
        }
        var hands=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized.ToString())));
        var decks=new List<DeckStrength.Estimate>();
        foreach(var seat in new[]{0,2})
        {
            var owner=ps[seat];
            using(var scope=DeckStrength.BeginEvaluation())
            {
                decks.Add(PreviousPriorScore(owner.Deck.Cards.ToArray(),owner));
                foreach(var id in seat==0?new[]{"PREPARED","ANTICIPATE","TACTICIAN","GRAND_FINALE"}:new[]{"BULLY","BLUDGEON","BASH","SECOND_WIND"})
                {
                    var c=Card(id,owner);
                    decks.Add(PreviousPriorScore(owner.Deck.Cards.Append(c).ToArray(),owner));
                    var clone=(CardModel)c.MutableClone();clone.UpgradeInternal();clone.FinalizeUpgradeInternal();
                    decks.Add(PreviousPriorScore(owner.Deck.Cards.Append(clone).ToArray(),owner));
                }
                decks.Add(PreviousPriorScore(owner.Deck.Cards.Skip(1).ToArray(),owner));
            }
            // New decisions must see ally deaths, changed decks and energy.
            ps[3].Creature.SetCurrentHpInternal(0);owner.MaxEnergy=4;
            decks.Add(PreviousPriorScore(owner.Deck.Cards.ToArray(),owner));
            owner.MaxEnergy=3;ps[3].Creature.SetCurrentHpInternal(80);
        }
        return new(hands,decks.ToArray());
    }

    // Owner-approved -6 -> -15 starter prior is an exact additive change.
    // Keep the original frozen vectors and normalize ONLY that known term;
    // combat, robustness, growth and every other field remain independently checked.
    private static DeckStrength.Estimate PreviousPriorScore(IReadOnlyList<CardModel> deck,Player player)
    {
        var score=DeckStrength.Evaluate(deck,player);
        var shift=9*deck.Count(c=>c.Rarity==CardRarity.Basic
            && (c.Tags.Contains(CardTag.Strike)||c.Tags.Contains(CardTag.Defend)));
        return score with { Total=score.Total+shift,Prior=score.Prior+shift };
    }

    internal static void Run()
    {
        var expected=JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"construction-performance.json")))!;
        var actual=Measure();
        if(actual.Hands!=expected.Hands)throw new Exception("Construction performance: sampled hand results or stable tie order changed");
        var properties=typeof(DeckStrength.Estimate).GetProperties();
        if(actual.Decks.Length!=expected.Decks.Length || actual.Decks.Where((deck,i)=>properties.Any(p=>
            Math.Abs((double)p.GetValue(deck)!-(double)p.GetValue(expected.Decks[i])!)>1e-8)).Any())
            throw new Exception("Construction performance: full deck scores or decision-scope invalidation changed");
        Console.WriteLine("PASS: construction matches 768 reviewed hand results and 22 full deck score vectors.");

        var (ps,run)=Team();var owner=ps[0];
        var offers=new[]{Card("PREPARED",owner),Card("ANTICIPATE",owner),Card("DAGGER_THROW",owner)};
        var harmony=new Harmony("coopbots.construction.performance.test");
        var method=AccessTools.Method(typeof(DeckRobustness),nameof(DeckRobustness.Evaluate));
        harmony.Patch(method,prefix:new HarmonyMethod(typeof(ConstructionPerformanceScenarios),nameof(CountEvaluation)));
        try
        {
            evaluations=0;BotBrain.ChooseCardReward(owner,offers);
            if(evaluations!=16)throw new Exception($"Construction performance: reward plus trace must evaluate four decks once; robustness calls={evaluations}, expected=16");
            Console.WriteLine("PASS: reward selection and trace share four deck evaluations, including growth.");
        }
        finally { harmony.UnpatchAll(harmony.Id); }

        var cards=new[]{"PREPARED","TACTICIAN","REFLEX","DAGGER_THROW","ACROBATICS","WOUND","BASH","BLUDGEON","SURVIVOR"}
            .Select(id=>{var c=Card(id,owner);return new ConstructionHand.Slot(c,CardProfile.Of(c));}).ToArray();
        var context=new ConstructionHand.Context(3,26,100,WitherDamage:9,Counter:5);
        ConstructionHand.Solve(cards,5,context); // warm the reusable buffers
        var before=GC.GetAllocatedBytesForCurrentThread();
        for(var n=0;n<64;n++)ConstructionHand.Solve(cards,5,context);
        var bytes=GC.GetAllocatedBytesForCurrentThread()-before;
        if(bytes>2*1024*1024)throw new Exception($"Construction performance: repeated hand searches allocated {bytes} bytes, budget=2097152");
        Console.WriteLine($"PASS: construction hand scratch buffers bound allocations ({bytes} bytes / 64 searches).");
    }
}
