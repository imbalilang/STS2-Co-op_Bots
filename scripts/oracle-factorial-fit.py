#!/usr/bin/env python3
"""Additive or multiplicative? Fit the P x M factorial and report the interaction.

    Additive:        y = a + b*P + c*M
    Multiplicative:  y = a + b*P + c*M + d*(P x M)

`d` is the whole experiment. The leader's decision rule:

  * d significant and positive -> the interaction is real, the shape must be
    multiplicative;
  * d not significant -> on this package the additive shape is enough, which is
    also a result worth having;
  * d significant but negative -> worth investigating on its own.

And one condition on top: a shape that holds on only one boss is not a shape. The
sign of d has to agree across the three, or the honest reading is that nothing was
established.

Three seeds per cell mean the residual finally contains something other than the
model's own error, so the F test has a chance of separating an interaction from
shuffle noise. The per-seed breakdown is printed too: an effect that appears at one
seed and vanishes at the others is a shuffle, not a shape.

Usage:
    python scripts/oracle-factorial-fit.py [--metric final_enemy_hp|turns]
"""
import argparse
import io
import json
import os
import statistics
from collections import defaultdict

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RESULTS = os.path.join(REPO, "work", "oracle", "factorial-v2", "results.jsonl")
BOSSES = ["TEST_SUBJECT_BOSS", "QUEEN_BOSS", "AEONGLASS_BOSS"]
# F(1, df) at 5%. df = n - 4 for the multiplicative model.
F05 = {4: 7.71, 5: 6.61, 6: 5.99, 7: 5.59, 8: 5.32, 9: 5.12, 10: 4.96,
       11: 4.84, 12: 4.75, 20: 4.35, 30: 4.17}


def ols(X, y):
    """Least squares via Gauss-Jordan on the normal equations."""
    k, n = len(X[0]), len(X)
    A = [[sum(X[i][a] * X[i][b] for i in range(n)) for b in range(k)] +
         [sum(X[i][a] * y[i] for i in range(n))] for a in range(k)]
    for c in range(k):
        p = max(range(c, k), key=lambda r: abs(A[r][c]))
        if abs(A[p][c]) < 1e-12:
            return None, None
        A[c], A[p] = A[p], A[c]
        pv = A[c][c]
        A[c] = [v / pv for v in A[c]]
        for r in range(k):
            if r != c and A[r][c]:
                f = A[r][c]
                A[r] = [A[r][j] - f * A[c][j] for j in range(k + 1)]
    beta = [A[i][k] for i in range(k)]
    pred = [sum(X[i][a] * beta[a] for a in range(k)) for i in range(n)]
    return beta, sum((y[i] - pred[i]) ** 2 for i in range(n))


def fit(rows, metric):
    """rows: (P, M, value). Returns beta and the sums of squares both models need."""
    y = [r[2] for r in rows]
    bA, ssA = ols([[1.0, r[0], r[1]] for r in rows], y)
    bB, ssB = ols([[1.0, r[0], r[1], r[0] * r[1]] for r in rows], y)
    return bA, ssA, bB, ssB


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--metric", default="final_enemy_hp",
                        choices=["final_enemy_hp", "turns", "expanded"])
    args = parser.parse_args()
    if not os.path.exists(RESULTS):
        raise SystemExit(f"no results at {RESULTS}")
    rows = [json.loads(line) for line in io.open(RESULTS, encoding="utf-8") if line.strip()]

    print(f"metric = {args.metric} | {len(rows)} runs\n")
    print(f"{'boss':20} {'y 范围':>14} {'加法 R2':>8} {'乘法 R2':>8} | "
          f"{'d(P*M)':>8} {'F':>7} {'临界':>6} | 残差 sd")
    print("-" * 96)
    verdicts = []
    for boss in BOSSES:
        subset = [r for r in rows if r["boss"] == boss and r.get(args.metric) is not None]
        if len(subset) < 8:
            print(f"{boss:20} 数据不足 ({len(subset)} 行)")
            continue
        data = [(r["P"], r["M"], float(r[args.metric])) for r in subset]
        y = [d[2] for d in data]
        bA, ssA, bB, ssB = fit(data, args.metric)
        n = len(data)
        n = len(data)
        sst = sum((v - statistics.mean(y)) ** 2 for v in y)
        rA, rB = 1 - ssA / sst, 1 - ssB / sst
        df = n - 4
        F = ((ssA - ssB) / 1) / (ssB / df) if ssB > 0 else float("inf")
        crit = F05.get(df, 4.0)
        sd = (ssB / df) ** 0.5
        print(f"{boss:20} {min(y):>6.0f}~{max(y):<6.0f} {rA:>8.3f} {rB:>8.3f} | "
              f"{bB[3]:>+8.3f} {F:>7.2f} {crit:>6.2f} | {sd:.1f}")
        verdicts.append((boss, bB[3], F, crit))

    print()
    if verdicts:
        signs = {1 if d > 0 else -1 for _, d, _, _ in verdicts}
        significant = [boss for boss, _, F, crit in verdicts if F > crit]
        print("判定:")
        if not significant:
            print("  交互项在任何一个 Boss 上都不显著 -> **不构成形状结论**")
            print("  按判据:在这条包件上,加法形状够用 —— 但见下面的三种子稳定性")
        elif len(signs) == 1:
            print(f"  交互项显著且符号一致({list(signs)[0]:+d}) -> 支持"
                  f"{'乘法' if list(signs)[0] > 0 else '加法(负交互)'}形状")
        else:
            print(f"  交互项显著但**符号在 Boss 间不一致** {sorted(signs)} -> 不构成形状结论")

    print("\n逐种子拟合(同一形式是否在 3 个种子间稳定):")
    seeds = sorted({r["seed"] for r in rows})
    for boss in BOSSES:
        line = []
        for seed in seeds:
            subset = [r for r in rows if r["boss"] == boss and r["seed"] == seed
                      and r.get(args.metric) is not None]
            if len(subset) < 8:
                line.append(f"{seed[-2:]}: n/a")
                continue
            data = [(r["P"], r["M"], float(r[args.metric])) for r in subset]
            _, _, bB, _ = fit(data, args.metric)
            line.append(f"{seed[-2:]}: d={bB[3]:+.2f}")
        print(f"  {boss:20} " + "  ".join(line))
    print("\n  同一个 d 若在不同种子上符号就翻,那它测的是抽牌运气,不是形状。")

    print("\n主效应(供参考):")
    print(f"  {'boss':20} {'b(P)':>8} {'c(M)':>8}")
    for boss in BOSSES:
        subset = [r for r in rows if r["boss"] == boss and r.get(args.metric) is not None]
        if len(subset) < 8:
            continue
        data = [(r["P"], r["M"], float(r[args.metric])) for r in subset]
        _, _, bB, _ = fit(data, args.metric)
        print(f"  {boss:20} {bB[1]:>+8.2f} {bB[2]:>+8.2f}")


if __name__ == "__main__":
    main()
