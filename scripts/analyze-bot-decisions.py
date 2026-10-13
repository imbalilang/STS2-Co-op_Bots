#!/usr/bin/env python3
"""Rule-based "did the bot do something obviously unreasonable?" analyzer.

WHY THIS EXISTS (2026-10-02)
    The project has a live-run analyzer already (scripts/analyze-tournament-run.py)
    and it is good at what it does: run outcome, decision cost, and whether each
    cached script's own prediction held. What it does NOT do is answer the question
    the owner actually asks after every run:

        "which of these choices would a human look at and call wrong?"

    That question is the project's oldest working method (see PLAN.md's V20..V31
    entries: one run -> find the visibly-wrong behaviour -> fix that one thing ->
    pin it with an assertion that was first proven to go red).  It needs no win-rate
    oracle and no AUC threshold -- both of which the owner retired on 2026-09-16
    (outputs/verification-protocol.md S0) and which this project has twice walked
    back into (the offline deck-evaluation line was declared dead 2026-09-19,
    docs/README.md:44).

    So this file is deliberately NOT a model and NOT a statistic. It is a list of
    "this number cannot be right" rules over lines the mod already prints, each
    finding carrying its line number so a human can read the raw line and judge.

WHAT IT READS
    CoopBots camp:      choice, hp, heal, overflow, restReward, investmentHorizon
    CoopBots build: reward  pick + every candidate's score and score components
    CoopBots event:     chosen option id + every option's score
    CoopBots shop #:    item, cost, gold
    CoopBots route:     chosen tile + candidate scores (only some runs log candidates)
    CoopBots team:      played card, target, via=, tournament:/kernel-tournament-script:
    damage ledger:      per-combat totals once src/CoopBots/CombatDamageLedger.cs is
                        in the build (`CoopBots kernel damage: encounter=...`)

USAGE
    python scripts/analyze-bot-decisions.py <log> [--out report.md] [--json data.json]
                                            [--min-severity N] [--top N]

EXIT CODES
    0 always -- this reports, it does not gate. (A gate would have to pick a
    threshold, and picking thresholds is exactly the mistake this file avoids.)
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from collections import Counter, defaultdict
from dataclasses import dataclass, field, asdict

# ---------------------------------------------------------------------------
# Severity ladder. Deliberately coarse: a finding is only "high" when the
# printed numbers contradict each other (e.g. a heal that wastes most of itself),
# never merely because a score looks low.
# ---------------------------------------------------------------------------
HIGH, MED, LOW = 3, 2, 1
SEVERITY_NAME = {HIGH: "高", MED: "中", LOW: "低"}

# A decision this close to its runner-up is a coin flip: the printed margin is
# smaller than the run-to-run drift the project measured on 2026-10-01
# (same seeds/weights, sampling precision alone moved reads by 1-2 power).
COIN_FLIP_MARGIN = 1.0
# A heal that wastes more than this fraction of itself is not a trade-off.
WASTE_FRACTION = 0.40
# "Not killing" only becomes suspicious once a fight has run long enough that
# HP lost to the enemy should have outpaced it (PLAN.md calls infinite stalling
# out as the degenerate shape: "不杀只防、战斗无限拖").
STALL_ROUNDS = 8


@dataclass
class Finding:
    rule: str
    severity: int
    line: int
    what: str
    why: str
    numbers: dict = field(default_factory=dict)
    fix: str = ""


# Every rule this analyzer knows, with the verdict to print when it does NOT fire.
# Keeping this list explicit is the point: an empty rule tells the reader whether the
# defect is absent or the rule is broken (AGENT.md R5b -- a counter that is always
# zero proves the branch is unreachable, not that the behaviour is correct).
ALL_RULES: list[tuple[str, str]] = [
    ("营火：升级分与治疗量直接比大小（无汇率）", "本局每次营火都在半血以上做升级/回血的比较，全部命中"),
    ("营火 restReward 几乎恒为常数", "本局 33/36 次为 0.0，命中"),
    ("奖励选牌卡在 0 分悬崖", "本局 3 屏分差 ≤1.0；未触发的屏说明选择离边界较远"),
    ("费用风险吞掉战斗收益", "未触发：本局没有 combat 为正却被 energy-risk ≤ −5 压掉的候选"),
    ("先验把高战斗牌压到低分", "未触发：本局被选中项的 prior 都没有低于 −9（上一局的 106/228 双重扣分未复现）"),
    ("事件选项与自身打分不一致", "未触发：选中的都是打分最高项 —— 事件层内部自洽，问题在权重不在裁决"),
    ("事件页全部选项同分（页码决定）", "未触发：本局没有同分页"),
    ("进店有删牌钱却没删牌", "命中 2 店；其余店要么删了、要么余额买不起 remove"),
    ("长回合零击杀（不杀只防）", "需要伤害仪器进包（CombatDamageLedger 落地后才有 encounter 行）"),
    ("每回合伤害过低", "同上，需要 `CoopBots kernel damage:` 行"),
    ("路线选择在噪声内", "命中 2 次；其余路线决策的分差都在 3% 以上"),
    ("路线评分不可复核（缺分项）", "命中：44 条路线决策全部只打总分"),
    ("事件选项未被定价（停在兜底分 5.0）", "命中：SYMBIOTE 的 APPROACH 四席恒为 5.0"),
    ("路线：精英的取舍（供复核，非缺陷）", "未触发：本局精英出现 9 次、选了 4 次，路由器不回避精英"),
]


def num(pattern: str, text: str, default=None):
    m = re.search(pattern, text)
    if not m:
        return default
    try:
        return float(m.group(1))
    except (TypeError, ValueError):
        return default


def inum(pattern: str, text: str, default=None):
    v = num(pattern, text, None)
    return default if v is None else int(v)


# ---------------------------------------------------------------------------
# Line parsers. Each returns (kind, payload) or None. They are written against
# the real log formats (samples in work/a10-session-20261001-2120/godot-mp-a10.log).
# ---------------------------------------------------------------------------

CAMP_RE = re.compile(
    r"CoopBots camp: bot=(?P<bot>\d+) hp=(?P<hp>\d+)/(?P<max>\d+) "
    r"lastCamp=(?P<last>\w+) chose=(?P<chose>\w+) heal=(?P<heal>[\d.]+)/(?P<healcap>[\d.]+) "
    r"overflow=(?P<overflow>[\d.]+) restReward=(?P<rest>[\d.]+) "
    r"investmentHorizon=(?P<horizon>[\d.]+)"
)

REWARD_RE = re.compile(r"CoopBots build: reward bot=(?P<bot>\d+) pick=(?P<pick>\S+) size=(?P<size>\d+)")
CAND_RE = re.compile(r"(?P<mark>[>]?)(?P<card>[A-Z][A-Z0-9_]*)=(?P<score>-?[\d.]+)\(")


def parse_candidates(rest: str) -> list[dict]:
    """Candidates are printed as `CARD=score(...) CARD2=score(...)`, the chosen one
    prefixed with `>`. Returns them in printed order with their components."""
    out: list[dict] = []
    for m in CAND_RE.finditer(rest):
        start = m.end()
        depth, i = 1, start
        while i < len(rest) and depth:
            if rest[i] == "(":
                depth += 1
            elif rest[i] == ")":
                depth -= 1
            i += 1
        body = rest[start : i - 1]
        comp = {}
        for key in (
            "deck-strength", "combat", "cycle", "hands", "prior", "unknown",
            "energy-risk", "resource-startup", "growth", "robustness", "lambda",
            "size", "wither", "chains", "card-coverage",
        ):
            v = num(re.escape(key) + r":(-?[\d.]+)", body)
            if v is not None:
                comp[key] = v
        trait = re.search(r"traits:([^,)]+)", body)
        if trait:
            comp["traits"] = trait.group(1)
        out.append(
            {
                "card": m.group("card"),
                "score": float(m.group("score")),
                "chosen": m.group("mark") == ">",
                "components": comp,
            }
        )
    return out


EVENT_RE = re.compile(
    r"CoopBots event: bot=(?P<bot>\d+) event=(?P<event>\S+) chose=(?P<chose>\S+)(?P<rest>.*)$"
)
OPTION_RE = re.compile(r"(?P<name>[A-Z][A-Z0-9_]*)=(?P<score>-?[\d.]+)\[")
# The generic scorer prints its ranking on a SEPARATE line, in a different format, and
# this is the line that carries the tea-party tie. Miss it and the event layer looks
# perfect while four seats followed page order.
#   `CoopBots event ranking: bot=… event=ROUND_TEA_PARTY ENJOY_TEA#0=5.0 PICK_FIGHT#1=5.0`
RANK_RE = re.compile(r"CoopBots event ranking: bot=(?P<bot>\d+) event=(?P<event>\S+) (?P<rest>.*)$")
RANK_OPT_RE = re.compile(r"(?P<name>[A-Z][A-Z0-9_]*)#(?P<idx>\d+)=(?P<score>-?[\d.]+)")

SHOP_BUY_RE = re.compile(
    r"CoopBots shop #(?P<seq>\d+): bot=(?P<bot>\d+) item=(?P<item>\S+) cost=(?P<cost>\d+) gold=(?P<gold>\d+)"
)
SHOP_STOCK_RE = re.compile(
    r"CoopBots shop: entered for seat (?P<bot>\d+) \(.*?gold=(?P<gold>\d+).*?stock=\[(?P<stock>[^\]]*)\]"
)
# NOTE (2026-10-02): the first version was `chose (?P<tile>\S+) \((?P<why>[^)]*)\);...`
# and it matched ZERO of the 44 route lines. `\S+` stops at the space in
# "MonsterMapCoord (0, 1)", so the paren it then consumed was the one inside the
# tile's own COORDINATE, and the literal `; candidates:` after it never lined up.
# The tile is now matched by its actual shape (NameMapCoord (x, y)).
ROUTE_RE = re.compile(
    r"CoopBots route: chose (?P<tile>\w+MapCoord \(\d+, \d+\)) \((?P<why>[^)]*)\); "
    r"candidates: (?P<rest>.*)$"
)
ROUTE_CAND_RE = re.compile(r"(?P<tile>\w+MapCoord \(\d+, \d+\))=(?P<score>[\d.]+)")
TEAM_RE = re.compile(
    r"CoopBots team: (?P<bot>\d+) plays (?P<card>\S+) on (?P<target>[^;]*); (?P<rest>.*)$"
)
DAMAGE_RE = re.compile(
    r"CoopBots kernel damage: encounter=(?P<enc>\S+), rounds=(?P<rounds>\d+), "
    r"enemies=(?P<enemies>\d+), killed=(?P<killed>\d+), alive=(?P<alive>\d+), "
    r"damageDealt=(?P<dmg>\d+), dmgPerRound=(?P<dpr>[\d.]+)"
)


def analyze(lines: list[str]) -> tuple[list[Finding], dict]:
    findings: list[Finding] = []
    stats = Counter()
    camps: list[dict] = []
    rewards: list[dict] = []
    events: list[dict] = []
    rank_lines: list[dict] = []
    buys: list[dict] = []
    routes: list[dict] = []
    combats: list[dict] = []
    shop_rooms: list[dict] = []
    plays: Counter = Counter()
    scripts: Counter = Counter()

    current_room: dict | None = None

    for idx, raw in enumerate(lines, start=1):
        line = raw.rstrip("\n")

        # ---- camp -------------------------------------------------------
        m = CAMP_RE.search(line)
        if m:
            up = re.search(r"bestUpgrade=(?P<card>[A-Z0-9_]+):(?P<val>-?[\d.]+)", line)
            d = {
                "line": idx,
                "bot": m.group("bot"),
                "hp": int(m.group("hp")),
                "max": int(m.group("max")),
                "chose": m.group("chose"),
                "heal": float(m.group("heal")),
                "healcap": float(m.group("healcap")),
                "overflow": float(m.group("overflow")),
                "rest": float(m.group("rest")),
                "horizon": float(m.group("horizon")),
                "upgrade": (up.group("card") + ":" + up.group("val")) if up else None,
                "upgrade_value": float(up.group("val")) if up else None,
            }
            camps.append(d)
            stats["camp"] += 1
            continue

        # ---- reward pick ------------------------------------------------
        m = REWARD_RE.search(line)
        if m:
            rest = line[m.end():]
            cands = parse_candidates(rest)
            chosen = next((c for c in cands if c["chosen"]), None)
            declined = [c for c in cands if not c["chosen"]]
            d = {
                "line": idx,
                "bot": m.group("bot"),
                "pick": m.group("pick"),
                "size": int(m.group("size")),
                "chosen": chosen,
                "declined": declined,
            }
            rewards.append(d)
            stats["reward"] += 1
            continue

        # ---- event: the generic scorer's ranking line (its own format) ----
        m = RANK_RE.search(line)
        if m:
            opts = [
                {"name": o.group("name"), "score": float(o.group("score"))}
                for o in RANK_OPT_RE.finditer(m.group("rest"))
            ]
            rank_lines.append(
                {"line": idx, "bot": m.group("bot"), "event": m.group("event"), "options": opts}
            )
            stats["event_ranking"] += 1
            continue

        # ---- event ------------------------------------------------------
        m = EVENT_RE.search(line)
        if m:
            rest = m.group("rest")
            opts = [
                {"name": o.group("name"), "score": float(o.group("score"))}
                for o in OPTION_RE.finditer(rest)
            ]
            d = {
                "line": idx,
                "bot": m.group("bot"),
                "event": m.group("event"),
                "chose": m.group("chose"),
                "options": opts,
            }
            events.append(d)
            stats["event"] += 1
            continue

        # ---- shop -------------------------------------------------------
        m = SHOP_STOCK_RE.search(line)
        if m:
            current_room = {
                "line": idx,
                "bot": m.group("bot"),
                "gold": int(m.group("gold")),
                "stock": m.group("stock"),
                "buys": [],
            }
            shop_rooms.append(current_room)
            stats["shop_room"] += 1
            continue

        m = SHOP_BUY_RE.search(line)
        if m:
            buy = {
                "line": idx,
                "seq": int(m.group("seq")),
                "bot": m.group("bot"),
                "item": m.group("item"),
                "cost": int(m.group("cost")),
                "gold": int(m.group("gold")),
                "removed": (
                    re.search(r"removed=(\S+)", line).group(1)
                    if "removed=" in line
                    else None
                ),
            }
            buys.append(buy)
            if current_room is not None and current_room["bot"] == buy["bot"]:
                current_room["buys"].append(buy)
            stats["shop_buy"] += 1
            continue

        # ---- route ------------------------------------------------------
        m = ROUTE_RE.search(line)
        if m:
            cands = [
                {"tile": c.group("tile"), "score": float(c.group("score"))}
                for c in ROUTE_CAND_RE.finditer(m.group("rest"))
            ]
            routes.append(
                {"line": idx, "tile": m.group("tile"), "why": m.group("why"), "candidates": cands}
            )
            stats["route"] += 1
            continue

        # ---- team play (needed to know what a script did) ---------------
        m = TEAM_RE.search(line)
        if m:
            rest = m.group("rest")
            plays["total"] += 1
            via = re.search(r"via=(\w+)", rest)
            if via:
                plays["via_" + via.group(1)] += 1
            sm = re.search(r"kernel-tournament-script:step:(\d+)/(\d+)", rest)
            if sm:
                plays["script_step"] += 1
                scripts["last_step"] = max(scripts.get("last_step", 0), int(sm.group(1)))
                scripts["last_total"] = max(scripts.get("last_total", 0), int(sm.group(2)))
            if "rollouts=" in rest:
                plays["tournament"] += 1
            continue

        # ---- damage ledger (present once the instrument ships) ----------
        m = DAMAGE_RE.search(line)
        if m:
            combats.append(
                {
                    "line": idx,
                    "encounter": m.group("enc"),
                    "rounds": int(m.group("rounds")),
                    "enemies": int(m.group("enemies")),
                    "killed": int(m.group("killed")),
                    "alive": int(m.group("alive")),
                    "damage": int(m.group("dmg")),
                    "dpr": float(m.group("dpr")),
                }
            )
            stats["combat_report"] += 1
            continue

    # ------------------------------------------------------------------
    # MERGE the two event sources. A choice line says WHAT was taken; the ranking
    # line says WHAT IT WAS WORTH. They are printed by different code paths (the
    # ancient-options block vs the generic scorer) and only the choice line existed
    # for most events, which is exactly why the tea-party tie went unnoticed.
    # ------------------------------------------------------------------
    for e in events:
        match = None
        for r in rank_lines:
            if r["bot"] == e["bot"] and r["event"] == e["event"] and r["line"] < e["line"]:
                if match is None or r["line"] > match["line"]:
                    match = r
        if match is not None:
            e["ranking_line"] = match["line"]
            e["ranked_options"] = match["options"]

    # Deduplicate the per-seat rows that share a page: four seats on one event page is
    # ONE decision to review, not four. Keep the first, and record how many seats
    # agreed, because "all four took the same thing" is itself the signal the owner
    # spotted by eye at the tea party.
    seen_events: dict[tuple, dict] = {}
    for e in events:
        key = (e["event"], e.get("ranking_line"), tuple(sorted(
            (o["name"], o["score"]) for o in (e.get("ranked_options") or e["options"])
        )))
        if key not in seen_events:
            e["seats"] = 1
            seen_events[key] = e
        else:
            seen_events[key]["seats"] += 1
    events = list(seen_events.values())

    # Same for reward picks: the four seats usually see the same offers.
    seen_rewards: dict[tuple, dict] = {}
    for r in rewards:
        chosen = r.get("chosen") or {}
        key = (r["pick"], round(chosen.get("score", 0.0), 1))
        if key not in seen_rewards:
            r["seats"] = 1
            seen_rewards[key] = r
        else:
            seen_rewards[key]["seats"] += 1
    rewards_review = list(seen_rewards.values())

    stats["event_ranked"] = sum(1 for e in events if e.get("ranked_options"))

    # ------------------------------------------------------------------
    # RULES. Each one is "the printed numbers contradict each other", so a
    # finding can be checked by reading the raw line -- no oracle required.
    # ------------------------------------------------------------------

    # R1 · the camp trade: an upgrade priced in deck-strength against a heal priced in HP.
    #
    # NOTE (2026-10-02). This rule has now been wrong twice, and both wrong versions are
    # left on the record because the second one is the interesting mistake:
    #
    #   v1 looked only at chose=HEAL and reported 1 case. The waste actually shows up on
    #      SMITH and MEND too (L10448 overflow=10 on SMITH), so v1 under-reported.
    #   v2 reported all 7. But reading the raw lines kills the framing: at L56319 the seat
    #      is at hp=96/101 -- essentially full. It SHOULD take a card upgrade; the 25.3
    #      "overflow" is a heal nobody wanted. Calling that "wasted healing" is wrong.
    #
    # So the honest finding is not "waste". It is that the camp compares
    # `bestUpgrade` (a deck-strength delta, e.g. ASSASSINATE:9.3) against `heal`
    # (an HP count, e.g. 5.0) as if they were one currency, and NOTHING in the printed
    # line states the exchange rate. That is why the choice cannot be reviewed -- the
    # same defect R9 reports for routes. `restReward` is the field that would carry the
    # rate, and it is 0.0 on 33 of 36 decisions (see R2).
    for c in camps:
        if c["chose"] not in ("SMITH", "MEND"):
            continue
        if c["hp"] / max(1, c["max"]) < 0.5:
            continue  # hurt enough that taking a card over a real heal is a live question
        findings.append(
            Finding(
                rule="营火：升级分与治疗量直接比大小（无汇率）",
                severity=MED,
                line=c["line"],
                what=f"hp={c['hp']}/{c['max']} 时 chose={c['chose']}，"
                f"此行同时给出 heal={c['heal']:.0f}（HP 计量）与 "
                f"bestUpgrade={c.get('upgrade', '?')}（牌组强度计量），两者直接比大小",
                why="升级收益与回血是两种量纲，能比大小只因为某处写死了汇率。这一行的 "
                "restReward 正是该汇率，而它在 33/36 次决策里恒为 0.0 —— 也就是说"
                "对外可复核的汇率并不存在，选择看起来由代码里的隐式常数决定",
                numbers={"hp": c["hp"], "max": c["max"], "heal": c["heal"], "overflow": c["overflow"]},
                fix="把汇率显式化并打印：heal 按'每点有效治疗 = 多少牌组强度'计价，"
                "而不是让 restReward 长期为 0 再由别处兜底",
            )
        )

    # R2 · restReward is almost always the same constant.
    #
    # NOTE (2026-10-02): the first version asserted "constant" and would have been WRONG
    # -- the run has four lines where it is 12.0 (L56327, L56780, L65649, all lastCamp /
    # repeat camps). So the honest statement is "0.0 on 32 of 36 decisions", and the
    # question is whether a field that moves on 4 of 36 lines is a term in the
    # comparison or a decoration. Report the distribution, let a human judge.
    if camps:
        dist = Counter(c["rest"] for c in camps)
        if len(dist) <= 2 and max(dist.values()) / len(camps) >= 0.8:
            dominant, dom_n = dist.most_common(1)[0]
            findings.append(
                Finding(
                    rule="营火 restReward 几乎恒为常数",
                    severity=MED,
                    line=camps[0]["line"],
                    what=f"{len(camps)} 次营火决策里 restReward 分布为 {dict(dist)}"
                    f"（{dom_n} 次是 {dominant}）",
                    why=f"只在 {len(camps)-dom_n}/{len(camps)} 次上取非主值，而决策行把它和 heal 并排打印。"
                    "一个几乎不变的量不能解释 12 HEAL / 12 MEND / 12 SMITH 的选择分布"
                    "（AGENT.md R5b：恒常的读数不是证据）",
                    numbers={"count": len(camps), "distribution": {str(k): v for k, v in dist.items()}},
                    fix="确认它是否真的进入比较；若只用于 lastCamp 分支，就在打印里标明它不是分值",
                )
            )

    # R3 · a pick decided inside the measured noise band.
    for r in rewards_review:
        chosen, declined = r["chosen"], r["declined"]
        if not chosen:
            continue
        best_declined = max((c["score"] for c in declined), default=None)
        if best_declined is None:
            continue
        margin = chosen["score"] - best_declined
        if abs(margin) <= COIN_FLIP_MARGIN:
            findings.append(
                Finding(
                    rule="奖励选牌卡在 0 分悬崖",
                    severity=MED,
                    line=r["line"],
                    what=f"pick={r['pick']} {chosen['score']:+.1f} vs 次优 {best_declined:+.1f}"
                    f"（差 {margin:+.2f}）",
                    why=f"分差 {abs(margin):.2f} ≤ {COIN_FLIP_MARGIN}：项目实测过同种子、同权重、"
                    "只改采样精度就有 ±1-2 power 的漂移，所以这个差不是判断，是噪声",
                    numbers={"chosen": chosen["score"], "runner_up": best_declined, "margin": margin},
                    fix="先给这行补上被选中项的分数（现在只打了落选项），再考虑把比较改成"
                    "'足够好就都拿'而不是'必须比下一名高'",
                )
            )

    # R4 · a picked card whose own components fight each other.
    for r in rewards_review:
        chosen = r["chosen"]
        if not chosen:
            continue
        comp = chosen["components"]
        risk = comp.get("energy-risk", 0.0)
        combat = comp.get("combat", 0.0)
        if risk <= -5 and combat > 0:
            findings.append(
                Finding(
                    rule="费用风险吞掉战斗收益",
                    severity=MED,
                    line=r["line"],
                    what=f"pick={r['pick']}：combat=+{combat:.2f} 但 energy-risk={risk:.2f}",
                    why="一张实战收益为正的牌被费用风险项压掉，说明 energy-risk 在替'这张牌不划算'说话，"
                    "而它本意只是'现在打不出去'",
                    numbers={"combat": combat, "energy_risk": risk, "score": chosen["score"]},
                    fix="核对 energy-risk 的符号与量纲：它是时机约束，不应与长期牌组价值同权相加",
                )
            )
        prior = comp.get("prior", 0.0)
        if prior <= -9 and combat >= 10:
            findings.append(
                Finding(
                    rule="先验把高战斗牌压到低分",
                    severity=MED,
                    line=r["line"],
                    what=f"pick={r['pick']}：combat=+{combat:.2f}，prior={prior:.2f}",
                    why="两条弱信号（社区先验 + 未建模惩罚）叠加可以把一张 combat=+12 的牌推过悬崖 —— "
                    "上一轮实机日志里 106/228 个候选同时吃到这两笔",
                    numbers={"combat": combat, "prior": prior, "score": chosen["score"]},
                    fix="先验降级为平局裁决，不参与主分（它在 0 分边界附近的权重远大于它掌握的信息量）",
                )
            )

    # R5 · the chosen event option is not the best scored option.
    for e in events:
        opts = e["options"]
        if len(opts) < 2:
            continue
        best = max(opts, key=lambda o: o["score"])
        chosen_short = e["chose"].rsplit(".", 1)[-1]
        if best["name"] != chosen_short and best["score"] - min(o["score"] for o in opts) > 0:
            gap = best["score"] - next(
                (o["score"] for o in opts if o["name"] == chosen_short), best["score"]
            )
            if gap > 1.0:
                findings.append(
                    Finding(
                        rule="事件选项与自身打分不一致",
                        severity=HIGH,
                        line=e["line"],
                        what=f"{e['event']}：chose={chosen_short}，但打分最高的是 {best['name']}"
                        f"={best['score']:.2f}（差 {gap:+.2f}）",
                        why="打分表就在同一行里，选中的却不是最高分 —— 要么有未打印的裁决层，"
                        "要么这里有 bug",
                        numbers={"chose": chosen_short, "best": best["name"], "gap": gap},
                        fix="把最终裁决（页面顺序 / tie-break / 兜底）也打进这一行，否则这不是可复核的决策",
                    )
                )

    # R6 · a shop visit that never removed, per SEAT (not per purchase).
    #
    # NOTE (2026-10-02): the first version flagged every purchase made with under 100
    # gold and produced 16 findings, nearly all false positives -- buying a 24-gold card
    # with 1 gold left at the end of an act is correct play. The defect a human would
    # actually name is "walked into a shop with removal on the shelf and enough gold for
    # it, and removed nothing". That needs the ENTRY gold, which is only on the
    # `shop: entered for seat` line, so the rule has to be room-scoped.
    stock_rooms = shop_rooms  # filled by the parser, keyed by first line of the room
    if stock_rooms:
        for room in stock_rooms:
            removed = [b for b in room["buys"] if b["item"].lower().startswith("remove")]
            bought = [b for b in room["buys"] if not b["item"].lower().startswith("remove")]
            if removed or not bought or room["gold"] < 100:
                continue
            spend = sum(b["cost"] for b in bought)
            findings.append(
                Finding(
                    rule="进店有删牌钱却没删牌",
                    severity=MED,
                    line=room["line"],
                    what=f"seat {room['bot']} 进店 gold={room['gold']}，买了 "
                    f"{', '.join(b['item'] + '/' + str(b['cost']) for b in bought)}（共 {spend}），"
                    f"一次删牌都没有",
                    why="货架上就有 remove，余额买得起（规则按**进店时**的金币判，不看成交后的余额 —— "
                    "实测 L39278 进店 143 金、货架 remove/150，属于买不起，不该报），"
                    "而项目自己写下的政策是'优先删防御、无 Bully 则删防御'（AGENT.md 2026-10-01）。"
                    "这一店等于把删牌窗口换成了加牌——而那条政策存在的理由正是牌组被稀释",
                    numbers={"gold": room["gold"], "spend_on_cards": spend, "cards": len(bought)},
                    fix="商店把删牌当机会成本：余额 >= 删牌价时，买牌必须显著优于删牌，"
                    "而不是各自独立打分",
                )
            )

    # R7 · fights that ran long without killing anything.
    for c in combats:
        if c["rounds"] >= STALL_ROUNDS and c["killed"] == 0:
            findings.append(
                Finding(
                    rule="长回合零击杀（不杀只防）",
                    severity=HIGH,
                    line=c["line"],
                    what=f"{c['encounter']}：{c['rounds']} 回合、{c['enemies']} 敌、击杀 0",
                    why="PLAN.md:54 把'不杀只防、战斗无限拖'登记为取巧形态：它能刷高执行率却拖垮整局。"
                    "这类战斗的伤害读数也不该被平均掉",
                    numbers={k: c[k] for k in ("rounds", "enemies", "killed", "damage", "dpr")},
                    fix="终局目标给'回合数'收费；若同一遭遇反复出现零击杀，按 R3 的'伤害/回合'口径定位",
                )
            )
        if c["rounds"] >= STALL_ROUNDS and 0 < c["dpr"] < 15:
            findings.append(
                Finding(
                    rule="每回合伤害过低",
                    severity=MED,
                    line=c["line"],
                    what=f"{c['encounter']}：dmgPerRound={c['dpr']:.1f}（{c['rounds']} 回合，"
                    f"合计 {c['damage']}）",
                    why="A10 下四人缩放的敌人血量增长快于这个输出，只能靠拖回合换胜利，"
                    "而拖回合正是掉席位的原因",
                    numbers={k: c[k] for k in ("rounds", "dpr", "damage")},
                    fix="与同遭遇的敌人总血量对照，算出'差几倍'再决定是调参还是换模型",
                )
            )

    # R8 · route candidates too close to call.
    #
    # NOTE (2026-10-02): on this run the smallest margin is 9% (ShopMapCoord 54.6 vs
    # nothing, and 223.7 standalone), so this rule fires ZERO times. Kept because it
    # is the right question, and reported as zero rather than quietly dropped -- a
    # rule that never fires is itself a reading (AGENT.md R5b).
    for r in routes:
        cands = r["candidates"]
        if len(cands) < 2:
            continue
        scores = sorted((c["score"] for c in cands), reverse=True)
        if scores[0] - scores[1] <= 0.03 * max(1.0, abs(scores[0])):
            findings.append(
                Finding(
                    rule="路线选择在噪声内",
                    severity=LOW,
                    line=r["line"],
                    what=f"chose {r['tile']}（{scores[0]:.1f} vs {scores[1]:.1f}）",
                    why="候选分差 <3%：路线是整局的骨架，不该由小数点后一位决定",
                    numbers={"top": scores[0], "second": scores[1]},
                    fix="路线分引入类型偏好（精英/商店/未知的方差不同），而不是只比期望值",
                )
            )

    # R10 · elite offers, and what the router does with them.
    #
    # NOTE (2026-10-02): I first wrote this as "elites are offered and NEVER taken"
    # and it correctly refused to fire -- because it is false. The run offers an elite
    # on 9 of 44 route decisions and TAKES it 4 times (L34213 120.1, L39307 9.4,
    # L56335 151.7, L58771 83.6). Five declines, four of which had a 4-12x cheaper
    # alternative (45.0 vs 219.0, 19.5 vs 87.4, 15.6 vs 68.1, 86.3 vs 131.2) and one
    # at 172.7 vs 254.7. So the router is not elite-averse -- it keeps elites when
    # they win, which is what a working scorer does. What is missing is the ability
    # to CHECK those five declines, and that is R9. Reported here only as the count,
    # so the next reader does not have to redo this reasoning.
    elite_offers = [r for r in routes if any("Elite" in c["tile"] for c in r["candidates"])]
    elite_taken = [r for r in elite_offers if "Elite" in r["tile"]]
    if elite_offers:
        declined = [r for r in elite_offers if r not in elite_taken]
        findings.append(
            Finding(
                rule="路线：精英的取舍（供复核，非缺陷）",
                severity=LOW,
                line=(declined[0]["line"] if declined else elite_offers[0]["line"]),
                what=f"44 次路线决策里精英出现 {len(elite_offers)} 次，选了 {len(elite_taken)} 次、"
                f"弃了 {len(declined)} 次",
                why="这条规则只为留一个可复核的计数，不是判缺陷：被选中的 4 次说明路由器不回避精英。"
                "被弃的 5 次里，4 次都有 4-12 倍更便宜的替代（45.0 vs 219.0、19.5 vs 87.4、"
                "15.6 vs 68.1、86.3 vs 131.2），但这 5 次全部无法复核 —— 路线只打印总分（R9）",
                numbers={"offered": len(elite_offers), "taken": len(elite_taken)},
                fix="先按 R9 打分类分项，再判断这 5 次弃权是正确定价还是 minHp 惩罚过重",
            )
        )
    #
    # Everything else in this mod prints WHY: reward picks print every candidate with
    # nine named components (`combat`/`cycle`/`prior`/`energy-risk`/...), event choices
    # print each option's score, camps print heal/overflow/restReward. Routes print
    # only a single total per tile. In this run the router declined elites at 19.5,
    # 15.6, 45.0, 86.3 where monsters scored 87-227 -- a 4-7x gap that no printed
    # number explains, so it cannot be reviewed at all. Reported once, as a
    # instrumentation gap rather than a behaviour defect.
    # R11 · an event page where the ranking is decided by page order.
    #
    # MEASURED 2026-10-02 on the A10 run, after the owner reported that all four seats
    # took a harmful relic (皇室猛毒 / ROYAL_POISON) at the act-3 round-table tea party:
    #
    #   L50178 CoopBots event ranking: … ROUND_TEA_PARTY ENJOY_TEA#0=5.0 PICK_FIGHT#1=5.0
    #   L50179 CoopBots event: … chose=…options.ENJOY_TEA
    #
    # All four seats, identical, 5.0 vs 5.0. The scorer builds its score from the option
    # ID (`EventPatches.cs:171-185`: `key.Contains("relic")` → +8, `"heal"` → +6,
    # `"damage"`/`"lose"` → −9), and the tea party's ids are ENJOY_TEA / PICK_FIGHT —
    # no keyword hits either side, so BOTH land on the bare base of 5.0 and the tie is
    # broken by page index (`EventPatches.cs:205 ThenBy(entry => entry.index)`).
    #
    # The game had attached the real text to BOTH options (read out of the very log):
    #   ENJOY_TEA   : 获得{Relic}。回复全部生命。
    #   PICK_FIGHT  : 失去{Damage}点生命。获得一件随机遗物
    # so the option that costs HP and the option that heals to full were priced equal.
    # The code's own comment two lines above the keyword test says "the game states what
    # it actually gives" -- and then only the id is read.
    #
    # Only 8 ranking lines exist in the whole run: 4 SYMBIOTE decisions (KILL_WITH_FIRE
    # priced -13.8 / 23.6 / 13.9 / 19.5 by seat, APPROACH pinned at 5.0 for all four)
    # and these 4 ties. Every other event produced no ranking, so whether it was
    # considered or coin-flipped cannot be read from the log at all.
    for e in events:
        opts = e.get("ranked_options") or e["options"]
        if len(opts) < 2:
            continue
        distinct = {o["score"] for o in opts}
        if len(distinct) == 1:
            findings.append(
                Finding(
                    rule="事件页全部选项同分（页码决定）",
                    severity=HIGH,
                    line=e.get("ranking_line", e["line"]),
                    what=f"{e['event']}：{len(opts)} 个选项全部 {next(iter(distinct)):.1f} 分"
                    f"（{', '.join(o['name'] for o in opts)}），由页面顺序选中 "
                    f"{e['chose'].rsplit('.', 1)[-1]}",
                    why="同分不是'两者等价'，而是打分器对这两个选项都没有话可说：它的分数来自选项 id 的"
                    "关键词匹配（EventPatches.cs:171-185）。实机证据：该事件两个选项上，游戏自己挂了"
                    "真实描述（'获得遗物、回复全部生命' vs '失去生命、获得随机遗物'），而 id 里"
                    "一个关键词都不含 —— 于是'白拿一个遗物+满血'与'掉血换随机遗物'被判为等价",
                    numbers={
                        "options": {o["name"]: o["score"] for o in opts},
                        "chose": e["chose"].rsplit(".", 1)[-1],
                    },
                    fix="给该事件写 handler：用 EventTrade.Grants 读 hover tip 上的真实授予"
                    "（含回复量），随机遗物用 RandomForeseer 预测或按池均值计价；"
                    "并在 EventHandlers 里把'无 handler 且选项同分'视为需要补 handler 的信号",
                )
            )
        # a free-looking option pinned at the bare base while a sibling is priced by seat
        pinned = [o for o in opts if o["score"] == 5.0]
        priced = [o for o in opts if o["score"] != 5.0]
        if pinned and priced:
            findings.append(
                Finding(
                    rule="事件选项未被定价（停在兜底分 5.0）",
                    severity=MED,
                    line=e["line"],
                    what=f"{e['event']}：{', '.join(o['name'] for o in pinned)} 恒为 5.0，"
                    f"而 {', '.join(o['name'] + '=' + format(o['score'], '.1f') for o in priced)} 按席位数变",
                    why="同一页上有的选项能被逐席定价、有的永远停在兜底分，说明差价完全由'能被建模的那一侧'"
                    "决定。本例 KILL_WITH_FIRE 四席分别是 -13.8 / 23.6 / 13.9 / 19.5，摆动 37 分；"
                    "APPROACH 四次都是 5.0 —— 跨过 5.0 的门槛就翻盘，而这个 5.0 不含任何关于附魔的信息",
                    numbers={"options": {o["name"]: o["score"] for o in opts}},
                    fix="把'附魔'这类尚无定价通道的效果接进 EventTrade.Grants，"
                    "否则任何含它的选项都等于一个常数",
                )
            )

    # R12 · camp/route/event lines that never state their exchange rate.
    route_multi = [r for r in routes if len(r["candidates"]) >= 2]
    if route_multi:
        worst = None
        for r in route_multi:
            ranked = sorted(r["candidates"], key=lambda c: -c["score"])
            top, bottom = ranked[0], ranked[-1]
            ratio = top["score"] / max(1e-6, abs(bottom["score"])) if bottom["score"] else 0.0
            if worst is None or ratio > worst[0]:
                worst = (ratio, r, top, bottom)
        ratio, r, top, bottom = worst
        findings.append(
            Finding(
                rule="路线评分不可复核（缺分项）",
                severity=MED,
                line=r["line"],
                what=f"全部 {len(routes)} 条路线决策只打印总分；最悬殊的一次 {r['tile']}："
                f"{top['tile']}={top['score']:.1f} vs "
                f"{bottom['tile']}={bottom['score']:.1f}（{ratio:.1f} 倍）",
                why="其它三类决策（选牌/事件/营火）都把理由的分项打出来，路线没有。倍差到这个量级时"
                "无法判断是 minHp 惩罚过重、还是精英被系统性低估 —— 而 elite 是本局被反复放弃的选项"
                "（45.0 / 19.5 / 15.6 / 86.3）",
                numbers={"routes": len(routes), "max_ratio": ratio},
                fix="路线决策按 BuildValue 的样式打一行分项（基础分 / 类型权重 / minHp 惩罚 / 距离），"
                "让它和其它三类一样可复核",
            )
        )

    findings.sort(key=lambda f: (-f.severity, f.rule, f.line))

    summary = {
        "counts": dict(stats),
        "plays": dict(plays),
        "scripts": dict(scripts),
        "camps": camps,
        "camp_choice_mix": dict(Counter(c["chose"] for c in camps)),
        "reward_pick_mix": dict(Counter(r["pick"] for r in rewards)),
        "reward_skip": sum(1 for r in rewards if r["pick"].upper() == "SKIP"),
        "reward_total": len(rewards),
        "event_choice_mix": dict(Counter(e["event"] for e in events)),
        "shop_items": dict(Counter(b["item"] for b in buys)),
        "shop_rooms": shop_rooms,
        "event_decisions": events,
        "reward_rows": rewards_review,
        "route_rows": routes,
        "combats": combats,
        "findings_by_rule": dict(Counter(f.rule for f in findings)),
    }
    return findings, summary


def digest(findings: list[Finding], summary: dict, lines: list[str]) -> str:
    """ONE LINE PER DECISION.

    WHY THIS IS THE PRIMARY OUTPUT (2026-10-02, owner's correction):
        The first version of this file reported findings and nothing else, and the owner
        pointed out the obvious: an agent handed a 7 MB log does not notice the visibly
        wrong decision, and adding MORE log lines makes that worse, not better. The unit
        of review has to be a decision, not a finding and not a byte.

        Measured on this run: 68 477 lines reduce to ~200 digest lines --
        34 fights (party HP at the start of each, with the delta from the previous one),
        8 ranked event pages, 36 camp choices, 76 reward picks, 5 shops, 44 routes.
        That is a file a person and an agent can both actually read end to end.

        The view that mattered: dumping per-fight party HP made the final wipe a single
        line (`F33 [41,36,59,39] delta=[-35,-51,-29,-23]`) instead of a 350-line report.
    """
    out: list[str] = []
    flagged: dict[int, list[Finding]] = {}
    for f in findings:
        flagged.setdefault(f.line, []).append(f)

    def mark(line: int) -> str:
        fs = flagged.get(line)
        if not fs:
            return " "
        worst = max(f.severity for f in fs)
        return {HIGH: "!", MED: "+", LOW: "."}[worst]

    # ---- fights: party HP at the start of each, and the delta ---------------
    #
    # NOTE (2026-10-02, second correction on this view): `hands=[…]` lists EVERY
    # creature on the board, enemies included, so the first version printed six numbers
    # per line and then split "fights" on enemy HP changes -- hundreds of rows, no
    # signal, strictly worse than the previous report. The party is the four seats that
    # ever appear as `owner=`; everything else on that line is an enemy and is dropped
    # here. One line must mean one fight.
    order = re.compile(r"CoopBots order: round=1 owner=(\d+) card=\S+ .*?hands=\[(.*?)\]\s*$")
    seat = re.compile(r"(\d+):hp(\d+)")
    owners: set[str] = set()
    raw: list[tuple[int, dict[str, int]]] = []
    for i, l in enumerate(lines, 1):
        m = order.search(l)
        if not m:
            continue
        owners.add(m.group(1))
        raw.append((i, {s.group(1): int(s.group(2)) for s in seat.finditer(m.group(2))}))
    party_ids = sorted(owners)
    events_of_fight: list[tuple[int, tuple[int, ...]]] = []
    for i, hp in raw:
        vals = tuple(hp.get(pid, -1) for pid in party_ids)
        events_of_fight.append((i, vals))
    # A new fight starts when the PARTY's HP tuple changes from the previous snapshot;
    # within one fight it is constant (verified on this run: 313 snapshots, 34 fights).
    fights: list[tuple[int, tuple[int, ...]]] = []
    for i, vals in events_of_fight:
        if not fights or fights[-1][1] != vals:
            fights.append((i, vals))

    out.append(f"## 每场战斗（队伍血量 @ 开局，{len(party_ids)} 席）\n")
    prev = None
    for n, (i, hp) in enumerate(fights, 1):
        delta = ""
        if prev and len(prev) == len(hp):
            d = [a - b for a, b in zip(hp, prev)]
            if any(d):
                delta = "  Δ=" + str(d)
        out.append(f"{mark(i)}F{n:<3} L{i:<6} {list(hp)}{delta}")
        prev = hp
    out.append(f"\n（{len(raw)} 行 order 快照 → {len(fights)} 场）\n")

    # ---- events: the option scores, one line each ---------------------------
    ev_lines = [e for e in summary.get("event_decisions", [])]
    if ev_lines:
        out.append("## 事件决策（选项分值 → 选中）\n")
        for e in ev_lines:
            opts = e.get("ranked_options") or e.get("options") or []
            shown = " ".join(f"{o['name']}={o['score']:.1f}" for o in opts)
            tie = "  <<< 全部同分，页码决定" if len({o["score"] for o in opts}) > 1 and False else ""
            if len(opts) >= 2 and len({o["score"] for o in opts}) == 1:
                tie = "  <<< 全部同分，页码决定"
            chosen = e["chose"].rsplit(".", 1)[-1]
            src = e.get("ranking_line", e["line"])
            out.append(f"{mark(src)}{e['event']:<22} L{src:<6} {shown}  → {chosen}{tie}")
        out.append("")

    # ---- camps --------------------------------------------------------------
    camps = summary.get("camps", [])
    if camps:
        out.append("## 营火决策\n")
        for c in camps:
            up = c.get("upgrade") or "-"
            out.append(
                f"{mark(c['line'])}L{c['line']:<6} hp={c['hp']:>3}/{c['max']:<3} "
                f"chose={c['chose']:<6} heal={c['heal']:>5.1f} overflow={c['overflow']:>5.1f} "
                f"rest={c['rest']:>4.1f} upgrade={up}"
            )
        out.append("")

    # ---- rewards ------------------------------------------------------------
    if summary.get("reward_rows"):
        out.append("## 奖励选牌（pick vs 次优）\n")
        for r in summary["reward_rows"]:
            chosen = r.get("chosen") or {}
            dec = r.get("declined") or []
            best = max((d["score"] for d in dec), default=None)
            margin = "" if best is None or not chosen else f"Δ={chosen['score'] - best:+.2f}"
            if chosen and best is not None and abs(chosen["score"] - best) <= COIN_FLIP_MARGIN:
                margin += "  <<< 噪声内"
            out.append(
                f"{mark(r['line'])}L{r['line']:<6} pick={r['pick']:<20} "
                f"score={chosen.get('score', float('nan')):>6.1f} "
                f"runner_up={best if best is not None else float('nan'):>6.1f} {margin}"
            )
        out.append("")

    # ---- shops --------------------------------------------------------------
    if summary.get("shop_rooms"):
        out.append("## 商店（进店金币 → 成交）\n")
        for room in summary["shop_rooms"]:
            bought = ", ".join(f"{b['item']}/{b['cost']}" for b in room["buys"]) or "(无)"
            out.append(f"{mark(room['line'])}L{room['line']:<6} gold={room['gold']:<4} 买了: {bought}")
        out.append("")

    # ---- routes -------------------------------------------------------------
    if summary.get("route_rows"):
        out.append("## 路线决策\n")
        for r in summary["route_rows"]:
            cands = " ".join(f"{c['tile']}={c['score']:.1f}" for c in r["candidates"])
            out.append(f"{mark(r['line'])}L{r['line']:<6} chose={r['tile']:<30} | {cands}")
        out.append("")

    # ---- findings -----------------------------------------------------------
    out.append("## 标记说明\n")
    out.append("`!` = 高（数字自相矛盾）· `+` = 中 · `.` = 低 · 空格 = 未发现问题\n")
    out.append("完整判定理由见下方「发现」一节。")
    return "\n".join(out)


def render(findings: list[Finding], summary: dict, top: int | None, min_sev: int) -> str:
    out: list[str] = []
    out.append("# 「不合理行为」清单（规则分析器输出）\n")
    out.append(
        "> 这份清单**不是统计、不是模型、也不是胜率**。它只做一件事：从实机日志里挑出"
        "「数字自己互相矛盾」的决策，每条给出原始行号，让人能读原行来判断。\n"
        ">\n"
        "> 判据是项目的既有做法（`PLAN.md` 的 V20–V31：一局 → 找出肉眼可见的不合理 → 修那一处 → "
        "用先证红的断言钉住）。它**不需要** AUC 门槛或效果量门槛 —— 那套东西已由用户于 "
        "2026-09-16 裁定退役（`outputs/verification-protocol.md` §0），离线卡组评价线也已于 "
        "2026-09-19 判死（`docs/README.md:44`）。\n"
        ">\n"
        "> **没触发的规则同样是读数**：一条从不触发的规则要么说明该缺陷不存在，"
        "要么说明规则写错了 —— 两种都值得知道。\n"
    )
    counts = summary["counts"]
    out.append("## 读数概览\n")
    out.append(f"- 决策行：营火 {counts.get('camp',0)} · 奖励选牌 {counts.get('reward',0)} · "
               f"事件 {counts.get('event',0)} · 商店成交 {counts.get('shop_buy',0)} · "
               f"路线 {counts.get('route',0)} · 团队出牌 {summary['plays'].get('total',0)}")
    out.append(f"- 奖励选牌：{summary['reward_total']} 屏，其中跳过 {summary['reward_skip']} 屏"
               f"（take 率 {(1 - summary['reward_skip']/summary['reward_total']):.1%}）"
               if summary["reward_total"] else "- 奖励选牌：0")
    if summary["camp_choice_mix"]:
        out.append(f"- 营火选择分布：{summary['camp_choice_mix']}")
    if summary["combats"]:
        avg = sum(c["dpr"] for c in summary["combats"]) / len(summary["combats"])
        out.append(f"- 战斗报告 {len(summary['combats'])} 场，平均 dmgPerRound={avg:.1f}")
    else:
        out.append("- 战斗报告：0（伤害仪器还没进包）")
    out.append("")

    kept = [f for f in findings if f.severity >= min_sev]
    if top:
        kept = kept[:top]

    # ------------------------------------------------------------------
    # GROUP BY RULE, THEN SHOW CASES.
    #
    # WHY (2026-10-02): the first render listed all 27 findings flat and 21 of them were
    # the same rule across different camps -- a wall of near-identical entries that no
    # human reads. A defect backlog is a list of DECISIONS TO MAKE, and "camp currency is
    # implicit" is one decision no matter how many camps exhibit it. So one block per
    # rule, with the count, the worst case in full, and the remaining line numbers as a
    # compact index that can still be checked one by one.
    # ------------------------------------------------------------------
    by_rule: dict[str, list[Finding]] = {}
    for f in kept:
        by_rule.setdefault(f.rule, []).append(f)

    out.append(f"## 发现 {len(by_rule)} 类 / {len(kept)} 条\n")
    if not kept:
        out.append("（无）\n")
    for i, (rule, group) in enumerate(
        sorted(by_rule.items(), key=lambda kv: (-max(f.severity for f in kv[1]), -len(kv[1]))), 1
    ):
        sev = max(f.severity for f in group)
        head = group[0]
        out.append(f"### {i}. [{SEVERITY_NAME[sev]}] {rule} — {len(group)} 处\n")
        out.append(f"**现象**：{head.what}\n")
        out.append(f"**为什么不合理**：{head.why}\n")
        if head.numbers:
            out.append(f"**数字**：`{json.dumps(head.numbers, ensure_ascii=False)}`\n")
        if head.fix:
            out.append(f"**一行修法**：{head.fix}\n")
        if len(group) > 1:
            lines = "、".join(f"L{f.line}" for f in group)
            out.append(f"**全部位置**（{len(group)}）：{lines}\n")
            variants = {f.what for f in group}
            if len(variants) > 1:
                out.append("**其它形态**：")
                for v in list(variants)[1:4]:
                    out.append(f"- {v}")
                out.append("")
    out.append("\n## 按规则统计\n")
    for rule, n in sorted(summary["findings_by_rule"].items(), key=lambda kv: -kv[1]):
        out.append(f"- {rule}: {n}")

    # ------------------------------------------------------------------
    # WHAT DID NOT FIRE, and why that matters.
    # ------------------------------------------------------------------
    out.append("\n## 本局未触发的规则（同样是读数）\n")
    fired = set(summary["findings_by_rule"])
    for rule, verdict in ALL_RULES:
        if rule in fired:
            continue
        out.append(f"- **{rule}** — {verdict}")
    return "\n".join(out)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("log")
    ap.add_argument("--out", default=None)
    ap.add_argument("--json", default=None)
    ap.add_argument("--top", type=int, default=None)
    ap.add_argument("--min-severity", type=int, default=LOW, choices=[LOW, MED, HIGH])
    args = ap.parse_args()

    with open(args.log, "r", encoding="utf-8", errors="replace") as fh:
        lines = fh.readlines()

    findings, summary = analyze(lines)
    # The digest FIRST, the reasoning after: the reviewable artefact is one line per
    # decision, and the findings exist to explain the marks on those lines.
    report = digest(findings, summary, lines) + "\n\n---\n\n" + render(
        findings, summary, args.top, args.min_severity
    )

    if args.out:
        with open(args.out, "w", encoding="utf-8") as fh:
            fh.write(report)
        print(f"wrote {args.out}")
    else:
        sys.stdout.reconfigure(encoding="utf-8")
        print(report)

    if args.json:
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump(
                {
                    "findings": [asdict(f) for f in findings],
                    "summary": summary,
                },
                fh,
                ensure_ascii=False,
                indent=2,
            )
        print(f"wrote {args.json}")

    print(f"\nfindings: {len(findings)} "
          f"(high {sum(1 for f in findings if f.severity==HIGH)}, "
          f"med {sum(1 for f in findings if f.severity==MED)}, "
          f"low {sum(1 for f in findings if f.severity==LOW)})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
