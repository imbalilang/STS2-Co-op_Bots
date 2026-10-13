using System.Reflection;
using CoopBots;
using CoopBots.Building;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

/// <summary>
/// 只读测量，**不是门禁**（不打印 PASS，不进门禁计数）。用途：复算实机 2026-09-29 那一手
/// "营地锻造 + 事件附魔之后，下一个商店把 BULLET_TIME 删掉" 里 <c>BuildValue.Remove</c> 到底算了什么。
///
/// 牌组取自实机日志 act2 起始那一行（`CoopBots build[act2] … size=20`），被删的那张取删除前的
/// 真实状态：已升级（营地 SMITH）+ 带 PERFECT_FIT（FIELD_OF_MAN_SIZED_HOLES / ENTER_YOUR_HOLE）。
/// 对照物是一张普通打击——同样是这个牌组里"模型能读懂的牌"。
///
/// 跑法：<c>… -- --removal-probe-only</c>
/// </summary>
internal static class RemovalProbeScenarios
{
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static readonly List<string> errors = [];

    private static void Check(bool ok, string message) { if (!ok) errors.Add(message); }

    internal static void Run()
    {
        new Harmony("coopbots.removal.probe.presentation").Patch(
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(RemovalProbeScenarios), nameof(Format)));

        // Deprived, not Silent: CreateForNewRun<Silent> walks the real starting-relic table and
        // needs the PopulateStartingRelics patch the other Silent fixtures install; the probe
        // only needs the card objects, and CreateCard works cross-character in this harness.
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 71, 1));
        var mate = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 72, 2));
        var run = RunState.CreateForTest([bot, mate], seed: "BULLET-TIME-REMOVAL");
        bot.ResetCombatState();
        bot.Creature.SetMaxHpInternal(89); bot.Creature.SetCurrentHpInternal(65);

        // 实机 act2 起始牌组（日志 18309 行），逐张重建。
        foreach (var name in ("ASCENDERS_BANE CLOAK_AND_DAGGER DAGGER_SPRAY+ DEFEND_SILENT DEFEND_SILENT DEFEND_SILENT " +
                              "DEFEND_SILENT DEFEND_SILENT DEFLECT+ GREED INFINITE_BLADES NEUTRALIZE+ SLICE STRIKE_SILENT " +
                              "STRIKE_SILENT STRIKE_SILENT STRIKE_SILENT STRIKE_SILENT SURVIVOR").Split(' '))
        {
            var card = run.CreateCard(ModelDb.AllCards.First(c => c.Id.Entry == name.TrimEnd('+')), bot);
            if (name.EndsWith('+')) card.UpgradeInternal();
            bot.Deck.AddInternal(card);
        }

        // 删除前那一刻的 BULLET_TIME：营地升过级，事件附了 PERFECT_FIT。
        var bullet = run.CreateCard<BulletTime>(bot);
        bullet.UpgradeInternal();
        var perfectFit = ModelDb.Enchantment<PerfectFit>().ToMutable();
        bullet.EnchantInternal(perfectFit, 1);
        perfectFit.ModifyCard();
        bot.Deck.AddInternal(bullet);

        // 每个变体都必须真的在传给 Remove 的那份牌组里，否则"删掉它"是个空操作，
        // 结果恒为 0——第一版探针就踩了这个坑（隔离项全 0.00，是假的）。
        var deck = bot.Deck.Cards.Where(c => !ReferenceEquals(c, bullet)).ToList();
        var withBullet = deck.Append(bullet).ToList();
        var bulletValue = BuildValue.Remove(bullet, bot, withBullet);
        Describe("BULLET_TIME+ + PERFECT_FIT (as removed)", bulletValue);
        var strikeValue = BuildValue.Remove(deck.First(c => c.Id.Entry == "STRIKE_SILENT"), bot, withBullet);
        Describe("plain STRIKE_SILENT", strikeValue);
        var defendValue = BuildValue.Remove(deck.First(c => c.Id.Entry == "DEFEND_SILENT"), bot, withBullet);
        Describe("plain DEFEND_SILENT", defendValue);

        // 升级同样使用卡组差值；未建模部分必须保留明确的估计项。
        var upgradable = run.CreateCard<BulletTime>(bot);
        var upgradeBullet = BuildValue.UpgradeDelta(upgradable, bot, withBullet);
        DescribeUpgrade("BULLET_TIME (unmodeled)", upgradeBullet);
        var upStrike = run.CreateCard(ModelDb.Card<StrikeSilent>(), bot);
        var upgradeStrike = BuildValue.UpgradeDelta(upStrike, bot, withBullet);
        DescribeUpgrade("plain STRIKE_SILENT (modeled)", upgradeStrike);
        var upNeutralize = run.CreateCard(ModelDb.Card<Neutralize>(), bot);
        var upgradeNeutralize = BuildValue.UpgradeDelta(upNeutralize, bot, withBullet);
        DescribeUpgrade("NEUTRALIZE (modeled)", upgradeNeutralize);

        // 用户 2026-09-30：没建模的牌，估值不得低于基础牌（打击/防御）。
        var floor = Math.Min(strikeValue.Total, defendValue.Total);
        Check(bulletValue.Total <= floor,
            $"没建模的牌不得比基础牌更该删：BULLET_TIME={bulletValue.Total:F2} 基础牌下限={floor:F2}");
        // 获取、删除、升级均记录同一套卡组项，未知效果不当作空白牌。
        Check(bulletValue.Reason.Contains("unknown:", StringComparison.Ordinal),
            $"删牌：读不懂的牌不得把实验份额当满额，reason={bulletValue.Reason}");
        Check(strikeValue.Reason.Contains("deck-strength:", StringComparison.Ordinal)
            && defendValue.Reason.Contains("deck-strength:", StringComparison.Ordinal),
            "已建模的删牌也必须来自公共卡组差值");
        Check(upgradeBullet.Total > 0 && upgradeBullet.Reason.Contains("unknown:", StringComparison.Ordinal),
            $"升级：读不懂的牌仍需保留原生费用改善，reason={upgradeBullet.Reason}");
        Check(upgradeStrike.Reason.Contains("deck-strength:", StringComparison.Ordinal)
            && upgradeNeutralize.Reason.Contains("deck-strength:", StringComparison.Ordinal),
            "升级：已建模的牌也必须来自公共卡组差值");
        if (errors.Count > 0) throw new Exception(string.Join(Environment.NewLine, errors));
        Console.WriteLine("PASS: an unmodeled card is never priced as a better removal than a plain Strike/Defend.");
    }

    private static void DescribeUpgrade(string label, BuildValue.Valuation value)
        => Console.WriteLine($"PROBE upgrade[{label}] total={value.Total:F2} reason={value.Reason}");

    private static void Describe(string label, BuildValue.Valuation value)
        => Console.WriteLine($"PROBE remove[{label}] total={value.Total:F2} reason={value.Reason}");
}
