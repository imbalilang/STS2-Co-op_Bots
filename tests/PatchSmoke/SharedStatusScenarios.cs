using System.Text.Json;
using CoopBots.Building;
using DeckSim;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class SharedStatusScenarios
{
    // R2: four production faults exit 1, restored suite exits 0.
    // work/robustness-optimization/r2-shared-results.json and r2-shared-sign-result.json:
    // System.Exception: Shared status: Bash must retain more value as living attacking allies join
    // Shared status: a four-player team must preserve its sole Vulnerable source before deleting ordinary basics
    // Shared status: four Ironclad starting decks must choose another basic before Bash
    // Shared status: Bash's extra Vulnerable duration must also help the starting team when upgrading
    // System.Exception: Shared status: poison-only allies must not invent attack damage for Vulnerable
    internal static void Run()
    {
        var ps=Enumerable.Range(1,4).Select(i=>Player.CreateForNewRun<Deprived>(UnlockState.all,(ulong)i)).ToArray();
        var run=RunState.CreateForTest(ps,ascensionLevel:10,seed:"SHARED-VULNERABLE");
        foreach(var p in ps)
        {
            p.MaxEnergy=3;p.Creature.SetMaxHpInternal(80);p.Creature.SetCurrentHpInternal(80);
            for(var i=0;i<5;i++)p.Deck.AddInternal(run.CreateCard<StrikeIronclad>(p));
            for(var i=0;i<4;i++)p.Deck.AddInternal(run.CreateCard<DefendIronclad>(p));
            p.Deck.AddInternal(run.CreateCard<AscendersBane>(p));
        }
        var owner=ps[0];var bash=run.CreateCard<Bash>(owner);owner.Deck.AddInternal(bash);
        var rows=new List<object>();var failures=new List<string>();
        void Check(bool ok,string text){if(!ok)failures.Add("Shared status: "+text);}
        (double Removal,double Upgrade,string Best) Measure(string scenario)
        {
            using var evaluation=DeckStrength.BeginEvaluation();
            var removal=BuildValue.Remove(bash,owner).Total;
            var upgrade=BuildValue.UpgradeDelta(bash,owner).Total;
            var best=owner.Deck.Cards.Where(c=>c.IsRemovable).OrderByDescending(c=>BuildValue.Remove(c,owner).Total).First().Id.Entry;
            rows.Add(new{scenario,alive=ps.Count(p=>p.Creature.IsAlive),removal,upgrade,best});
            Console.WriteLine($"SHARED STATUS {scenario} alive={ps.Count(p=>p.Creature.IsAlive)} bashRemove={removal:F4} bashUpgrade={upgrade:F4} best={best}");
            return(removal,upgrade,best);
        }
        foreach(var p in ps.Skip(1))p.Creature.SetCurrentHpInternal(0);
        var solo=Measure("sole-source");
        ps[1].Creature.SetCurrentHpInternal(80);var duo=Measure("sole-source");
        foreach(var p in ps.Skip(1))p.Creature.SetCurrentHpInternal(80);
        var four=Measure("sole-source");
        Check(duo.Removal<solo.Removal && four.Removal<duo.Removal,"Bash must retain more value as living attacking allies join");
        Check(four.Best!="BASH","a four-player team must preserve its sole Vulnerable source before deleting ordinary basics");
        foreach(var p in ps.Skip(1))p.Deck.AddInternal(run.CreateCard<Bash>(p));
        var starters=Measure("all-starting-bashes");
        Check(starters.Best!="BASH","four Ironclad starting decks must choose another basic before Bash");
        Check(starters.Upgrade>solo.Upgrade,"Bash's extra Vulnerable duration must also help the starting team when upgrading");
        foreach(var p in ps.Skip(1))
        {
            foreach(var card in p.Deck.Cards.ToArray())p.Deck.RemoveInternal(card);
            for(var i=0;i<10;i++)p.Deck.AddInternal(run.CreateCard<DeadlyPoison>(p));
        }
        var poison=Measure("poison-only-allies");
        Check(Math.Abs(poison.Removal-solo.Removal)<1e-7,"poison-only allies must not invent attack damage for Vulnerable");
        var path=Path.Combine(DraftSimHarness.RepoRoot(),"work/robustness-optimization/shared-status.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,JsonSerializer.Serialize(rows));
        if(failures.Count>0)throw new Exception(string.Join("\n",failures));
        Console.WriteLine("PASS: shared Vulnerable values living allied attacks, preserves starter Bash and excludes poison damage.");
    }
}
