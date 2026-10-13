"""A5 批次汇总报告。用法: python scripts/a5-report.py <批次目录>

读 a5-batch.py 产出的 godot-a5-r*.log，回答三个问题：
  1. 这一批通关率多少、每局死在哪一层/哪个房间；
  2. 队伍的血是在哪一段掉光的（用 `where … hp=[…]` 这条每 5 秒一行的仪器，
     按房间切段 —— 这是唯一能回答"哪个房间在放血"的读数）；
  3. 失败模式计数（剧本为什么被丢、边界卡在哪、有没有异常回退）。

刻意只做可复核的计数，不做因果猜测。
"""
import collections
import json
import os
import re
import sys

BATCH = sys.argv[1] if len(sys.argv) > 1 else "."


def read(path):
    with open(path, encoding="utf-8", errors="replace") as handle:
        return handle.read()


def hp_at(text):
    """[(行号, floor, room, [hp...], [max...], [deck...])] —— 来自 where 行。"""
    out = []
    for i, line in enumerate(text.splitlines()):
        m = re.search(r"where scene=\S+ state=(\S+) room=(\S+) roomStack=\S+ floor=(\S+) botTicks=\d+.*?"
                      r"hp=\[([^\]]*)\] deck=\[([^\]]*)\]", line)
        if m:
            out.append((i, m.group(3), m.group(2), m.group(4).split(","), m.group(1), m.group(5).split(",")))
    return out


def fights(text):
    """[(行号, encounter)] —— 战斗房开始处。"""
    return [(i, m.group(1).rstrip("."))
            for i, line in enumerate(text.splitlines())
            if (m := re.search(r"Creating NCombatRoom with mode=ActiveCombat encounter=(\S+)", line))]


def damage_edges(pts, fight_starts):
    """把 where 采样按战斗房切段，给出每个房间里 hp 的第一次/最后一次读数。"""
    rows = []
    for index, (line, encounter) in enumerate(fight_starts):
        end = fight_starts[index + 1][0] if index + 1 < len(fight_starts) else len(pts)
        inside = [p for p in pts if line <= p[0] < end]
        if not inside:
            rows.append((encounter, None, None, 0))
            continue
        rows.append((encounter, inside[0][3], inside[-1][3], len(inside)))
    return rows


def main():
    logs = sorted(f for f in os.listdir(BATCH) if re.match(r"godot-a5-r\d+\.log$", f))
    if not logs:
        print(f"{BATCH}: 没有 godot-a5-r*.log")
        return
    summary_path = os.path.join(BATCH, "summary.json")
    summary = json.load(open(summary_path, encoding="utf-8")) if os.path.exists(summary_path) else None
    if summary:
        print(f"批次 {summary['label']}  包={summary['package']}  A{summary['ascension']}  perf={summary['perf']}")
        print(f"通关率: {summary['wins']}/{summary['games']} = {100 * summary['clear_rate']:.0f}%")

    mode_counts = collections.Counter()
    for log in logs:
        text = read(os.path.join(BATCH, log))
        run_seed = None
        for m in re.finditer(r"multiplayer run started — seed=(\S+)", text):
            run_seed = m.group(1)
        victory = None
        for m in re.finditer(r"run outcome — victory=(\w+) act=(\S+) floor=(\S+)", text):
            victory, act, floor = m.group(1) == "true", m.group(2), m.group(3)
        if victory is None:
            victory, act, floor = None, "-", "-"
        print(f"\n===== {log}  run_seed={run_seed}  "
              f"{'WIN' if victory else 'LOSS' if victory is False else '??'}  act={act} floor={floor} =====")

        pts = hp_at(text)
        encounters = fights(text)
        print(f"  采样点={len(pts)}  战斗房={len(encounters)}")
        for encounter, first, last, n in damage_edges(pts, encounters):
            print(f"    {encounter:<34} {first} → {last}   ({n} 采样)")

        for reason, n in collections.Counter(
                re.findall(r"tournament script dropped: (.*)", text)).most_common(6):
            print(f"    drop ×{n}: {reason}")
            mode_counts[f"drop: {reason}"] += n
        for reason, n in collections.Counter(re.findall(r"reason=(\S+)", text)).most_common(6):
            print(f"    refuse ×{n}: {reason}")
            mode_counts[f"refuse: {reason}"] += n
        unmodeled = collections.Counter(re.findall(r"enemy-move-unmodeled:(\S+)", text))
        for key, n in unmodeled.most_common(5):
            print(f"    boundary ×{n}: {key}")
            mode_counts[f"boundary: {key}"] += n

    print("\n===== 全批失败模式计数 =====")
    for key, n in mode_counts.most_common(20):
        print(f"  ×{n}  {key}")


main()
