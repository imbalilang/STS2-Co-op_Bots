#!/usr/bin/env python3
"""How much of a run is actually search? And is the search CPU-sensitive at all?

Two questions, and the second one decides whether the dual-instance result means
what it looked like it meant.

(a) Duty cycle. A run takes ~150 s of wall clock. If the game process only burns
    ~20 s of CPU in that time, then most of a run is NOT search, the wall-clock
    search budget is a small slice of the cost, and every efficiency estimate built
    on "lower the budget and the run gets faster" is wrong by a large factor.

(d) The decisive control. The earlier dual-instance test showed `expanded` falling
    13-40% with a second instance present, and that was attributed to CPU
    contention — but the machine was measured at ~5% CPU, so the attribution cannot
    be right. This separates the variable cleanly: same deck, same seed, alone vs
    with a synthetic CPU load that has nothing to do with the game.

      expanded falls with the load  -> the search IS CPU-sensitive, and the
                                       attribution survives in a corrected form;
      expanded unchanged            -> the dual-instance effect comes from
                                       somewhere else (a shared lock, disk, engine
                                       pacing, thread affinity).

Usage:
    python scripts/oracle-duty.py --cases 5 --load-cores 2
"""
import argparse
import importlib.util
import io
import json
import os
import statistics
import subprocess
import sys
import threading
import time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(REPO, "work", "oracle", "duty")
BOSSES = ["TEST_SUBJECT_BOSS", "QUEEN_BOSS", "AEONGLASS_BOSS"]


def load_v2():
    spec = importlib.util.spec_from_file_location(
        "v2", os.path.join(REPO, "scripts", "oracle-factorial-v2.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


PWSH = os.path.join(REPO, "work", "pwsh", "7", "pwsh.exe")


def game_cpu_seconds():
    """Total CPU seconds the game process has consumed so far, or None."""
    try:
        completed = subprocess.run(
            [PWSH, "-NoProfile", "-Command",
             "$p = Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue; "
             "if ($p) { ($p | Measure-Object -Property TotalProcessorTime -Sum).Sum.TotalSeconds } else { 0 }"],
            capture_output=True, text=True, timeout=30)
        text = completed.stdout.strip().splitlines()[-1].strip()
        return float(text)
    except Exception:
        return None


def burn(cores, stop):
    """Synthetic load that has nothing to do with the game: pure CPU spinning."""
    code = "x=0\nwhile True: x+=1\n"
    processes = []
    for _ in range(cores):
        processes.append(subprocess.Popen([sys.executable, "-c", code],
                                          stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL))
    stop.wait()
    for process in processes:
        process.terminate()


def one(v2, base, colours, boss, seed, tag):
    deck = v2.build_deck(base, 0, 0)
    spec = v2.spec_for(deck, boss, seed, colours)
    case = os.path.join(OUT, tag, boss)
    os.makedirs(case, exist_ok=True)
    with io.open(os.path.join(case, "input.json"), "w", encoding="utf-8") as handle:
        json.dump(spec, handle, indent=2)
    # The game only exists during the run: with ExitOnComplete the launcher takes it
    # down the moment the scenario ends, so sampling before and after measures
    # nothing but zero. Poll while the run is in flight and keep the high-water mark.
    cpu_samples = []
    outcome_box = {}

    def runner():
        outcome_box["value"] = v2.run_case(spec, case, tag, 900, 1000)

    began = time.time()
    worker = threading.Thread(target=runner)
    worker.start()
    while worker.is_alive():
        value = game_cpu_seconds()
        if value:
            cpu_samples.append(value)
        time.sleep(4)
    worker.join()
    wall = time.time() - began
    outcome = outcome_box["value"]
    cpu = max(cpu_samples) if cpu_samples else None
    row = {"tag": tag, "boss": boss, "seed": seed, "wall_seconds": round(wall, 1),
           "cpu_seconds": None if cpu is None else round(cpu, 2),
           "duty": None if not cpu else round(cpu / wall, 3),
           "expanded": outcome["expanded"], "final_enemy_hp": outcome["final_enemy_hp"],
           "turns": outcome["turns"], "status": outcome["status"]}
    with io.open(os.path.join(OUT, "duty.jsonl"), "a", encoding="utf-8") as handle:
        handle.write(json.dumps(row, ensure_ascii=False) + "\n")
    return row


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--cases", type=int, default=5)
    parser.add_argument("--load-cores", type=int, default=2)
    args = parser.parse_args()
    v2 = load_v2()
    base = v2.load_base()
    colours = v2.load_colours()
    os.makedirs(OUT, exist_ok=True)

    print("=== (a) 占空比:单实例,无额外负载 ===", flush=True)
    print(f"{'boss':18} {'seed':10} {'wall':>7} {'cpu':>7} {'duty':>6} {'expanded':>9} {'turns':>6}")
    for index in range(args.cases):
        boss = BOSSES[index % len(BOSSES)]
        row = one(v2, base, colours, boss, f"DUTY-{index}", f"alone-{index}")
        print(f"{row['boss']:18} {row['seed']:10} {row['wall_seconds']:>7.1f} "
              f"{str(row['cpu_seconds']):>7} {str(row['duty']):>6} {str(row['expanded']):>9} "
              f"{str(row['turns']):>6}", flush=True)

    rows = [json.loads(line) for line in io.open(os.path.join(OUT, "duty.jsonl"), encoding="utf-8")]
    duties = [r["duty"] for r in rows if r["duty"]]
    if duties:
        print(f"\n占空比: 中位 {statistics.median(duties):.2f}  (n={len(duties)})")
        print(f"  即 {statistics.median(duties):.0%} 的墙钟在耗 CPU,其余是等待。")

    print(f"\n=== (d) 决定性对照:同样的运行 + {args.load_cores} 核合成负载 ===", flush=True)
    stop = threading.Event()
    burner = threading.Thread(target=burn, args=(args.load_cores, stop), daemon=True)
    burner.start()
    time.sleep(3)
    print(f"{'boss':18} {'seed':10} {'wall':>7} {'cpu':>7} {'expanded':>9} {'turns':>6}")
    for index in range(args.cases):
        boss = BOSSES[index % len(BOSSES)]
        row = one(v2, base, colours, boss, f"DUTY-{index}", f"loaded-{index}")
        print(f"{row['boss']:18} {row['seed']:10} {row['wall_seconds']:>7.1f} "
              f"{str(row['cpu_seconds']):>7} {str(row['expanded']):>9} {str(row['turns']):>6}", flush=True)
    stop.set()
    time.sleep(2)

    all_rows = [json.loads(line) for line in io.open(os.path.join(OUT, "duty.jsonl"), encoding="utf-8")]
    print("\n=== 对照 ===")
    print(f"{'boss':18} {'seed':10} {'expanded 空载':>14} {'expanded 负载':>14} {'变化':>9}")
    for index in range(args.cases):
        boss = BOSSES[index % len(BOSSES)]
        seed = f"DUTY-{index}"
        alone = next((r for r in all_rows if r["tag"] == f"alone-{index}"), None)
        loaded = next((r for r in all_rows if r["tag"] == f"loaded-{index}"), None)
        if not alone or not loaded or alone["expanded"] is None or loaded["expanded"] is None:
            continue
        delta = loaded["expanded"] - alone["expanded"]
        pct = delta / alone["expanded"] if alone["expanded"] else 0
        print(f"{boss:18} {seed:10} {alone['expanded']:>14} {loaded['expanded']:>14} "
              f"{delta:>+6} ({pct:+.0%})")
    print("\n判读: 负载下 expanded 明显下降 ⇒ 搜索对 CPU 敏感;")
    print("      两者相当 ⇒ 双实例那个效应另有原因(共享锁/磁盘/引擎节奏)。")


if __name__ == "__main__":
    main()
