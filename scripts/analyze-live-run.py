#!/usr/bin/env python3
"""One live run, read end to end: the game log AND the damage meter, in one report.

WHY THIS EXISTS
---------------
The two halves answer different halves of the same question and were being read
separately, which twice produced a wrong conclusion:

  * `godot.log` says what the BOT DID (which planner decided, whether the
    tournament fell back to the legacy planner, how many joint searches
    produced no route, what the drafter picked and why).
  * `DamageMeter_combat_state.json` says what the decks PRODUCED (damage dealt
    and taken per seat, damage per card, damage by card, energy wasted). It is
    the authoritative output measurement; reconstructing it from the log is
    both slower and less accurate.

Reading only the log once led to "the loss was a route-planning bug" when the
route was already camp-maximising and the real cause was that every deck dealt
~6 damage per card. Reading only the meter cannot say WHY a deck ended up
without damage. Together they close the loop.

USAGE
-----
    python scripts/analyze-live-run.py                    # newest log + meter
    python scripts/analyze-live-run.py --log <path>
    python scripts/analyze-live-run.py --meter <path>
    python scripts/analyze-live-run.py --only damage,planner
    python scripts/analyze-live-run.py --no-color          # for files

Sections: run, planner, combats, damage, decisions, decks.

Paths default to the live install:
  log   %APPDATA%/SlayTheSpire2/logs/godot.log
  meter %APPDATA%/SlayTheSpire2/DamageMeter_combat_state.json

The log ROLLS. `godot.log` is the current session; `godot<timestamp>.log` are
finished ones. The report prints the mtime of every candidate so a reading made
against the wrong file is visible immediately.
"""

from __future__ import annotations

import argparse
import datetime
import json
import os
import re
import sys
from collections import Counter, defaultdict

# ---------------------------------------------------------------------------
# paths

APPDATA = os.path.join(os.environ.get("APPDATA", os.path.expanduser("~")), "SlayTheSpire2")
DEFAULT_LOG_DIR = os.path.join(APPDATA, "logs")
DEFAULT_LOG = os.path.join(DEFAULT_LOG_DIR, "godot.log")
DEFAULT_METER = os.path.join(APPDATA, "DamageMeter_combat_state.json")

SECTIONS = ["run", "planner", "combats", "damage", "decisions", "decks"]


def out(text: str = "") -> None:
    # The console on this machine is cp936; force UTF-8 so CJK card names survive
    # without every caller remembering PYTHONIOENCODING.
    try:
        sys.stdout.write(text + "\n")
    except UnicodeEncodeError:
        sys.stdout.write(text.encode("utf-8", "replace").decode("utf-8", "replace") + "\n")


def head(title: str) -> None:
    out()
    out("=" * 78)
    out(title)
    out("=" * 78)


# ---------------------------------------------------------------------------
# log model

class Order:
    __slots__ = ("round", "owner", "card", "party", "energy", "enemy")

    def __init__(self, rnd, owner, card, party, energy, enemy):
        self.round, self.owner, self.card = rnd, owner, card
        self.party, self.energy, self.enemy = party, energy, enemy


class Encounter:
    def __init__(self, name, line):
        self.name, self.line = name, line
        self.orders: list[Order] = []
        self.enemy_hp_max = 0

    @property
    def rounds(self) -> int:
        return len({o.round for o in self.orders})

    def round_series(self):
        """(round, enemy HP at first/last order, party HP at first/last order)."""
        by = defaultdict(list)
        for o in self.orders:
            by[o.round].append(o)
        for rnd in sorted(by):
            rows = by[rnd]
            yield rnd, rows[0].enemy, rows[-1].enemy, sum(rows[0].party.values()), sum(rows[-1].party.values())


RE_ENCOUNTER = re.compile(r"ActiveCombat encounter=([A-Z_]+)")
RE_ORDER = re.compile(
    r"CoopBots order: round=(\d+) owner=(\d+) card=([A-Z_]+).*?hands=\[(.*?)\] enemies=\[(.*?)\]")
RE_SEAT = re.compile(r"(\d+):hp(\d+):b(\d+):e(\d+)")
RE_EHP = re.compile(r"hp(\d+)")
RE_OUTCOME = re.compile(r"run outcome — victory=(true|false) act=(\d+) floor=(\d+)")
RE_ASC = re.compile(r"Ascension: (\d+)")
RE_SEED = re.compile(r"Seed: ([A-Z0-9]+)")
RE_FALLBACK = re.compile(r"falling back for this tick — (.*)")
RE_JOINT = re.compile(
    r"joint search: phase=(\w+) nodes=(\d+) prefixes=(\d+) maxActions=(\d+) selected=(\w+) "
    r"stop=(\S+) paths=(\d+)/(\d+)")
RE_BUDGET_CUTOFF = re.compile(r"budgetCutoffs=(\d+)")
RE_REFERENCE = re.compile(r"reference=(\S*)")
RE_DECISION_MS = re.compile(r"decision-ms=([\d.]+)")
RE_ROUTE = re.compile(r"route: chose (\w+)\((\d+), (\d+)\) \(minHp=(\d+) %\)")
RE_CAMP = re.compile(
    r"camp: bot=(\d+) hp=(\d+)/(\d+) .*?chose=(\w+) heal=([\d.]+)/([\d.]+) overflow=([\d.]+)")
RE_SHOP = re.compile(r"shop #\d+: bot=(\d+) item=(\w+) cost=(\d+)")
RE_EVENT = re.compile(r"event: bot=(\d+) event=([A-Z_0-9]+) chose=(\S+)")
RE_REWARD = re.compile(r"build: reward bot=(\d+) pick=(\w+) size=(\d+).*?\| (.*)$")
RE_REWARD_ENTRY = re.compile(r"(>?)([A-Z_0-9]+)=(-?[\d.]+)\(")
RE_BUILD = re.compile(r"build\[([a-z0-9]+)\]: (\d+) size=(\d+) route=(.*?) :: (.*)")

# Every channel a simulation failure can leave a trace in. Their ABSENCE is the
# evidence that a given guarded entry point did not throw (see the triage skill).
CHANNELS = {
    "prediction-exception": "KernelSession.Play mirror threw (boundary text)",
    "round settlement exception": "KernelSession.EndTurn threw (logged with stack)",
    "potion-exception": "KernelSession.UsePotion threw (boundary text)",
    "惰性创建": "power materialisation guard fired (names the power)",
}


def parse_log(path: str) -> dict:
    d = {
        "path": path,
        "encounters": [], "outcome": None, "ascension": None, "seed": None,
        "fallbacks": [], "via": Counter(), "channels": Counter(),
        "joint": [], "decision_ms": [], "routes": [], "camps": [],
        "shops": [], "events": [], "rewards": [], "builds": [],
        "order_count": 0, "termination": "unknown",
    }
    enc = None
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            if not line.startswith("[INFO]") and "[ERROR]" not in line and "[WARN]" not in line:
                continue
            if enc is not None and line.startswith("[INFO] CoopBots order:"):
                m = RE_ORDER.search(line)
                if m:
                    party = {int(x[0]): int(x[1]) for x in RE_SEAT.findall(m.group(4))}
                    energy = sum(int(x[3]) for x in RE_SEAT.findall(m.group(4)))
                    enemy = sum(int(x) for x in RE_EHP.findall(m.group(5)))
                    enc.orders.append(Order(int(m.group(1)), m.group(2), m.group(3), party, energy, enemy))
                    enc.enemy_hp_max = max(enc.enemy_hp_max, enemy)
                    d["order_count"] += 1

            m = RE_ENCOUNTER.search(line)
            if m:
                enc = Encounter(m.group(1), line)
                d["encounters"].append(enc)
                continue
            m = RE_OUTCOME.search(line)
            if m:
                d["outcome"] = (m.group(1) == "true", int(m.group(2)), int(m.group(3)))
            m = RE_ASC.search(line)
            if m:
                d["ascension"] = int(m.group(1))
            m = RE_SEED.search(line)
            if m and d["seed"] is None:
                d["seed"] = m.group(1)
            m = RE_FALLBACK.search(line)
            if m:
                d["fallbacks"].append(m.group(1))
            for key in CHANNELS:
                if key in line:
                    d["channels"][key] += 1
            m = RE_JOINT.search(line)
            if m:
                d["joint"].append({
                    "phase": m.group(1), "nodes": int(m.group(2)), "prefixes": int(m.group(3)),
                    "selected": m.group(5) == "True", "stop": m.group(6),
                    "paths": f"{m.group(7)}/{m.group(8)}",
                    "cutoffs": int(RE_BUDGET_CUTOFF.search(line).group(1)) if RE_BUDGET_CUTOFF.search(line) else -1,
                    "reference": RE_REFERENCE.search(line).group(1) if RE_REFERENCE.search(line) else "?",
                })
            m = RE_DECISION_MS.search(line)
            if m:
                d["decision_ms"].append(float(m.group(1)))
            if "via=" in line:
                if "via=legacy" in line:
                    d["via"]["legacy"] += 1
                elif "via=kernel" in line:
                    d["via"]["kernel"] += 1
            m = RE_ROUTE.search(line)
            if m:
                d["routes"].append((m.group(1), f"({m.group(2)},{m.group(3)})", int(m.group(4))))
            m = RE_CAMP.search(line)
            if m:
                d["camps"].append((m.group(1), int(m.group(2)), int(m.group(3)), m.group(4),
                                   float(m.group(5)), float(m.group(6)), float(m.group(7))))
            m = RE_SHOP.search(line)
            if m:
                d["shops"].append((m.group(1), m.group(2), int(m.group(3))))
            m = RE_EVENT.search(line)
            if m:
                d["events"].append((m.group(1), m.group(2), m.group(3)))
            m = RE_REWARD.search(line)
            if m:
                rest = m.group(4)
                parts = list(RE_REWARD_ENTRY.finditer(rest))
                entries = []
                for i, p in enumerate(parts):
                    end = parts[i + 1].start() if i + 1 < len(parts) else len(rest)
                    blk = rest[p.end() - 1:end]
                    cov = re.search(r"card-coverage:([\d.]+)", blk)
                    prior = re.search(r"(?<![a-z-])prior:([+\-][\d.]+)", blk)
                    combat = re.search(r"(?<![a-z-])combat:([+\-][\d.]+)", blk)
                    entries.append({
                        "name": p.group(2), "score": float(p.group(3)), "won": p.group(1) == ">",
                        "coverage": float(cov.group(1)) if cov else None,
                        "prior": float(prior.group(1)) if prior else None,
                        "combat": float(combat.group(1)) if combat else None,
                    })
                d["rewards"].append({"bot": m.group(1), "pick": m.group(2), "size": int(m.group(3)),
                                     "entries": entries})
            m = RE_BUILD.search(line)
            if m:
                d["builds"].append((m.group(1), m.group(2), int(m.group(3)), m.group(4).strip(), m.group(5).strip()))

    # Terminal-state classification (the triage skill's rule, applied once here).
    with open(path, encoding="utf-8", errors="replace") as fh:
        tail = fh.readlines()[-400:]
    body = "".join(tail)
    if d["outcome"] is not None:
        d["termination"] = "run-finished" if "How did the game end" in body or True else "run-finished"
    elif "NGame.Quit called" in body:
        d["termination"] = "external-quit (data truncated; do NOT judge strength from this)"
    else:
        d["termination"] = "unknown/watchdog"
    return d


# ---------------------------------------------------------------------------
# meter model

def parse_meter(path: str) -> dict | None:
    if not os.path.exists(path):
        return None
    with open(path, encoding="utf-8") as fh:
        d = json.load(fh)
    segments = list(d.get("segments") or [])
    if d.get("players"):
        segments.append(d)          # the combat still in progress is a segment too
    seats, name = [], {}
    for seg in segments:
        for k, p in (seg.get("players") or {}).items():
            if k not in name:
                seats.append(k)
                name[k] = p.get("name", k)
    totals = defaultdict(lambda: defaultdict(float))
    dbc = defaultdict(lambda: defaultdict(float))
    for seg in segments:
        for k, p in (seg.get("players") or {}).items():
            for field in ("dmg_dealt", "dmg_taken", "cards_played", "energy_spent",
                          "energy_wasted", "block_total", "hits", "overkill", "potions"):
                totals[k][field] += p.get(field) or 0
            mx = totals[k].get("max_hit", 0)
            if (p.get("max_hit") or 0) > mx:
                totals[k]["max_hit"] = p["max_hit"]
                totals[k]["max_hit_card"] = p.get("max_hit_card")
            for card, v in (p.get("dbc") or {}).items():
                dbc[k][card] += v
    return {"path": path, "segments": segments, "seats": seats, "name": name,
            "totals": totals, "dbc": dbc, "run_seed": d.get("run_seed"),
            "encounter": d.get("encounter"), "turn": d.get("turn")}


def expand_cards(text: str) -> list[str]:
    cards = []
    for tok in text.split():
        m = re.match(r"^([A-Z_]+?)(?:\+)?(?:x(\d+))?$", tok)
        if m:
            cards += [m.group(1)] * int(m.group(2) or 1)
    return cards


# ---------------------------------------------------------------------------
# sections

def sec_run(log, meter) -> None:
    head("RUN")
    out(f"log   {log['path']}")
    if meter:
        out(f"meter {meter['path']}")
    out()
    out(f"seed={log['seed']}  ascension={log['ascension']}  termination={log['termination']}")
    if log["outcome"]:
        victory, act, floor = log["outcome"]
        out(f"outcome: victory={victory}  act={act} (0-based)  floor={floor}")
    if meter:
        # Cross-check by ENCOUNTER SEQUENCE, not by seed: the log prints the seed as
        # a string and the meter stores a numeric run hash, so comparing those two is
        # apples to oranges and always "differs" on the same run.
        log_enc = [e.name for e in log["encounters"]]
        meter_enc = [s.get("encounter") for s in meter["segments"]]
        overlap = min(len(log_enc), len(meter_enc))
        matched = bool(overlap) and log_enc[:overlap] == meter_enc[:overlap]
        out(f"meter: segments={len(meter['segments'])} current={meter.get('encounter')} "
            f"turn={meter.get('turn')}  "
            f"[{'encounter sequence matches this log' if matched else '*** METER IS FROM ANOTHER RUN ***'}]")
        if not matched:
            out("   the meter only ever holds ONE run; against an archived log its DAMAGE")
            out("   section describes a different game. Readings below that need the meter")
            out("   (orchestrator: sections damage) are not about this log.")


def sec_planner(log) -> None:
    head("PLANNER HEALTH")
    if log["fallbacks"]:
        out(f"!! tournament THREW and declined {len(log['fallbacks'])} time(s).")
        out(f"!! first: {log['fallbacks'][0]}")
        kinds = Counter(re.split(r"[:@]", f)[0] for f in log["fallbacks"])
        for k, n in kinds.most_common():
            out(f"     {n:>4}  {k[:110]}")
    else:
        out("tournament fallbacks: 0  (no decision was abandoned to an exception)")
    out()
    out(f"decisions: via=kernel {log['via']['kernel']}   via=legacy {log['via']['legacy']}")
    if log["via"]["legacy"]:
        out("   !! via=legacy is the LEGACY planner; any count here means the kernel was not driving")
    out()
    out("guarded failure channels (0 everywhere = none of these entry points threw):")
    for key, why in CHANNELS.items():
        out(f"   {log['channels'][key]:>5}  {key:<28} {why}")
    out()
    joint = log["joint"]
    if joint:
        out(f"joint searches: {len(joint)}")
        empty = sum(1 for j in joint if j["paths"].startswith("0/"))
        out(f"   produced NO evaluated route (paths=0/N): {empty}  ({empty / len(joint):.0%})")
        out(f"   reference=continuation-budget (reference ate the budget): "
            f"{sum(1 for j in joint if j['reference'].startswith('continuation-budget'))}")
        refs = Counter(j["reference"] for j in joint)
        out("   reference outcomes: " + ", ".join(f"{k}:{v}" for k, v in refs.most_common()))
    ms = sorted(log["decision_ms"])
    if ms:
        def bucket(v):
            return "<0.1s" if v < 100 else "0.1-1s" if v < 1000 else "1-3s" if v < 3000 else "3-6s" if v < 6000 else ">6s"
        out()
        out("decision-ms: " + ", ".join(f"{k}:{v}" for k, v in
                                        sorted(Counter(bucket(v) for v in ms).items(),
                                               key=lambda kv: ["<0.1s", "0.1-1s", "1-3s", "3-6s", ">6s"].index(kv[0]))))
        out(f"   max={ms[-1]:.0f}ms")
    out()
    out("NOTE: interpret tournament cost telemetry against the loaded MOD version.")
    out("      Current versions capture compute after completion; older construction-time")
    out("      records can report ~0. Allocation and GC scopes may include other threads.")


def sec_combats(log) -> None:
    head("COMBATS")
    if not log["encounters"]:
        out("(no combat encountered in this log — wrong file?)")
        return
    out(f"{'#':>2} {'encounter':28} {'rnd':>4} {'enemyHP':>8} {'entry':>6} {'exit':>6} {'lost':>5} {'/seat/play':>10}")
    for i, e in enumerate(log["encounters"]):
        if not e.orders:
            out(f"{i:>2} {e.name[:28]:28} {'-':>4} {'-':>8} {'-':>6} {'-':>6} {'-':>5} {'-':>10}")
            continue
        entry = sum(e.orders[0].party.values())
        exit_ = sum(e.orders[-1].party.values())
        plays = len(e.orders)
        dmg = max(0, e.orders[0].enemy - e.orders[-1].enemy)
        per = dmg / plays if plays else 0
        flag = "  <<<" if e.rounds >= 8 else ""
        out(f"{i:>2} {e.name[:28]:28} {e.rounds:>4} {e.enemy_hp_max:>8} {entry:>6} {exit_:>6} "
            f"{max(0, entry - exit_):>5} {per:>10.1f}{flag}")
    out()
    out("enemyHP is the SUM over enemies at the widest point; `lost` is party total HP in->out")
    out("(order lines are sampled per card play, so entry/exit are close but not exact).")
    out("The single most useful column is the last one: effective damage PER CARD PLAY.")


def sec_damage(log, meter) -> None:
    head("DAMAGE (from the damage meter, not reconstructed from the log)")
    if meter is None:
        out("(DamageMeter_combat_state.json not found; pass --meter)")
        return
    seats = meter["seats"]
    out(f"{'seat':>22} {'dmg':>7} {'cards':>6} {'per card':>9} {'taken':>6} {'block':>6} "
        f"{'wasted':>7} {'max hit':>8} {'max card':>16}")
    ranked = sorted(seats, key=lambda k: meter["totals"][k]["dmg_dealt"] / max(1, meter["totals"][k]["cards_played"]))
    for k in ranked:
        t = meter["totals"][k]
        per = t["dmg_dealt"] / max(1, t["cards_played"])
        out(f"{meter['name'][k][:22]:>22} {t['dmg_dealt']:>7.0f} {t['cards_played']:>6.0f} {per:>9.2f} "
            f"{t['dmg_taken']:>6.0f} {t['block_total']:>6.0f} {t['energy_wasted']:>7.0f} "
            f"{t.get('max_hit', 0):>8.0f} {str(t.get('max_hit_card', ''))[:16]:>16}")
    low = ranked[0]
    out()
    out(f"lowest output: {meter['name'][low]} ({low})")
    out("  damage by card:")
    total = sum(meter["dbc"][low].values()) or 1
    for card, v in sorted(meter["dbc"][low].items(), key=lambda kv: -kv[1])[:8]:
        out(f"     {card:<24} {v:>7.0f}  {v / total:>4.0%}")
    out()
    out("  read this against the DECK section: a seat whose top damage card is STRIKE_*")
    out("  owns no engine, and that is a DRAFTING outcome, not a play-quality one")
    out("  (check energy_wasted before blaming the in-combat planner).")


def sec_decisions(log) -> None:
    head("OUT-OF-COMBAT DECISIONS")
    if log["routes"]:
        out("route choices (minHp = worst seat's HP% at the moment of the choice):")
        for kind, coord, minhp in log["routes"]:
            flag = "  <<< chose a fight at low HP" if (kind in ("EliteMapCoord",) and minhp < 40) else ""
            out(f"   {kind:<20} {coord:>10}  minHp={minhp:>3}%{flag}")
    camps = log["camps"]
    if camps:
        picks = Counter(c[3] for c in camps)
        out()
        out(f"camps: {len(camps)} seat-decisions  " + ", ".join(f"{k}:{v}" for k, v in picks.most_common()))
        healed = sum(c[4] for c in camps)
        out(f"   total healed {healed:.0f}; healing events at <50% HP: "
            f"{sum(1 for c in camps if c[1] / max(1, c[2]) < 0.5)}/{len(camps)}")
    if log["shops"]:
        out()
        out(f"shops: {len(log['shops'])} purchases — " + ", ".join(f"{i}" for _, i, _ in log["shops"]))
    if log["events"]:
        out()
        out(f"events: {len(log['events'])}")
        for bot, name, chose in log["events"]:
            out(f"   {bot[-6:]:>8}  {name:<22} {chose.split('.')[-1]}")
    rw = log["rewards"]
    if rw:
        out()
        skips = sum(1 for r in rw if r["pick"] == "SKIP")
        unmodeled = 0
        scored = 0
        for r in rw:
            win = next((e for e in r["entries"] if e["won"]), None)
            if win and win["coverage"] is not None:
                scored += 1
                if win["coverage"] == 0.0:
                    unmodeled += 1
        out(f"card rewards: {len(rw)} decisions, {skips} SKIP")
        if scored:
            out(f"   picked a card the sim CANNOT model: {unmodeled}/{scored} ({unmodeled / scored:.0%})")
        out("   (compare that share against how often coverage-0 cards were OFFERED; a")
        out("    big gap means the `unknown` imputation is outbidding measured value)")
        out()
        out("   damage-vs-prior pattern: the pick had LESS measured offense than an")
        out("   alternative and won on `prior` alone. Candidates, not verdicts — a card can")
        out("   also be rejected on energy/commitment, which this filter does not see:")
        shown = flagged = 0
        for r in rw:
            if r["pick"] == "SKIP" or not r["entries"]:
                continue
            win = next((e for e in r["entries"] if e["won"]), None)
            if not win or win["prior"] is None:
                continue
            alt = [e for e in r["entries"]
                   if not e["won"] and e["combat"] is not None and e["score"] > -50]
            if not alt:
                continue
            best = max(alt, key=lambda e: e["combat"])
            if best["combat"] > 4 and (win["prior"] or 0) > best["prior"] and win["combat"] < best["combat"]:
                flagged += 1
                if shown < 10:
                    out(f"     size={r['size']:>2} took {win['name']:<18} combat={win['combat']:>5.1f} "
                        f"prior={win['prior']:>5.1f}  over {best['name']:<18} combat={best['combat']:>5.1f} "
                        f"prior={best['prior']:>5.1f}")
                    shown += 1
        out(f"     {flagged} of {len(rw)} decisions match (10 shown)")
        if flagged:
            out("   Most plain damage cards sit at prior=-12.0, the clamp floor: prior is a")
            out("   single-player, all-runs Elo whose swing is larger than anything the")
            out("   offline model can produce for a plain attack, so those cards cannot win.")


def sec_decks(log) -> None:
    head("DECKS")
    final = [b for b in log["builds"] if b[0] == "final"]
    if not final:
        out("(no build[final] line — the run did not reach a final deck dump)")
        return
    grand = 0
    starters = 0
    for _, bot, size, route, text in final:
        cards = expand_cards(text)
        st = sum(1 for c in cards if c.startswith("STRIKE_"))
        df = sum(1 for c in cards if c.startswith("DEFEND_"))
        grand += len(cards)
        starters += st + df
        out(f"{bot[-6:]:>8} size={len(cards):>3} route={route:<32} "
            f"plain strikes={st:>2} defends={df:>2} ({100 * (st + df) / max(1, len(cards)):.0f}% starters)")
    out()
    out(f"TOTAL: {starters}/{grand} cards are basic Strike/Defend = {100 * starters / max(1, grand):.0f}%")
    out("A third of the deck still being starters at act 2+ is the single best predictor")
    out("of the low damage-per-card seen in the DAMAGE section.")


# ---------------------------------------------------------------------------

def newest_logs(limit: int = 5) -> list[tuple[str, float]]:
    if not os.path.isdir(DEFAULT_LOG_DIR):
        return []
    rows = []
    for fn in os.listdir(DEFAULT_LOG_DIR):
        if fn.startswith("godot") and fn.endswith(".log"):
            p = os.path.join(DEFAULT_LOG_DIR, fn)
            rows.append((p, os.path.getmtime(p)))
    rows.sort(key=lambda r: -r[1])
    return rows[:limit]


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--log", default=DEFAULT_LOG)
    ap.add_argument("--meter", default=DEFAULT_METER)
    ap.add_argument("--only", default=",".join(SECTIONS),
                    help=f"comma list from {','.join(SECTIONS)}")
    ap.add_argument("--list-logs", action="store_true", help="show candidate logs and exit")
    args = ap.parse_args()

    if args.list_logs:
        for p, mtime in newest_logs(10):
            out(f"{datetime.datetime.fromtimestamp(mtime):%Y-%m-%d %H:%M}  {os.path.getsize(p):>9}  {p}")
        return 0

    if not os.path.exists(args.log):
        out(f"log not found: {args.log}")
        out("candidates:")
        for p, mtime in newest_logs(10):
            out(f"  {datetime.datetime.fromtimestamp(mtime):%Y-%m-%d %H:%M}  {os.path.getsize(p):>9}  {p}")
        return 2

    out(f"# log mtime {datetime.datetime.fromtimestamp(os.path.getmtime(args.log)):%Y-%m-%d %H:%M}"
        f"   ({os.path.getsize(args.log)} bytes)")
    others = [(p, m) for p, m in newest_logs(10) if os.path.abspath(p) != os.path.abspath(args.log)]
    if others:
        out("# other logs present (godot.log ROLLS — check you read the right one):")
        for p, mtime in others[:4]:
            out(f"#   {datetime.datetime.fromtimestamp(mtime):%Y-%m-%d %H:%M}  {os.path.getsize(p):>9}  {os.path.basename(p)}")

    log = parse_log(args.log)
    meter = parse_meter(args.meter)
    if meter is None:
        out(f"# damage meter not found at {args.meter}")

    wanted = {s.strip() for s in args.only.split(",") if s.strip()}
    for name, fn in (("run", lambda: sec_run(log, meter)),
                     ("planner", lambda: sec_planner(log)),
                     ("combats", lambda: sec_combats(log)),
                     ("damage", lambda: sec_damage(log, meter)),
                     ("decisions", lambda: sec_decisions(log)),
                     ("decks", lambda: sec_decks(log))):
        if name in wanted:
            fn()
    out()
    return 0


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass
    raise SystemExit(main())
