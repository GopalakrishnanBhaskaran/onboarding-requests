#!/usr/bin/env python3
"""Tests for tools/import_workbook.py on a SAMPLE workbook in the layout of the real ones (no real data).

  python3 tools/test_import_workbook.py            # run the checks
  python3 tools/test_import_workbook.py --fixture  # also rewrite tests/fixtures/import-sample.json (read by the .NET tests)
"""
from __future__ import annotations

import json
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import import_workbook as iw  # noqa: E402

RED, AMBER = "FFFCE4E4", "FFFFF2CC"
HEADER = ["Tenant", "Environment", "Group Name", "Object ID (if available)", "Role / Access Type", "Intended Job Role(s)", "Scope", "Purpose", "Source File",
          "Notes / Ambiguities"]


def workbook(folder: Path) -> tuple[Path, Path]:
    from openpyxl import Workbook
    from openpyxl.styles import PatternFill
    c = Workbook()
    c.remove(c.active)
    tabs = {
        "PEST - Dev": [
            ("azr-pes-rg-data-dev-ph-weu-support", "11111111-1111-4111-8111-111111111111", "CONTRIBUTOR - whole platform\n\nAzure roles held:",
             "Domain: Central hub / platform - THIS IS THE dai DOMAIN (ph)\nResources this group holds a role on:\n  • Storage Blob Data Contributor → samplestddevdaiweu01\n  • Reader → pes-rg-data-dev-dai-weu-001 / pes-rg-data-dev-acq-weu-001",
             "Application / Production Support", "• BROAD ACCESS: whole platform", AMBER),
            ("azr-pes-rg-data-dev-fin-weu-powerbidevelopers", "22222222-2222-4222-8222-222222222222", "REPORTING - no data-plane RBAC",
             "Domain: Finance (fin)\nResources: NONE DOCUMENTED.", "Power BI Developer", "• Members: 2", None),
            ("azr-pes-rg-data-dev-sup-weu-powerbideveloper", "not captured", "TYPO", "Domain: Supply  [PEST spelling of supp] (sup)", "", "• EMPTY GROUP", RED),
        ],
        "PEST - Prod": [],
        "Vestacy - Dev": [
            ("azr-ves-rg-data-dev-ph-weu-dataengineers", "33333333-3333-4333-8333-333333333333", "CONTRIBUTOR", "Domain: Central hub (ph)", "Data Engineer", "", None),
        ],
    }
    for title, rows in tabs.items():
        ws = c.create_sheet(title)
        ws.append([f"{title} (SAMPLE)"])
        ws.append(["SAMPLE layout of the classification workbook."])
        ws.append(HEADER)
        for name, oid, role, scope, jobs, notes, fill in rows:
            ws.append([title.split(" - ")[0], title.split(" - ")[1], name, oid, role, jobs, scope, "", "SAMPLE.csv", notes])
            if fill:
                for cell in ws[ws.max_row]:
                    cell.fill = PatternFill("solid", fgColor=fill)
        if not rows:
            ws.append(["NO GROUPS EXIST - see the note above."])
    cpath = folder / "classification-sample.xlsx"
    c.save(cpath)

    r = Workbook()
    ws = r.active
    ws.title = "RBAC"
    ws.append(["", "MI-ResourceName", "IdentityType", "RoleName", "ResourceName", "ResourceType", "Notes"])
    ws.append([1, "azr-pes-rg-data-dev-ph-weu-support-reader", "Entra ID Group", "Storage Blob Data Reader", "samplestddevdaiweu01\nsamplestddevlogweu01",
               "Microsoft.Storage/storageAccounts", "SAMPLE support reader"])
    ws.append(["", "", "", "Reader", "pes-rg-data-dev-dai-weu-001", "Microsoft.Resources/resourceGroups", ""])
    ws.append([2, "azr-pes-rg-data-prd-ph-weu-support-reader", "Entra ID Group", "Reader", "pes-rg-data-prd-dai-weu-001", "Microsoft.Resources/resourceGroups", ""])
    rpath = folder / "response-sample.xlsx"
    r.save(rpath)
    return cpath, rpath


def run(write_fixture: bool) -> None:
    with tempfile.TemporaryDirectory() as tmp:
        cpath, rpath = workbook(Path(tmp))
        scope = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/pes-rg-data-dev-dai-weu-001"
        data = iw.build("PEST", "SAMPLE-PEST-IMPORT", cpath, [rpath], {"pes-rg-data-dev-dai-weu-001": scope}, use_az=False, resource_prefix="SAMPLE ")
    rows = {r["entraGroup"]: r for r in data["mappingSheet"]}

    # Tenant separation: the Vestacy tab is never read for PEST.
    assert not any("-ves-" in g for g in rows), rows.keys()
    assert data["project"]["environments"] == ["Dev", "Prod"], data["project"]

    support = rows["azr-pes-rg-data-dev-ph-weu-support"]
    assert (support["environment"], support["accessLevel"], support["flag"], support["groupState"]) == ("Dev", "Support", "Amber", "Exists")
    assert support["resource"] == "SAMPLE PEST Central hub / platform (ph)", support["resource"]
    assert support["groupObjectId"] == "11111111-1111-4111-8111-111111111111"
    assert [(g["roleName"], g["resourceName"]) for g in support["azureRoles"]] == [
        ("Storage Blob Data Contributor", "samplestddevdaiweu01"), ("Reader", "pes-rg-data-dev-dai-weu-001"), ("Reader", "pes-rg-data-dev-acq-weu-001")]
    assert [g["scope"] for g in support["azureRoles"]] == [None, scope, None]

    assert rows["azr-pes-rg-data-dev-fin-weu-powerbidevelopers"]["azureRoles"] == []
    typo = rows["azr-pes-rg-data-dev-sup-weu-powerbideveloper"]
    assert (typo["flag"], typo["groupObjectId"], typo["resource"]) == ("Red", None, "SAMPLE PEST Supply (sup)")

    reader = rows["azr-pes-rg-data-dev-ph-weu-support-reader"]
    assert (reader["groupState"], reader["flag"], reader["accessLevel"]) == ("ToBeCreated", "Amber", "Support Reader")
    assert len(reader["azureRoles"]) == 3 and reader["azureRoles"][2] == {
        "roleName": "Reader", "resourceName": "pes-rg-data-dev-dai-weu-001", "resourceType": "Microsoft.Resources/resourceGroups", "scope": scope, "roleDefinitionId": None}

    prod = rows["azr-pes-rg-data-prd-ph-weu-support-reader"]
    assert (prod["environment"], prod["flag"], prod["groupState"]) == ("Prod", "Red", "ToBeCreated"), prod
    assert "no Prod groups" in prod["notes"]

    for r in data["mappingSheet"]:  # what MappingSheetService would accept
        assert iw.re.match(r"^[A-Za-z0-9_.:-]{1,64}$", r["rowId"]) and r["resource"] and r["accessLevel"] and r["role"] and r["sourceReference"]

    if write_fixture:
        out = Path(__file__).resolve().parent.parent / "tests" / "fixtures" / "import-sample.json"
        for r in data["mappingSheet"]:
            r["updatedAt"] = "2026-10-21T00:00:00+00:00"
            r["isSample"] = True
            r["sourceReference"] = "SAMPLE workbook (tools/test_import_workbook.py)"
        out.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print("wrote", out)
    print("import_workbook: all checks passed")


if __name__ == "__main__":
    run("--fixture" in sys.argv)
