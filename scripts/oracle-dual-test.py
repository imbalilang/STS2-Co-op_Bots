#!/usr/bin/env python3
"""Does a second solver instance beside this one corrupt the readings?

The oracle has two machine-dependent channels, both found in the source earlier: the
search stops on a *wall clock* deadline, and its parallelism is derived from the CPU
count (pinned to 1 here). A second instance competes for cores, so the wall-clock
deadline could expire earlier in real terms and the solver could expand fewer nodes —
which means two concurrent instances might not give the same answer for the same deck
and seed.

The baseline already exists: the factorial v2 batch was run with a single instance, so
its recorded rows ARE the uncontended reading. This script replays a few of those
cells **while v2 keeps running**, which is the most honest possible second-instance
load — real work, not a synthetic spinner. Nothing needs stopping.

Two quantities are compared, and `expanded` is the more sensitive one:

  * `final_enemy_hp` — the reading itself. Same seed, so it should be bit-identical.
  * `expanded`       — nodes the search got through. If contention eats budget, this
                       falls **even when the reading happens to be unchanged**, which
                       would make a clean reading luck rather than safety.

Usage:
    python scripts/oracle-dual-test.py --cases 6
"""
import argparse
import importlib.util
import io
import json
import os
import subprocess
import threading
import time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
V2 = os.path.join(REPO, "work", "oracle", "factorial-v2")
OUT = os.path.join(REPO, "work", "oracle", "dual-test")

# A dedicated instance name: the launcher keeps a per-instance runtime root and lock,
# so reusing v2's name would collide with the batch that is deliberately still running.
# Two distinct lease names: the lease is per-instance, so both halves of the pair
# need their own, and both must be in parallel mode — one exclusive lease blocks
# every other instance regardless of what the newcomer asks for.
DUAL_INSTANCES = ["oracle-dual-a", "oracle-dual-b"]


def run_parallel(spec, case, scenario_id, timeout_seconds, budget_ms, instance):
    """Same as the v2 runner, but asking the launcher for parallel execution.

    The launcher defaults to HeadlessExecutionMode=exclusive, which does not run two
    instances at once — it queues the second behind the first. The first attempt at
    this test found that out the honest way: its log said UNATTENDED_QUEUED while the
    batch held the lock, and only one game process was ever on the machine. Testing
    contention requires asking for it.
    """
    import subprocess as sp
    environment = dict(os.environ)
    environment["PATH"] = os.path.dirname(v2_module.PWSH) + os.pathsep + environment.get("PATH", "")
    input_path = os.path.join(case, "input.json")
    command = [v2_module.PWSH, "-NoProfile", "-File", v2_module.WRAPPER,
               "-GeneratedScenarioPath", input_path, "-EvidenceDirectory", case,
               "-ScenarioId", scenario_id, "-HeadlessInstance", instance,
               "-HeadlessExecutionMode", "parallel",
               "-TimeoutSeconds", str(timeout_seconds), "-ExitOnComplete",
               "-Sts2GameRoot", v2_module.GAME_ROOT,
               "-RitsuWorkshopRoot", v2_module.RITSU_ROOT,
               "-CombatSolverBuildDir", v2_module.SOLVER_DIR,
               "-DeploymentFastModeForTest", "Normal",
               "-ExpectedUnexpectedReplansAtMost", "10",
               "-AcceptAnyCombatOutcome",
               "--search-budget-override-milliseconds", str(budget_ms),
               "--search-max-degree-of-parallelism-for-test", "1"]
    with io.open(os.path.join(case, "launcher.log"), "w", encoding="utf-8") as log:
        sp.run(command, cwd=os.path.join(REPO, "CombatSolver-main"),
               stdout=log, stderr=sp.STDOUT, env=environment)
    path = os.path.join(case, "result.json")
    if not os.path.exists(path):
        return {"status": "timeout", "turns": None, "survived": None, "final_enemy_hp": None,
                "hp_lost": None, "expanded": None, "boundary": None, "error": "launcher timeout"}
    with io.open(path, encoding="utf-8-sig") as handle:
        result = json.load(handle)
    outcome = ((result.get("generatedScenario") or {}).get("actualOutcome")) or {}
    return {"status": result.get("status") or "unknown",
            "turns": result.get("finishedTurn"), "survived": outcome.get("survived"),
            "final_enemy_hp": outcome.get("finalEnemyHp"), "hp_lost": outcome.get("hpLost"),
            "expanded": (result.get("solverMetrics") or {}).get("selectedExpanded"),
            "boundary": (result.get("solverMetrics") or {}).get("boundary"),
            "error": (result.get("error") or "")[:160]}


def load_v2():
    spec = importlib.util.spec_from_file_location(
        "v2", os.path.join(REPO, "scripts", "oracle-factorial-v2.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def sample_cpu(stop, samples):
    """Coarse total CPU load, sampled while the run is in flight."""
    while not stop.is_set():
        try:
            completed = subprocess.run(
                ["powershell", "-NoProfile", "-Command",
                 "(Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average"],
                capture_output=True, text=True, timeout=20)
            value = completed.stdout.strip().splitlines()[-1]
            if value.isdigit():
                samples.append(int(value))
        except Exception:
            pass
        stop.wait(8)


v2_module = None


def main():
    global v2_module
    parser = argparse.ArgumentParser()
    parser.add_argument("--cases", type=int, default=6)
    parser.add_argument("--budget-ms", type=int, default=1000)
    parser.add_argument("--timeout-seconds", type=int, default=900)
    args = parser.parse_args()

    v2 = load_v2()
    v2_module = v2
    base = v2.load_base()
    colours = v2.load_colours()
    os.makedirs(OUT, exist_ok=True)

    # Replay cells v2 has already finished, so each has an uncontended reading to
    # compare against. Spread over bosses and P/M so a Boss effect cannot be read as
    # a contention effect.
    recorded = {}
    with io.open(os.path.join(V2, "results.jsonl"), encoding="utf-8") as handle:
        for line in handle:
            row = json.loads(line)
            recorded[(row["label"], row["seed"], row["boss"])] = row
    wanted, used_bosses = [], {}
    for key in sorted(recorded, key=lambda k: (k[2], k[0], k[1])):
        if used_bosses.get(key[2], 0) >= args.cases // 3 + 1:
            continue
        used_bosses[key[2]] = used_bosses.get(key[2], 0) + 1
        wanted.append(key)
        if len(wanted) >= args.cases:
            break
    print(f"{len(wanted)} cells replayed while v2 runs; recorded baseline from v2", flush=True)

    comparison = []
    cpu_samples = []

    def one(index, key):
        label, seed, boss = key
        parts = label.replace("P", "").split("M")
        generators, multipliers = int(parts[0]), int(parts[1])
        deck = v2.build_deck(base, generators, multipliers)
        spec = v2.spec_for(deck, boss, f"{seed}-{label}-{boss}", colours)
        case = os.path.join(OUT, label, seed, boss)
        os.makedirs(case, exist_ok=True)
        # The launcher resolves this path itself, so it has to exist before it is
        # invoked; run_parallel only launches, it does not build the scenario.
        with io.open(os.path.join(case, "input.json"), "w", encoding="utf-8") as handle:
            json.dump(spec, handle, indent=2)
        began = time.time()
        outcome = run_parallel(spec, case, f"DUAL-{label}-{seed}-{boss}",
                               args.timeout_seconds, args.budget_ms,
                               DUAL_INSTANCES[index % len(DUAL_INSTANCES)])
        return outcome, time.time() - began

    stop = threading.Event()
    sampler = threading.Thread(target=sample_cpu, args=(stop, cpu_samples), daemon=True)
    sampler.start()

    # Pairs, run at the same instant. The whole question is whether two instances
    # change each other's reading, so the pair has to overlap in wall clock; running
    # them one after the other would only re-measure the uncontended case.
    for start_index in range(0, len(wanted), 2):
        pair = wanted[start_index:start_index + 2]
        results = {}
        threads = []
        for index, key in enumerate(pair):
            thread = threading.Thread(target=lambda i, k: results.__setitem__(k, one(i, k)),
                                      args=(index, key))
            threads.append(thread)
            thread.start()
        for thread in threads:
            thread.join()

        for key in pair:
            outcome, elapsed = results[key]
            label, seed, boss = key
            before = recorded[key]
            row = {"label": label, "seed": seed, "boss": boss, "wall_seconds": round(elapsed, 1),
                   "base_enemy_hp": before["final_enemy_hp"], "dual_enemy_hp": outcome["final_enemy_hp"],
                   "base_expanded": before["expanded"], "dual_expanded": outcome["expanded"],
                   "same_hp": before["final_enemy_hp"] == outcome["final_enemy_hp"],
                   "same_expanded": before["expanded"] == outcome["expanded"],
                   "base_survived": before["survived"], "dual_survived": outcome["survived"],
                   "status": outcome["status"], "paired_with": [k for k in pair if k != key][0]}
            comparison.append(row)
            with io.open(os.path.join(OUT, "comparison.jsonl"), "a", encoding="utf-8") as handle:
                handle.write(json.dumps(row, ensure_ascii=False) + "\n")
            flag = "SAME" if row["same_hp"] else "**DIFF**"
            print(f"  {label:5} {seed} {boss:18} hp {row['base_enemy_hp']:>4} -> {row['dual_enemy_hp']:>4} {flag}"
                  f" | expanded {row['base_expanded']} -> {row['dual_expanded']}"
                  f" | {row['wall_seconds']}s | paired with {row['paired_with'][0]}", flush=True)
    stop.set()

    if cpu_samples:
        print(f"\nCPU samples during the dual runs: {cpu_samples} (mean {sum(cpu_samples)/len(cpu_samples):.0f}%)")
        with io.open(os.path.join(OUT, "cpu.json"), "w", encoding="utf-8") as handle:
            json.dump(cpu_samples, handle)


if __name__ == "__main__":
    main()
