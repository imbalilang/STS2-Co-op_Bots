"""Audit actual shop receipts separately from event removals in a godot.log."""
import argparse
import collections
import hashlib
import json
import pathlib
import re

parser = argparse.ArgumentParser()
parser.add_argument("log", type=pathlib.Path)
parser.add_argument("--output", type=pathlib.Path)
args = parser.parse_args()
data = args.log.read_bytes()
seats = collections.defaultdict(lambda: dict(visits=[], receipts=[], shop_removals=0, other_removal_prompts=[]))
outcomes = []
for number, line in enumerate(data.decode("utf-8-sig", errors="replace").splitlines(), 1):
    if m := re.search(r"CoopBots shop: entered for seat (\d+).*gold=(\d+).*stock=\[(.*)\]", line):
        seats[m[1]]["visits"].append(dict(line=number, gold=int(m[2]), stock=m[3]))
    if m := re.search(r"CoopBots shop #\d+: bot=(\d+) item=(\S+) cost=(\d+) gold=(\d+)", line):
        seats[m[1]]["receipts"].append(dict(line=number, item=m[2], cost=int(m[3]), gold_left=int(m[4])))
        if m[2].lower() == "remove":
            seats[m[1]]["shop_removals"] += 1
    if m := re.search(r"edit purpose=FromDeckForRemoval bot=(\d+) chose=\[([^]]*)\]", line):
        # This prompt alone does not prove a shop purchase. Count receipts above.
        seats[m[1]]["other_removal_prompts"].append(dict(line=number, cards=m[2]))
    if "run outcome" in line:
        outcomes.append(dict(line=number, text=line))
report = dict(log=str(args.log.resolve()), sha256=hashlib.sha256(data).hexdigest(),
              seats=dict(seats), outcomes=outcomes)
text = json.dumps(report, ensure_ascii=False, indent=2)
if args.output:
    args.output.write_text(text, encoding="utf-8")
for seat, row in seats.items():
    print(f"{seat}: visits={len(row['visits'])} purchases={len(row['receipts'])} shop_removals={row['shop_removals']}")
for row in outcomes:
    print(row)
