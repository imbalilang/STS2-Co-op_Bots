#!/usr/bin/env python3
"""Import which cards PRODUCE a build's exclusive resource and which SPEND it.

Source: work/spire-codex/export-en.zip (cards.json), the same snapshot the
archetype clusters are baked from.

Why this exists: a payoff card is only worth its printed numbers if the deck can
pay for it. The scorer had no notion of that at all, so a Regent who lost its
Star producers still priced Seven Stars (7x7 AoE, 7 Stars) as a premium card —
it can never be cast. Card text is the only place production is recorded
(`Gain [star:1]`), while consumption is recorded in the `star_cost` field, so the
two sides are extracted differently on purpose.

The output is a plain table; it is not consulted at runtime by regex. A drift in
the game's card text is caught by a coverage assertion in the test suite, the
same way the archetype and event tables are guarded.

Usage:
    python scripts/import-spire-codex-resources.py
"""
import json
import re
import zipfile

CARD_DUMP = "work/spire-codex/export-en.zip"
OUT = "src/CoopBots/Building/BakedResources.cs"
# colorless 也必须扫:每个职业的卡池里都有无色牌(如 PROWESS 产力量),
# 只扫五个职业色会让这一整类牌在每一行里都不可见。
COLORS = ("ironclad", "silent", "defect", "necrobinder", "regent", "colorless")

# (resource, producer pattern, mention pattern, what the resource is)
#
# The payoff side is derived: a card that MENTIONS the resource without
# producing it is one whose value depends on the stock existing ("Shivs deal 4
# additional damage" is worth nothing with no Shivs). Hand-writing a payoff
# regex per resource was tried and abandoned — the shapes differ too much
# ("for each Channeled Orb", "Poison is triggered an extra time") and guessing
# them is how the producer side got ROYAL_GAMBLE wrong.
# Producers CREATE the stock; payoffs need stock to exist to be worth anything.
# "Channeled" is excluded from the orb producer pattern because Barrage reads
# "for each Channeled Orb" — that is consumption, not production.
# 手工校白的 shiv 角色分类 —— 自动正则在这里必然出错,原因是**一个标签压了三种含义**:
# 13 张带 `shiv` 标签的牌里既有产者、又有乘数器、又有重放器,还有资源本身(SHIV 白卡)。
# 旧的 `Routes` 拿 `shiv` 当 payoff 并要求 >=2 张,于是"两张白卡"和
# "产者+ACCURACY+KNIFE_TRAP"评分完全相同 —— 而这两者实测 OR 是 0.45 vs 3.11。
# 分类逐张核对卡面文本(与测试员的 role-table 对照过)。
SHIV_ROLES = {
    # 产者:把资源造出来
    "producers": ["BLADE_DANCE", "BLADE_OF_INK", "CLOAK_AND_DAGGER", "FAN_OF_KNIVES",
                  "HIDDEN_DAGGERS", "INFINITE_BLADES", "LEADING_STRIKE", "STORM_OF_STEEL",
                  "UP_MY_SLEEVE"],
    # 乘数器:让每张小刀更值钱。FAN_OF_KNIVES 同时是产者(Add 4 Shivs)与乘数器(改为打全体),
    # 两类都在 —— 单一标签恰恰表达不了这种重叠。
    "multipliers": ["ACCURACY", "FAN_OF_KNIVES", "PHANTOM_BLADES"],
    # 重放器:把已经积累的存量一次性变现
    "replayers": ["KNIFE_TRAP"],
    # SHIV 白卡本身不进任何一类:它【就是】资源,不是资源的来源。
    # 把它算进去,会让"包件完整"被两张白卡满足 —— 那正是旧 Route 的毛病。
}

# 毒包。同一套三拆,判据同样是逐张核对卡面文本。
POISON_ROLES = {
    "producers": ["BOUNCING_FLASK", "BUBBLE_BUBBLE", "CORROSIVE_WAVE", "DEADLY_POISON",
                  "ENVENOM", "HAZE", "NOXIOUS_FUMES", "POISONED_STAB", "SNAKEBITE"],
    # 乘数器:让每一层毒多触发一次。与 ACCURACY 对小刀是同一个位置。
    "multipliers": ["ACCELERANT"],
    # 重放器:空 —— 这是个发现,不是遗漏。
    # MIRAGE 是"把已有毒换成格挡"(converter),OUTBREAK 是"每施毒 3 次打 11"(frequency):
    # 两者都是斜坡式变现,没有一张牌能把积累的毒一次性打出去。
    # 小刀之所以有 OR 3.11 那一级,靠的正是 KNIFE_TRAP 这种 burst 变现。
    # 毒没有对应的 burst —— 这可能是它实测 -18pt(而小刀 +31pt)的结构原因。
    "replayers": [],
}


# ---- 派单13 更正 + 派单16:加第 4 个桶 Converter(只做数据分类,不进评分) ----
# 根因:三个桶装不下"读一个存量、换成另一种产出"的牌。清 `Payoffs` 会让
# MIRAGE 这类牌从表里消失 —— 三桶之外的残渣必须有个去处,否则就是删数据。
CONVERTERS = {
    # "Gain Block equal to Poison on ALL enemies" —— 读毒层,产出格挡
    "poison": ["MIRAGE"],
    # "Deal damage equal to the enemy's Doom" —— 读灾厄层,产出伤害
    # "If you applied Doom this turn, gain Block" / "Whenever you apply Doom, gain 2 Block"
    "doom": ["TIME'S UP", "DEATHS_DOOR", "SHROUD"],
}
# 三桶之外的残渣自动进 Converter,所以清 `Payoffs` 不再丢牌。
# 但有两张**读的资源轴不在这张表里**,硬挂任何一个资源行都会假装存在那条存量,
# 所以单独列出、不入行 —— 见回执 §3:
# MOLTEN_FIST 与 BULLY 都挂在【易伤】这条轴上,而本表没有 vulnerable 行。
# 用户指认的 DOMINATE+MOLTEN_FIST 指数成长就跑在这条轴上:
#   MOLTEN_FIST「Double the enemy's Vulnerable」× DOMINATE「Gain 1 Strength for each Vulnerable」
# 把它挂进 strength 行会假装它是力量牌 —— 它一个字都没提力量。
UNFILED_CONVERTERS = ["BULLY", "SUPERMASSIVE", "MOLTEN_FIST"]

# 重新分桶:测试员指认为「指数引擎」、却躺在 Payoffs 里的四张。
# 判据是卡面原句,便于独立复核。
REBUCKET = {
    # "plus an additional 5 Doom for every 10 Doom [on the target]" → 读当前层数,乘数器。
    "doom": {"multipliers": ["NO_ESCAPE"]},
    # "Channel Lightning equal to the Lightning already Channeled this combat"
    #   → 测试员判 R(读已积累的量并一次产出),不是 M。采纳他的判读。
    "orb": {"replayers": ["VOLTAIC"]},
    # "Deals 2 additional damage for each Soul in your Exhaust Pile" → 兑现式,重放器。
    "soul": {"replayers": ["SOUL_STORM"]},
}


# 其余资源的 M/R 暂不填。一个形状验证链路,不一次全做。

# 铁甲力量。判据逐张核对卡面文本。
STRENGTH_ROLES = {
    "producers": ["ARSENAL", "BRAND", "BULK_UP", "DEMON_FORM", "DOMINATE", "FIGHT_ME",
                  "INFLAME", "MONOLOGUE", "PROWESS", "RESONANCE", "RUPTURE", "SETUP_STRIKE"],
    # 乘数器:**空** —— 这个 build 里没有"让你的力量翻倍"的牌。
    # 力量真正的乘数不是一张牌,而是一个**属性**:攻击段数。每点力量按段数重复结算,
    # 所以"多段攻击"才是力量的放大器 —— 它以角色(multihit)表达,不是一个牌集。
    "multipliers": [],
    # 重放器 = 多段攻击:它把**整份已积累的力量**在一次出牌里按段数全部打出。
    # 这与 KNIFE_TRAP 是同一种形状(读存量、一次全部兑现),所以进 R 而不是 M。
    "replayers": ["CONFLAGRATION", "DISMANTLE", "FIGHT_ME", "SPITE", "SWORD_BOOMERANG",
                  "TEAR_ASUNDER", "THRASH", "TWIN_STRIKE", "WHIRLWIND"],
    # DOMINATE 同时是产者(它确实给力量)与转换器(易伤层→力量)。
    # 与 FAN_OF_KNIVES 同理:一张牌跨两桶是真实结构,单一标签表达不了。
    "converters": ["DOMINATE"],
}
EXTRA_ROLES = {
    "shiv": (SHIV_ROLES["multipliers"], SHIV_ROLES["replayers"]),
    "poison": (POISON_ROLES["multipliers"], POISON_ROLES["replayers"]),
    "strength": (STRENGTH_ROLES["multipliers"], STRENGTH_ROLES["replayers"]),
}
CONVERTERS["strength"] = STRENGTH_ROLES["converters"]
for _r, _bucket in REBUCKET.items():
    _m, _x = EXTRA_ROLES.get(_r, ([], []))
    EXTRA_ROLES[_r] = (_m + _bucket.get("multipliers", []), _x + _bucket.get("replayers", []))
# 手工覆盖产者集的资源(自动正则在"一个标签压多种角色"处必然出错)
HAND_PRODUCERS = {"shiv": SHIV_ROLES["producers"], "poison": POISON_ROLES["producers"],
                  "strength": STRENGTH_ROLES["producers"]}

RESOURCES = [
    ("star", r"gain\s+\d*\s*\[star:\d+\]", r"\[star:\d+\]", "Regent Stars"),
    ("orb", r"channel(\[/gold\])?\s+\d", r"\borb\b|evoke|channel", "Defect orbs"),
    ("shiv", r"add\s+.*?shiv", r"shiv", "Silent Shivs"),
    ("poison", r"apply\s+\d*\s*.*?poison", r"poison", "Poison"),
    ("doom", r"apply\s+.*?\[gold\]doom", r"doom", "Necrobinder Doom"),
    ("soul", r"add\s+.*?souls?", r"soul", "Necrobinder Souls"),
    ("focus", r"gain\s+\d*\s*.*?focus", r"focus", "Defect Focus (amplifier)"),
    ("strength", r"gain\s+\d*\s*.*?strength", r"strength", "Strength"),
    ("hp-loss", r"lose \d+ hp", r"lose \d+ hp|whenever you lose hp", "Ironclad self-damage"),
]


def main() -> int:
    with zipfile.ZipFile(CARD_DUMP) as archive:
        cards = json.loads(archive.read("cards.json"))

    def text(card):
        return (card.get("description") or "") + "\n" + (card.get("upgrade_description") or "")

    rows = []
    for name, producer, mention, label in RESOURCES:
        produced, consumed = [], []
        for card in cards:
            if card.get("color") not in COLORS:
                continue
            cid = card["id"]
            if re.search(producer, text(card), re.IGNORECASE):
                produced.append(cid)
            if name == "star":
                # The consumer side is a field, not prose: a card that COSTS
                # stars says so in star_cost and mentions nothing in its text.
                if (card.get("star_cost") or 0) > 0 or card.get("is_x_star_cost"):
                    consumed.append(cid)
            elif cid not in produced and re.search(mention, text(card), re.IGNORECASE):
                consumed.append(cid)
        if not produced and not consumed:
            continue
        owners = sorted({card["color"] for card in cards
                         if card["id"] in set(produced) and card.get("color") in COLORS})
        rows.append((name, label, owners, sorted(produced), sorted(consumed)))

    for name, label, owners, produced, consumed in rows:
        print(f"{name:10s} producers={len(produced):3d} payoffs={len(consumed):3d} owners={owners}  ({label})")

    lines = [
        "namespace CoopBots.Building;",
        "",
        "// GENERATED by scripts/import-spire-codex-resources.py from the Spire Codex snapshot.",
        "// Do not edit by hand: re-run the script and let the coverage assertion in",
        "// the test suite confirm every id still exists in this game build.",
        "//",
        "// Producers create a build's exclusive resource; payoffs are only worth",
        "// their printed numbers when the deck can pay for it. Star consumption is",
        "// the `star_cost` field rather than prose, which is why the two sides are",
        "// extracted differently.",
        "internal static class BakedResources",
        "{",
        "    internal sealed record Resource(string Name, string Label, string[] Owners, string[] Producers,",
        "        string[] Multipliers, string[] Replayers, string[] Converters,",
        "        string[] Spends);",
        "",
        "    internal static readonly Resource[] All =",
        "    [",
    ]
    for name, label, owners, produced, consumed in rows:
        def fmt(ids):
            return "[" + ", ".join(f'"{i}"' for i in ids) + "]"
        ownerList = "[" + ", ".join(f'"{o}"' for o in owners) + "]"
        if name in HAND_PRODUCERS:
            produced = sorted(HAND_PRODUCERS[name])
        mult, repl = EXTRA_ROLES.get(name, ([], []))
        HAND_CONVERTERS = CONVERTERS
        # 这个字段只对 star 有意义(由 star_cost 导出 = "消耗该资源")。
        # 其余资源上它曾是"提到但没产出它"的正则结果 —— 与 M/R 语义重叠又更松,
        # 消费方读它会和读 M/R 得到两个都"对"的答案。清空,只留 star。
        # 三桶之外的残渣 —— 不丢,进 Converter
        covered = set(produced) | set(mult) | set(repl)
        conv = sorted(set(HAND_CONVERTERS.get(name, [])) | {c for c in consumed if c not in covered})
        if name != "star":
            consumed = []
        lines.append(f'        new("{name}", "{label}", {ownerList}, {fmt(produced)}, '
                     f'{fmt(mult)}, {fmt(repl)}, {fmt(conv)}, {fmt(consumed)}),')
    lines += ["    ];", "}", ""]
    with open(OUT, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(lines))
    print(f"wrote {OUT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
