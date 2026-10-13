#!/usr/bin/env python3
"""把 rerun-old-ruler.py 的 comparison.jsonl 排成一份并排对照报告。

关键判据不是"数字是否相等"(两边预算口径不同,不可能相等),而是
**两把尺子对牌组的【排序】是否一致** —— 因为 oracle 的用途是给牌组排序。
"""

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "work" / "oracle" / "old-ruler-rerun" / "comparison.jsonl"
OUT = ROOT / "work" / "oracle" / "old-ruler-rerun" / "comparison.md"


def ranks(values):
    """平均秩(处理并列)。"""
    order = sorted(range(len(values)), key=lambda i: values[i])
    out = [0.0] * len(values)
    i = 0
    while i < len(order):
        j = i
        while j + 1 < len(order) and values[order[j + 1]] == values[order[i]]:
            j += 1
        average = (i + j) / 2.0 + 1.0
        for k in range(i, j + 1):
            out[order[k]] = average
        i = j + 1
    return out


def spearman(xs, ys):
    if len(xs) < 3:
        return None
    rx, ry = ranks(xs), ranks(ys)
    n = len(xs)
    mx, my = sum(rx) / n, sum(ry) / n
    cov = sum((a - mx) * (b - my) for a, b in zip(rx, ry))
    vx = sum((a - mx) ** 2 for a in rx) ** 0.5
    vy = sum((b - my) ** 2 for b in ry) ** 0.5
    return cov / (vx * vy) if vx and vy else None


def main() -> int:
    rows = [json.loads(line) for line in SRC.read_text(encoding="utf-8").splitlines() if line.strip()]
    usable = [r for r in rows if r["new"].get("final_enemy_hp_sum") is not None]
    failed = [r for r in rows if r["new"].get("final_enemy_hp_sum") is None]

    lines = ["# 旧尺 vs 离线路径 —— 并排对照", "",
             f"场景数 **{len(rows)}**,离线跑通 **{len(usable)}**,失败 **{len(failed)}**。", ""]
    if failed:
        lines += ["## 离线跑不通的", ""]
        for r in failed:
            lines.append(f"- `{r['scenario']}`: exit={r['new'].get('exit')} "
                         f"error={str(r['new'].get('error'))[:160]}")
        lines.append("")

    lines += ["## 1. 逐场对照(敌血越低 = 牌组打得越深)", "",
              "| 场景 | 旧尺 敌血 | 新尺 敌血 | 差 | 旧 回合 | 新 回合 | 未计划选择 | 新 搜索/执行(s) |",
              "|---|---:|---:|---:|---:|---:|---:|---|"]
    for r in sorted(usable, key=lambda x: x["scenario"]):
        old, new = r["old"], r["new"]
        a = old.get("actual_final_enemy_hp")
        b = new.get("final_enemy_hp_sum")
        turn_new = (new.get("final_enemies") and
                    (r["new"].get("final_enemies") or [""])[0] or "")
        lines.append(
            f"| `{r['scenario']}` | {a} | {b} | {'' if a is None else b - a:+d} | "
            f"{old.get('finished_turn')} | {new.get('combat_in_progress')} | "
            f"{len(new.get('unplanned_choices') or [])} | {new.get('search_s')}/{new.get('exec_s')} |")
    lines.append("")

    if usable:
        old_v = [r["old"].get("actual_final_enemy_hp") or 0 for r in usable]
        new_v = [r["new"].get("final_enemy_hp_sum") for r in usable]
        rho = spearman(old_v, new_v)
        lines += ["## 2. ⭐ 两把尺子对【牌组排序】是否一致(这才是能不能替尺的判据)", ""]
        lines.append(f"- **全部 {len(usable)} 场:Spearman ρ = "
                     f"{'n/a' if rho is None else f'{rho:+.3f}'}**")
        by_boss = {}
        for r in usable:
            boss = r["scenario"].split("-", 1)[1]
            by_boss.setdefault(boss, []).append(r)
        for boss, subset in sorted(by_boss.items()):
            if len(subset) < 3:
                continue
            sub_old = [r["old"].get("actual_final_enemy_hp") or 0 for r in subset]
            sub_new = [r["new"].get("final_enemy_hp_sum") for r in subset]
            sub_rho = spearman(sub_old, sub_new)
            lines.append(f"- {boss}(n={len(subset)}):ρ = "
                         f"{'n/a' if sub_rho is None else f'{sub_rho:+.3f}'}")
        lines.append("")

        lines += ["## 3. 求解器预测 vs 真打的仗(两边各自内部是否自洽)", ""]
        for label, key_actual, key_pred in (
                ("旧尺", "actual_final_enemy_hp", "pred_final_enemy_hp"),
                ("新尺", "final_enemy_hp_sum", "pred_final_enemy_hp")):
            if label == "旧尺":
                a = [r["old"].get("actual_final_enemy_hp") or 0 for r in usable]
                p = [r["old"].get("pred_final_enemy_hp") or 0 for r in usable]
            else:
                a = [r["new"].get("final_enemy_hp_sum") for r in usable]
                p = [r["new"].get("pred_final_enemy_hp") or 0 for r in usable]
            rho2 = spearman(a, p)
            lines.append(f"- {label}:预测 vs 实际 ρ = "
                         f"{'n/a' if rho2 is None else f'{rho2:+.3f}'}")
        lines.append("")

        spent = sum((r["new"].get("wall_s") or 0) for r in usable)
        lines += ["## 4. 时间", "",
                  f"- 离线合计 {spent:.0f}s / {len(usable)} 场 = **{spent / len(usable):.1f}s/场**",
                  f"- 旧尺合计 "
                  f"{sum((r['old'].get('wall_ms') or 0) for r in usable) / 1000.0:.0f}s"
                  f" = **{sum((r['old'].get('wall_ms') or 0) for r in usable) / 1000.0 / len(usable):.1f}s/场**",
                  ""]

    OUT.write_text("\n".join(lines), encoding="utf-8")
    print("\n".join(lines[:60]))
    print(f"\n写出 {OUT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
