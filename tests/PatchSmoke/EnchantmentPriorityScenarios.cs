using System.Reflection;
using CoopBots;
using CoopBots.Building;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Relics;

internal static class EnchantmentPriorityScenarios
{
    // R2 2026-10-04: 14 production faults, each exit 1, then restored.
    // Evidence: work/enchantment-policy-20261004/r2-results.json and r2-*.log.
    // veto -> System.Exception: Enchantment priorities: Adroit/BOMBARDMENT must have tier -1
    // tier -> System.Exception: Enchantment priorities: preference tier must dominate the original score
    // tie -> System.Exception: Enchantment priorities: same tier must keep original scoring
    // x-cost -> System.Exception: Enchantment priorities: Corrupted/WHIRLWIND must have tier 2
    // hits -> System.Exception: Enchantment priorities: Momentum/SEVEN_STARS must have tier 3
    // self-replay -> System.Exception: Enchantment priorities: Adroit/HOWL_FROM_BEYOND must have tier 3
    // upgrade-exhaust -> System.Exception: Enchantment priorities: SoulsPower/HOLOGRAM must have tier -1
    // optional -> System.Exception: Enchantment priorities: optional selection must fill allowed slots without rejected cards
    // forced -> System.Exception: Enchantment priorities: forced selection must meet the native minimum
    // damage -> System.Exception: Enchantment priorities: single play damage must sum all hits
    // dispatcher -> System.Exception: Enchantment priorities: every native enchantment overload must carry the enchantment identity
    // event-veto -> System.Exception: Enchantment priorities: event must avoid paying for an all-veto target pool
    // clone -> System.Exception: Enchantment priorities: Clone event option must have the fixed score minus 100
    // event-target-value -> System.Exception: Enchantment priorities: ancient option must price the targets the priority policy will choose
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("Enchantment priorities: " + message); }
    private static Player Deck(params string[] ids)
    {
        var p = ConstructionParallelScenarios.Fixture();
        foreach (var c in p.Deck.Cards.ToArray()) p.Deck.RemoveInternal(c);
        foreach (var id in ids) p.Deck.AddInternal(ConstructionParallelScenarios.Card(id, p));
        return p;
    }
    private static EnchantmentModel E(string name) => EnchantmentPriorities.Resolve(name)!;
    private static int Tier(CardModel c, string e) => EnchantmentPriorities.Classify(c, E(e)).Tier;
    private static EnchantmentPriorities.Plan Pick(Player p, string e, int min = 1, int max = 1)
        => EnchantmentPriorities.Choose(p, p.Deck.Cards, E(e), 1, min, max);

    internal static void Run()
    {
        TestEnvironment.Ensure();
        using var evaluation = DeckStrength.BeginEvaluation();
        var display = new Harmony("coopbots.test.enchantment-display");
        display.Patch(AccessTools.PropertyGetter(typeof(CardModel),nameof(CardModel.Title)),prefix:new HarmonyMethod(typeof(EnchantmentPriorityScenarios),nameof(Text)));
        display.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString),"GetFormattedText"),prefix:new HarmonyMethod(typeof(EnchantmentPriorityScenarios),nameof(Text)));
        display.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString),"Exists",[typeof(string),typeof(string)]),prefix:new HarmonyMethod(typeof(EnchantmentPriorityScenarios),nameof(Exists)));
        foreach(var type in new[]{typeof(EnchantmentModel),typeof(CardModel),typeof(RelicModel)})
            display.Patch(AccessTools.PropertyGetter(type,"HoverTips"),prefix:new HarmonyMethod(typeof(EnchantmentPriorityScenarios),nameof(Tips)));
        display.Patch(AccessTools.PropertyGetter(typeof(RelicModel),"HoverTipsExcludingRelic"),prefix:new HarmonyMethod(typeof(EnchantmentPriorityScenarios),nameof(Tips)));
        display.Patch(AccessTools.Method(typeof(EventOption),"AddLocVars"),prefix:new HarmonyMethod(typeof(EnchantmentPriorityScenarios),nameof(NoDisplay)));
        try { Rules(); Ranking(); Dispatch(); Events(); }
        finally { display.UnpatchAll(display.Id); }
    }
    private static bool Text(ref string __result) { __result="";return false; }
    private static bool Exists(ref bool __result) { __result=false;return false; }
    private static bool NoDisplay() => false;
    private static bool Tips(ref IEnumerable<MegaCrit.Sts2.Core.HoverTips.IHoverTip> __result) { __result=[];return false; }

    private static void Rules()
    {
        // Exact CSV coverage includes the five usually automatic enchantments.
        var names = new[] { "Adroit", "Clone", "Corrupted", "Glam", "Goopy", "Imbued", "Inky", "Instinct", "Momentum", "Nimble",
            "PerfectFit", "RoyallyApproved", "Sharp", "Slither", "SlumberingEssence", "SoulsPower", "Sown", "Spiral", "Steady", "Swift", "TezcatarasEmber", "Vigorous" };
        Check(names.Order().SequenceEqual(EnchantmentPriorities.Table.Keys.Order()) && names.All(n => E(n) is not null), "all 22 native enchantments must resolve");
        var p = Deck("ANGER", "STRIKE_IRONCLAD", "BASH", "BLUDGEON", "METEOR_STRIKE", "WHIRLWIND", "SEVEN_STARS", "SWORD_BOOMERANG", "TWIN_STRIKE", "BOLAS", "HOWL_FROM_BEYOND", "BOMBARDMENT", "BACKSTAB", "INFLAME", "HOLOGRAM", "DEMONIC_SHIELD");
        CardModel C(string id) => p.Deck.Cards.Single(c => c.Id.Entry == id);
        var cases = new (string Enchant, string Card, int Tier)[] {
            ("Adroit","HOWL_FROM_BEYOND",3), ("Adroit","BOMBARDMENT",-1), ("Adroit","ANGER",2), ("Adroit","STRIKE_IRONCLAD",1),
            ("Adroit","BACKSTAB",-1), ("Adroit","INFLAME",-1), ("Corrupted","BACKSTAB",-1), ("Corrupted","WHIRLWIND",2),
            ("Imbued","WHIRLWIND",2), ("Imbued","BLUDGEON",1), ("Imbued","BASH",1), ("Imbued","STRIKE_IRONCLAD",0),
            ("Instinct","BACKSTAB",-1), ("Instinct","WHIRLWIND",2), ("Momentum","BOLAS",3), ("Momentum","SEVEN_STARS",3),
            ("Momentum","ANGER",2), ("Momentum","SWORD_BOOMERANG",2), ("Momentum","TWIN_STRIKE",1), ("Momentum","BACKSTAB",-1),
            ("Nimble","ANGER",2), ("Nimble","STRIKE_IRONCLAD",1), ("Nimble","BACKSTAB",-1),
            ("PerfectFit","INFLAME",-1), ("PerfectFit","BACKSTAB",-1), ("RoyallyApproved","BACKSTAB",-1),
            ("Sharp","SEVEN_STARS",3), ("Sharp","ANGER",2), ("Sharp","WHIRLWIND",2), ("Sharp","TWIN_STRIKE",1), ("Sharp","BACKSTAB",-1),
            ("Slither","METEOR_STRIKE",3), ("Slither","BLUDGEON",2), ("Slither","BASH",1), ("Slither","ANGER",-1), ("Slither","STRIKE_IRONCLAD",-1),
            ("SlumberingEssence","ANGER",-1), ("SoulsPower","HOLOGRAM",-1), ("SoulsPower","DEMONIC_SHIELD",-1),
            ("SoulsPower","BACKSTAB",0), ("Sown","BACKSTAB",1), ("Spiral","STRIKE_IRONCLAD",0),
            ("Steady","ANGER",-1), ("Swift","ANGER",2), ("Swift","STRIKE_IRONCLAD",1), ("Vigorous","BACKSTAB",2), ("Vigorous","SEVEN_STARS",3)
        };
        foreach (var row in cases) Check(Tier(C(row.Card), row.Enchant) == row.Tier, $"{row.Enchant}/{row.Card} must have tier {row.Tier}");
        Check(Tier(C("WHIRLWIND"), "Slither") == 0, "X must not be mistaken for zero cost");
        var strike = C("STRIKE_IRONCLAD"); strike.UpgradeInternal(); strike.FinalizeUpgradeInternal();
        Check(Tier(strike,"Spiral") == 1, "permanent upgrade must affect Spiral");
        var hologram = C("HOLOGRAM");
        Check(!hologram.IsUpgraded && hologram.Keywords.Contains(CardKeyword.Exhaust), "upgrade inspection must not mutate live card");
        Check(names.Where(n => n is "Clone" or "Glam" or "Goopy" or "Inky" or "TezcatarasEmber").All(n => Tier(strike,n) == 0), "blank preference rows must stay ordinary");
        Console.WriteLine("PASS: enchantment table covers 22 native models and current costs, hits, replay, keywords and upgrades.");
    }

    private static void Ranking()
    {
        var p = Deck("ANGER", "BLUDGEON", "BACKSTAB");
        Check(BotBrain.ScoreDeckCard(p.Deck.Cards[1]) > BotBrain.ScoreDeckCard(p.Deck.Cards[0]), "sample must distinguish tier from original score");
        Check(Pick(p,"Sharp").Cards.Single().Id.Entry == "ANGER", "preference tier must dominate the original score");
        var veto = p.Deck.Cards[2]; veto.BaseReplayCount = 4;
        Check(Tier(veto,"Sharp") == -1, "veto must beat even an extreme preference");
        var optional = Pick(p,"Sharp",0,3);
        Check(optional.Cards.Count == 2 && !optional.Cards.Contains(veto) && !optional.ForcedFallback, "optional selection must fill allowed slots without rejected cards");
        var forced = Pick(p,"Sharp",3,3);
        Check(forced.Cards.Count == 3 && forced.ForcedFallback, "forced selection must meet the native minimum");
        var blocked = Deck("ANGER", "STRIKE_IRONCLAD");
        Check(Pick(blocked,"Slither",0,1).Cards.Count == 0, "optional all-veto must skip");
        var expected = blocked.Deck.Cards.OrderByDescending(BotBrain.ScoreDeckCard).First();
        Check(Pick(blocked,"Slither").Cards.Single() == expected && Pick(blocked,"Slither").ForcedFallback, "forced all-veto must use original scoring");
        var equal = Deck("ANGER", "BOLAS");
        Check(Pick(equal,"Swift").Cards.Single() == equal.Deck.Cards.OrderByDescending(BotBrain.ScoreDeckCard).ThenBy(c=>c.Id.Entry,StringComparer.Ordinal).First(), "same tier must keep original scoring");
        var damage = Deck("TWIN_STRIKE", "STRIKE_IRONCLAD", "WHIRLWIND");
        Check(Pick(damage,"Instinct").Cards.Single().Id.Entry == "WHIRLWIND", "X must precede total damage ranking");
        damage.Deck.RemoveInternal(damage.Deck.Cards[2]);
        Check(Pick(damage,"Instinct").Cards.Single().Id.Entry == "TWIN_STRIKE", "single play damage must sum all hits");
        Check(Pick(damage,"Corrupted").Cards.Single().Id.Entry == "TWIN_STRIKE", "Corrupted must also rank total play damage");
        Console.WriteLine("PASS: enchantment tiers and vetoes override scoring; ties, total play damage and forced minimum behave as specified.");
    }

    private static void Dispatch()
    {
        var p = Deck("ANGER", "BLUDGEON", "BACKSTAB");
        AutoPilot.Set(p.NetId,true);
        try
        {
            var methods = typeof(CardSelectCmd).GetMethods().Where(m => m.Name == nameof(CardSelectCmd.FromDeckForEnchantment)).ToArray();
            foreach (var method in methods)
            {
                var prefs = new CardSelectorPrefs(CardSelectorPrefs.EnchantSelectionPrompt,1);
                object[] args = method.GetParameters()[0].ParameterType == typeof(Player)
                    ? method.GetParameters().Length == 5 ? [p,E("Sharp"),2,(Func<CardModel,bool>)(c=>c.Id.Entry!="BLUDGEON"),prefs] : [p,E("Sharp"),2,prefs]
                    : [p.Deck.Cards.ToArray(),E("Sharp"),2,prefs];
                Check(BotCardChoiceDispatcher.TrySelect(method,args,out var value) && ((IEnumerable<CardModel>)value!).Single().Id.Entry == "ANGER",
                    "every native enchantment overload must carry the enchantment identity");
            }
            var methodWithFilter = methods.Single(m=>m.GetParameters().Length==5);
            Check(BotCardChoiceDispatcher.TrySelect(methodWithFilter,[p,E("Sharp"),2,(Func<CardModel,bool>)(c=>c.Id.Entry=="BLUDGEON"),new CardSelectorPrefs(CardSelectorPrefs.EnchantSelectionPrompt,1)],out var filtered)
                && ((IEnumerable<CardModel>)filtered!).Single().Id.Entry=="BLUDGEON", "native additional filter must remain authoritative");
            // Genuine async construction keeps metadata across deferred evaluation.
            using var pump = new ConstructionParallelScenarios.Pump();
            var selected = pump.Run(()=>ConstructionDecision.RunAsync(p,()=>BotBrain.SelectCards(p,p.Deck.Cards,1,1,"FromDeckForEnchantment:Sharp:2"),()=>"enchant-test"));
            Check(selected.Single().Id.Entry=="ANGER", "async choice must preserve tier priority");
        }
        finally { AutoPilot.Set(p.NetId,false); }
        Console.WriteLine("PASS: all native enchantment selection overloads and asynchronous decisions use the static policy.");
    }

    private static T Event<T>(Player p) where T : EventModel
    {
        var model = (T)ModelDb.Event<T>().ToMutable();
        typeof(EventModel).GetField("<Owner>k__BackingField",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(model,p);
        var options = (IReadOnlyList<EventOption>)AccessTools.Method(typeof(T),"GenerateInitialOptions").Invoke(model,null)!;
        typeof(EventModel).GetField("_currentOptions",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(model,options.ToList());
        return model;
    }

    private static void Events()
    {
        var p = Deck("ANGER", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD"); p.Gold = 100;
        var wood = Event<WoodCarvings>(p);
        var snake = wood.CurrentOptions.Single(o=>o.TextKey.EndsWith(".SNAKE"));
        Check(EnchantmentEventPolicy.ShouldAvoid(p,wood,snake), "event must avoid paying for an all-veto target pool");
        var chosen = (int)AccessTools.Method(typeof(BotEventDriver),"ChooseEventOption").Invoke(null,[p,wood])!;
        Check(wood.CurrentOptions[chosen] != snake, "live event path must respect veto before a special handler");
        var bookPlayer=Deck("BACKSTAB","DEFEND_IRONCLAD","INFLAME");
        var book=Event<SelfHelpBook>(bookPlayer);
        chosen=(int)AccessTools.Method(typeof(BotEventDriver),"ChooseEventOption").Invoke(null,[bookPlayer,book])!;
        Check(!book.CurrentOptions[chosen].TextKey.EndsWith(".READ_THE_BACK"), "book must apply its additional card-type restriction");
        var only=Deck("ANGER"); only.Gold=100;
        var scriptorium=Event<WaterloggedScriptorium>(only);
        var quill=scriptorium.CurrentOptions.Single(o=>o.TextKey.EndsWith(".TENTACLE_QUILL"));
        Check(EnchantmentEventPolicy.ShouldAvoid(only,scriptorium,quill), "generic event must reject zero-cost retain");
        var pael=ModelDb.Event<Pael>();
        var clone=AncientOptionValue.Evaluate(p,pael,pael.AllPossibleOptions.First(o=>o.Relic is PaelsGrowth));
        Check(clone.Value == -100 && clone.Detail.Contains("owner-clone-option-score:-100.00"), "Clone event option must have the fixed score minus 100");
        var targets=Deck("BACKSTAB","STRIKE_IRONCLAD","TWIN_STRIKE","BASH","WHIRLWIND");
        targets.Deck.Cards[0].BaseReplayCount=4;
        var tanx=ModelDb.Event<Tanx>();
        var triple=tanx.AllPossibleOptions.First(o=>o.Relic is TriBoomerang);
        var chosenTargets=Pick(targets,"Instinct",0,3);
        var targetValue=chosenTargets.Cards.Sum(c=>AncientOptionValue.EnchantmentDelta(targets,c,E("Instinct"),1));
        Check(Math.Abs(AncientOptionValue.Evaluate(targets,tanx,triple).Value-targetValue)<1e-8,
            "ancient option must price the targets the priority policy will choose");
        var all=wood.CurrentOptions.Where(o=>ReferenceEquals(o,snake)).ToList();
        typeof(EventModel).GetField("_currentOptions",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(wood,all);
        Check(!EnchantmentEventPolicy.ShouldAvoid(p,wood,snake), "an unavoidable event page must still progress");
        Console.WriteLine("PASS: event planning avoids rejected target pools and prices Clone with the owner penalty.");
    }
}
