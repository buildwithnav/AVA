#!/usr/bin/env python
"""
Bulk-load the study 1066 CRF datasets into the AVA "analyze-study" domain.

For every ``*.xlsx`` in the data directory this script:
  * derives a stable KB id  (``as-<formcode>``) and a friendly name,
  * reads the matching ``json/<stem>.json`` sidecar for the dataset
    description and per-column business definitions,
  * POSTs the spreadsheet + definitions to the ava-api endpoint
    ``POST /api/kb/{id}/upload-file`` which parses it via ava-kb and stores
    the schema + column definitions in Postgres.

The upload endpoint is "load-and-warn": columns without a definition never
fail the load, they are just reported. Re-running is safe (idempotent upsert).

Usage:
    python load_analyze_study_kbs.py                 # load all 68
    python load_analyze_study_kbs.py --only 1066_AE  # load matching stems only
    python load_analyze_study_kbs.py --dry-run       # show plan, upload nothing
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import re
import sys
import time

try:
    import requests
except ImportError:  # pragma: no cover
    sys.exit("This script needs the 'requests' package:  python -m pip install requests")

# ── Defaults ─────────────────────────────────────────────────────────────────
DEFAULT_API_BASE = "http://localhost:5010"
DEFAULT_DOMAIN = "analyze-study"
DEFAULT_XLSX_DIR = r"c:\pk\ava_rdl"
DEFAULT_JSON_DIR = r"c:\pk\ava_rdl\json"
XLSX_MIME = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"

# Friendly names for datasets whose filename has no "(label)" component.
NAME_OVERRIDES = {
    "1066_AE_FIN_ADJ": "AE Financial Adjudication",
}


def slugify(value: str) -> str:
    """Lower-case, collapse any run of non-alphanumerics to a single '-'."""
    return re.sub(r"[^a-z0-9]+", "-", value.lower()).strip("-")


def parse_stem(stem: str) -> tuple[str, str, str]:
    """Return (kb_id, friendly_name, form_code) for a file stem like
    ``1066_AE(AE - Main)`` -> ('as-1066-ae', 'AE - Main', '1066_AE')."""
    m = re.match(r"^(?P<code>[^(]+?)\s*\((?P<label>.*)\)\s*$", stem)
    if m:
        form_code = m.group("code").strip()
        name = m.group("label").strip()
    else:
        form_code = stem.strip()
        name = NAME_OVERRIDES.get(form_code, form_code)
    return f"as-{slugify(form_code)}", name, form_code


def _stored_header(spec: dict, fallback: str) -> str:
    """The column name ava-kb stores is the spreadsheet *header*, which is the
    human label — i.e. the leading phrase of the sidecar 'description' (text
    before the first '. '), whitespace-stripped. Fall back to the field's short
    code when no description is present."""
    lead = (spec.get("description") or "").split(". ", 1)[0].strip()
    return lead or fallback


def load_sidecar(json_path: str) -> tuple[str | None, dict]:
    """Read the JSON sidecar -> (description, columnDefinitions) keyed by the
    stored column name so the definitions actually join in kb_schema.

    The sidecar 'fields' are keyed by CDISC short codes (AESTDTC), but the
    spreadsheet headers — and therefore the column names ava-kb persists — are
    the human labels (e.g. "Date of Adverse Event (AE) onset"). ava-kb strips
    header whitespace and de-duplicates repeats as 'name_1', 'name_2', ...
    (app/services/file_handler.py::_make_headers). We mirror that here so keys
    line up exactly; the short code is preserved as the definition 'label'.
    This resolves ~99.7% of columns across the 68 datasets (the remainder are
    genuinely duplicated headers that cannot be disambiguated). Returns
    (None, {}) gracefully if the sidecar is missing."""
    if not os.path.isfile(json_path):
        return None, {}
    with open(json_path, "r", encoding="utf-8") as fh:
        doc = json.load(fh)
    description = doc.get("description")
    col_defs: dict[str, dict] = {}
    seen: dict[str, int] = {}
    for code, spec in (doc.get("fields") or {}).items():
        header = _stored_header(spec, code)
        if header in seen:                       # mirror ava-kb dup suffixing
            seen[header] += 1
            header = f"{header}_{seen[header]}"
        else:
            seen[header] = 0
        col_defs[header] = {
            "label": spec.get("label"),          # CDISC/CRF short code
            "description": spec.get("description"),
            "dataType": spec.get("data_type"),   # note: snake_case -> camelCase
        }
    return description, col_defs


def upload_one(session: requests.Session, api_base: str, domain: str, kb_id: str,
               name: str, description: str | None, col_defs: dict,
               xlsx_path: str, timeout: float) -> dict:
    url = f"{api_base.rstrip('/')}/api/kb/{kb_id}/upload-file"
    data = {
        "domainId": domain,
        "name": name,
        "columnDefinitions": json.dumps(col_defs),
    }
    if description:
        data["description"] = description
    with open(xlsx_path, "rb") as fh:
        files = {"file": (os.path.basename(xlsx_path), fh, XLSX_MIME)}
        resp = session.post(url, data=data, files=files, timeout=timeout)
    resp.raise_for_status()
    return resp.json()


def main() -> int:
    ap = argparse.ArgumentParser(description="Bulk-load study 1066 CRF datasets into AVA.")
    ap.add_argument("--api-base", default=DEFAULT_API_BASE)
    ap.add_argument("--domain", default=DEFAULT_DOMAIN)
    ap.add_argument("--xlsx-dir", default=DEFAULT_XLSX_DIR)
    ap.add_argument("--json-dir", default=DEFAULT_JSON_DIR)
    ap.add_argument("--only", default=None,
                    help="Only upload files whose stem contains this substring.")
    ap.add_argument("--timeout", type=float, default=600.0, help="Per-file upload timeout (s).")
    ap.add_argument("--dry-run", action="store_true", help="Show the plan; upload nothing.")
    args = ap.parse_args()

    xlsx_files = sorted(glob.glob(os.path.join(args.xlsx_dir, "*.xlsx")))
    if args.only:
        xlsx_files = [f for f in xlsx_files if args.only.lower() in os.path.basename(f).lower()]
    if not xlsx_files:
        print(f"No .xlsx files found in {args.xlsx_dir}"
              + (f" matching '{args.only}'" if args.only else ""))
        return 1

    # Build the plan and check id uniqueness up front.
    plan = []
    seen: dict[str, str] = {}
    for xlsx in xlsx_files:
        stem = os.path.splitext(os.path.basename(xlsx))[0]
        kb_id, name, form_code = parse_stem(stem)
        if kb_id in seen:
            print(f"!! ID COLLISION: '{kb_id}' from both '{seen[kb_id]}' and '{stem}'")
        seen[kb_id] = stem
        json_path = os.path.join(args.json_dir, stem + ".json")
        plan.append((xlsx, stem, kb_id, name, json_path))

    print(f"Target : {args.api_base}  domain='{args.domain}'")
    print(f"Datasets: {len(plan)}\n")

    if args.dry_run:
        for _, stem, kb_id, name, json_path in plan:
            has_json = "json" if os.path.isfile(json_path) else "NO-JSON"
            print(f"  {kb_id:<22} <- {stem:<45} name='{name}'  [{has_json}]")
        return 0

    session = requests.Session()
    ok, failures, total_undefined = 0, [], 0
    t0 = time.time()

    for i, (xlsx, stem, kb_id, name, json_path) in enumerate(plan, 1):
        description, col_defs = load_sidecar(json_path)
        prefix = f"[{i:>2}/{len(plan)}] {kb_id:<22}"
        try:
            result = upload_one(session, args.api_base, args.domain, kb_id, name,
                                description, col_defs, xlsx, args.timeout)
            undef = result.get("undefinedColumns") or []
            total_undefined += len(undef)
            created = "created" if result.get("created") else "updated"
            msg = (f"{prefix} OK  rows={result.get('rowCount'):<6} "
                   f"cols={result.get('columnCount'):<4} "
                   f"defined={result.get('definedColumns'):<4} {created}")
            if undef:
                msg += f"  WARN {len(undef)} undefined: {', '.join(undef[:6])}"
                if len(undef) > 6:
                    msg += f" (+{len(undef) - 6})"
            print(msg)
            ok += 1
        except requests.HTTPError as exc:
            body = exc.response.text[:300] if exc.response is not None else ""
            print(f"{prefix} FAIL  HTTP {exc.response.status_code if exc.response else '?'}: {body}")
            failures.append((kb_id, stem, body))
        except Exception as exc:  # noqa: BLE001 - report and keep going
            print(f"{prefix} FAIL  {type(exc).__name__}: {exc}")
            failures.append((kb_id, stem, str(exc)))

    dt = time.time() - t0
    print("\n" + "=" * 70)
    print(f"Loaded {ok}/{len(plan)} datasets in {dt:.1f}s. "
          f"Columns without definitions: {total_undefined}.")
    if failures:
        print(f"\n{len(failures)} FAILURE(S):")
        for kb_id, stem, err in failures:
            print(f"  - {kb_id} ({stem}): {err[:160]}")
    return 0 if not failures else 2


if __name__ == "__main__":
    sys.exit(main())
