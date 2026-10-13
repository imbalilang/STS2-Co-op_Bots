#!/usr/bin/env python3
"""Turn the oracle batch results into the report a person reads.

The shape is deliberate, and comes from the leader's ruling for 派单29 §3:

  1. the ordering, blind — no win/loss labels, because a reader who knows which
     decks really won would be confirming rather than judging;
  2. each deck's full composition, duplicates and upgrade levels spelled out,
     because that is what "is this deck strong" is actually asked about;
  3. the real labels last, in their own section, with the decks the oracle
     disagreed with called out.

The point of the whole exercise is that a human looks at (1) and (2) and says
whether the ordering is sensible. So the report leads with the numbers and the
cards, and keeps the answer key at the back.
"""
import argparse
import io
import json
import os
import zipfile
from collections import Counter

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CARDS_ZIP = os.path.join(REPO, "work", "spire-codex", "export-en.zip")
BATCH = os.path.join(REPO, "work", "oracle", "batch")
OUT = os.path.join(REPO, "outputs", "oracle-small-batch.md")

# The player's HP is the budget the two act-3 bosses are paid out of; a run that
# spends more than this on its pair does not finish, whatever each fight cost
# alone. Single-player A10 Ironclad is 80.
PLAYER_HP = {"IRONCLAD": 80, "SILENT": 70, "DEFECT": 75, "NECROBINDER": 70, "REGENT": 70}


def localised_names():
    with zipfile.ZipFile(CARDS_ZIP) as archive:
        return {c["id"]: c.get("name") or c["id"] for c in json.loads(archive.read("cards.json"))}


def load_rows():
    path = os.path.join(BATCH, "results.jsonl")
    if not os.path.exists(path):
        raise SystemExit(f"no results yet at {path}")
    with io.open(path, encoding="utf-8") as handle:
        return [json.loads(line) for line in handle if line.strip()]


def load_decks():
    """Deck compositions, keyed by run hash.

    The batch writes decks.json only when it finishes, so a run that is stopped
    early would leave the report unable to show any cards — and the cards are the
    half a human is judging. Falling back to the community cache costs a directory
    walk and removes that failure mode entirely.
    """
    path = os.path.join(BATCH, "decks.json")
    if os.path.exists(path):
        with io.open(path, encoding="utf-8") as handle:
            return {deck["run_hash"]: deck for deck in json.load(handle)}
    runs = os.path.join(REPO, "work", "spire-codex", "decks", "runs")
    decks = {}
    for name in os.listdir(runs):
        if not name.endswith(".json"):
            continue
        with io.open(os.path.join(runs, name), encoding="utf-8") as handle:
            deck = json.load(handle)
        decks[deck["run_hash"]] = deck
    return decks


def cost_for(rows, label, boss):
    """HP the fight cost, or None when it did not produce a reading.

    A deck that died spent its whole pool, so the boss's remaining HP is the only
    thing that still separates it from another deck that also died — that is
    reported alongside rather than folded in, because the two are different
    quantities and averaging them would hide exactly the case that matters.
    """
    for row in rows:
        if row["label"] == label and row["boss"] == boss:
            if row["hp_lost"] is None:
                return None, None
            return row["hp_lost"], row.get("final_enemy_hp")
    return None, None


def pair_verdict(costs, hp):
    """A10 draws two act-3 bosses, so clearing means both fights fit in one pool."""
    values = [c for c in costs if c is not None]
    if len(values) < 3:
        return "incomplete"
    pairs = sorted(values)
    worst = pairs[-1] + pairs[-2]
    return f"{worst} {'≤' if worst <= hp else '>'} {hp} {'PASS' if worst <= hp else 'fail'}"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", default=OUT)
    args = parser.parse_args()
    rows, decks, names = load_rows(), load_decks(), localised_names()

    labels = sorted({row["label"] for row in rows},
                    key=lambda name: int(name[1:]) if name[1:].isdigit() else 0)
    bosses = sorted({row["boss"] for row in rows})
    by_label = {label: [row for row in rows if row["label"] == label] for label in labels}

    lines = []
    add = lines.append
    add("# Oracle small batch — 请人工判读")
    add("")
    add(f"共 {len(labels)} 副牌 × {len(bosses)} 个 Act-3 Boss = {len(rows)} 场。"
        "全部为**真实社区牌组原样注入**(含重复牌与升级等级),"
        "**A10、单人、零遗物、零药水**,固定种子,求解器最优出牌。")
    add("")
    add("> 每副牌的数与构成在前,**真实胜负标签在最后一节**。"
        "第一次读时先不看标签 —— 否则判读会变成确认。")
    add("")

    add("## 1. 排序(盲)")
    add("")
    add("**主序 = 三个 Boss 的「剩余血量」之和**(0 表示通关)。")
    add("")
    add("为什么不用血耗排序:阵亡时血必然掉光,`hp_cost` 就等于角色血量池 —— "
        "一个常数,彼此没有区分度(旧批 34 个读数里 20 个是这种)。"
        "Boss 剩余血量在**通关与阵亡两种结局上都定义**,且单调于牌组质量,所以它才是主序。")
    add("")
    add("`血耗` 列保留作参考,**只在 `survived=true` 时有意义**,不参与排序。")
    add("")
    complete, incomplete = [], []
    for label in labels:
        deck = decks[next(row["run_hash"] for row in by_label[label])]
        cells = []
        for boss in bosses:
            row = next((r for r in by_label[label] if r["boss"] == boss), None)
            cells.append(row)
        if any(c is None or c.get("final_enemy_hp") is None for c in cells):
            incomplete.append((label, deck, cells))
            continue
        remaining = sum(c["final_enemy_hp"] for c in cells)
        complete.append((remaining, label, deck, cells))
    complete.sort(key=lambda item: item[0])

    add("| 排序 | 牌组 | 角色 | " + " | ".join(f"{b.replace('_BOSS','')} 剩血" for b in bosses)
        + " | 剩余合计 | 通关数 | " + " | ".join(f"{b.replace('_BOSS','')} 血耗" for b in bosses) + " |")
    add("|---|---|---|" + "---|" * len(bosses) + "---|---|" + "---|" * len(bosses))
    rank_of = {}
    for rank, (remaining, label, deck, cells) in enumerate(complete, 1):
        rank_of[label] = rank
        left = " | ".join("—" if c is None else str(c["final_enemy_hp"]) for c in cells)
        cleared = sum(1 for c in cells if c.get("survived"))
        costs = " | ".join("—" if not c.get("survived") else str(c["hp_lost"]) for c in cells)
        add(f"| {rank} | {label} | {deck['character']} | {left} | {remaining} | {cleared}/{len(bosses)} | {costs} |")
    add("")
    add("`血耗` 各列里阵亡局记 `—`:那些位置的血耗等于血量池,是常数,列出来只会误导。")
    add("")

    if incomplete:
        add("### 未跑完的牌组(不参与排序)")
        add("")
        add("**缺读数的牌组不进排序。** 把缺失当成最差、或按已有读数求和,都会让排序由"
            "「跑没跑完」而不是「牌组强弱」决定 —— 旧批里这两种处理会让同一批数据的 AUC "
            "从 0.667 掉到 0.36。")
        add("")
        add("| 牌组 | 角色 | " + " | ".join(b.replace('_BOSS','') for b in bosses) + " | 原因 |")
        add("|---|---|" + "---|" * len(bosses) + "---|")
        for label, deck, cells in incomplete:
            status = " | ".join("缺" if c is None or c.get("final_enemy_hp") is None else str(c["final_enemy_hp"])
                                for c in cells)
            missing = [c for c in cells if c is None or c.get("final_enemy_hp") is None]
            why = next((c["error"] for c in missing if c is not None), "未跑")
            add(f"| {label} | {deck['character']} | {status} | {why} |")
        add("")
        add("**超时是「测不出来」,不是「这副牌很差」** —— 二者不能混为一谈,所以这些牌组"
            "既不排进名次,也不该被当成最差。要不要重跑见文末。")
        add("")

    add("## 2. 每副牌的完整构成")
    add("")
    ordering = [(label, deck, cells) for _, label, deck, cells in complete]         + [(label, deck, cells) for label, deck, cells in incomplete]
    for rank, (label, deck, cells) in enumerate(ordering, 1):
        left = " / ".join("—" if c is None or c.get("final_enemy_hp") is None
                          else str(c["final_enemy_hp"]) for c in cells)
        add(f"### {label} — {deck['character']}   剩余血量 {left}")
        add("")
        counter = Counter()
        upgraded = Counter()
        for card in deck["deck"]:
            counter[card["id"]] += 1
            if card.get("upgrades"):
                upgraded[card["id"]] += 1
        real = sum(1 for c in deck["deck"] if c.get("upgrades"))
        add(f"牌组 {len(deck['deck'])} 张,**真实升级 {real} 张**"
            f"({real / max(1, len(deck['deck'])):.0%})。")
        add("")
        add("> 求解器收到的就是下列构成:`upgradeLevelsPerCard` 让逐卡等级得以表达,"
            "`loadout.json` 已逐张核对(见 `scripts/combatsolver-oracle-adaptations.json` ORACLE-02)。")
        add("")
        add("```")
        for card_id, count in sorted(counter.items(), key=lambda kv: (-kv[1], kv[0])):
            plus = "+1" if upgraded[card_id] else ""
            add(f"  {count}× {names.get(card_id, card_id)}{plus}  ({card_id})")
        add("```")
        add("")
        add("逐 Boss 实际结局:")
        add("")
        add("| Boss | survived | 剩余血量 | 回合 | 血耗 |")
        add("|---|---|---|---|---|")
        for boss in bosses:
            row = next((r for r in by_label[label] if r["boss"] == boss), None)
            if row is None or row.get("survived") is None:
                add(f"| {boss} | 未跑完 | — | — | — |")
                continue
            add(f"| {boss} | {'是' if row['survived'] else '否'} | {row['final_enemy_hp']} "
                + f"| {row['turns']} | {row['hp_lost']} |")
        add("")
        add("`status: Passed` 一行**故意不报** —— 那是测试框架的结果(开 `AcceptAnyCombatOutcome` "
            "后每场都是 Passed),不是战斗的结果。")
        add("")

    add("## 3. 已知失真(判读前必读)")
    add("")
    add("1. **零遗物、零药水** —— 按设计,这是控制变量(裸牌组纯度检验),不是缺陷。")
    add("2. **求解器仍强于我们的 Bot** —— 每决策 1 秒的 beam 搜索,"
        "绝对值不能当「Bot 会掉这么多血」读,只能用来排序。")
    add("3. **逐 Boss 独立测量**,隐含每个 Boss 都满血进场;真实连战第二场带伤,"
        "所以「三数之和」≠ 真实连战代价。排序仍可比。")
    add("4. **A10 双 Boss 配对未在报告里判过** —— 三数足以算出三个配对,但本轮先给单场读数。")
    add("")

    add("## 4. 真实胜负标签(判读后再看)")
    add("")
    add("| 牌组 | 真实标签 | oracle 排序 | 是否相符 |")
    add("|---|---|---|---|")
    for label in labels:
        deck = decks[next(row["run_hash"] for row in by_label[label])]
        real = "赢家" if deck["win"] else "深败局"
        place = f"{rank_of[label]} / {len(complete)}" if label in rank_of else "未跑完,未排名"
        add(f"| {label} | {real} | {place} | |")
    add("")
    add("**最后一列留给判读的人**:oracle 把真实赢家排在真实深败局之前,算相符。")
    add("")

    add("## 5. 自检")
    add("")
    add("- 与测试员独立读数的对照:**未做** —— 需要他在同一批牌组上跑一遍。"
        "`results.jsonl` 与 `decks.json` 已在 `work/oracle/batch/`,可直接使用。")
    add(f"- 求解设置已固定并记录在每一行:`budget_ms`、`parallelism=1`(并行度已钉,门 1)、"
        "`seed`、`timeout_s`。")
    add("- **门 1/3 未完成**:并行度已显式钉死,但「同种子两跑逐位相同」只在单副败局上验过,**未在本批重复**。")
    add("")

    with io.open(args.out, "w", encoding="utf-8") as handle:
        handle.write("\n".join(lines) + "\n")
    print(f"wrote {args.out} ({len(lines)} lines, {len(labels)} decks)")


if __name__ == "__main__":
    main()
