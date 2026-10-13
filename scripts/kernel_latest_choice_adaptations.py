"""Retain the 1JJ0XFWWNLTC native-menu protocol adaptations.

This is an independent, last-layer adapter. The public importer decides when
to call it after its existing adaptations; it does not import another layer.
Keep requested implicit-all bounds, real empty native menu slots and explicit
capacity gates; also retain nested source metadata and cursor cardinality.
"""

import hashlib
import json
from pathlib import Path


def apply_latest_choice_adaptations(relative, text):
    records = json.loads(Path(__file__).with_name(
        "kernel-latest-choice-adaptations-41.json").read_text(encoding="utf-8"))
    record = records.get(relative)
    if record is None:
        return text
    actual = hashlib.sha256(text.encode('utf-8')).hexdigest()
    if actual != record['base_sha256']:
        raise RuntimeError(
            f"Latest choice {relative}: predecessor hash changed; "
            f"expected {record['base_sha256']}, got {actual}; review before reimport.")
    for change in record["changes"]:
        old, new = change["old"], change["new"]
        if text.count(old) != 1:
            raise RuntimeError(
                f"Latest choice {relative}: expected exactly one '{change['name']}' seam; "
                "review the native menu/source/cardinality protocol before reimport.")
        text = text.replace(old, new, 1)
    return text
