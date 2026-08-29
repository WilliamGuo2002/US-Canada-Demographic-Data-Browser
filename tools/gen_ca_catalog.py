"""Generate Catalogs/ca-catalog.json from the official 2021 Census Profile characteristic
codelist (tools/data/char_ids.tsv, extracted from SDMX CL_CHARACTERISTIC).

Names come verbatim from Statistics Canada; this script only selects the Tier 1 subset
and assigns display hierarchy (indent) and value format.
"""
import json, pathlib, sys

ROOT = pathlib.Path(__file__).resolve().parent
NAMES = {}
for line in (ROOT / "data/char_ids.tsv").read_text(encoding="utf-8-sig").splitlines():
    if line.strip():
        cid, name = line.split("\t", 1)
        NAMES[int(cid)] = name.strip()

def block(spec):
    """spec: list of (id, indent) or (id, indent, format)."""
    out = []
    for item in spec:
        cid, indent, fmt = (item + ("count",))[:3] if len(item) == 2 else item
        if cid not in NAMES:
            sys.exit(f"characteristic id {cid} not found in codelist")
        out.append({"code": str(cid), "label": NAMES[cid], "indent": indent, "format": fmt})
    return out

def mapvar(key, label, numerator, kind, fmt, denominator=None):
    """A derived choropleth indicator: numerator codes (summed), optional denominator."""
    mv = {"key": key, "label": label, "numerator": numerator}
    if denominator is not None:
        mv["denominator"] = denominator
    mv["kind"] = kind
    mv["format"] = fmt
    return mv

datasets = [
    {
        "key": "population_dwellings",
        "displayName": "Population and dwellings",
        "sourceTable": "Census Profile 2021 - Population and dwelling counts",
        "universe": "Total population and all private dwellings, 100% data",
        "gendered": False,
        "variables": block([(1, 0), (3, 0, "percent"), (4, 0), (5, 1), (6, 0, "decimal"), (7, 0, "area")]),
        "compareVariables": ["1", "6"],
        "mapVariables": [
            mapvar("pop_density", "Population density (per km²)", ["6"], "direct", "decimal"),
            mapvar("pop_change", "Population change 2016 to 2021 (%)", ["3"], "direct", "percent"),
        ],
    },
    {
        "key": "age_gender",
        "displayName": "Age groups by gender",
        "sourceTable": "Census Profile 2021 - Age",
        "universe": "Total population, 100% data",
        "notes": "2021 reports gender (Men+ / Women+), not sex; non-binary people are distributed within these categories.",
        "variables": block(
            [(8, 0), (9, 1), (10, 2), (11, 2), (12, 2), (13, 1)]
            + [(i, 2) for i in range(14, 24)]
            + [(24, 1), (25, 2), (26, 2), (27, 2), (28, 2), (29, 2), (30, 3), (31, 3), (32, 3), (33, 3)]
            + [(34, 0)] + [(i, 1, "percent") for i in range(35, 39)]
            + [(39, 0, "decimal"), (40, 0, "decimal")]),
        "compareVariables": ["8", "40"],
        "mapVariables": [
            mapvar("median_age", "Median age", ["40"], "direct", "decimal"),
            mapvar("pct_65plus", "Population 65 and over (%)", ["24"], "percent", "percent", denominator="8"),
        ],
    },
    {
        "key": "income_individuals",
        "displayName": "Individual income (2020)",
        "sourceTable": "Census Profile 2021 - Income",
        "universe": "Population aged 15 years and over in private households, 100% data (CRA administrative records, 2020 income year)",
        "variables": block(
            [(111, 0), (113, 1, "currency"), (115, 1, "currency"), (128, 1, "currency"), (130, 1, "currency"),
             (148, 0), (149, 1), (150, 1)]
            + [(i, 2) for i in range(151, 162)] + [(162, 3), (163, 3)]),
        "compareVariables": ["113", "115"],
        "mapVariables": [
            mapvar("median_income", "Median total income, 2020", ["113"], "direct", "currency"),
        ],
    },
    {
        "key": "income_households",
        "displayName": "Household income (2020)",
        "sourceTable": "Census Profile 2021 - Income",
        "universe": "Private households, 100% data (CRA administrative records, 2020 income year)",
        "gendered": False,
        "variables": block(
            [(228, 0), (229, 1, "currency"), (230, 1, "currency"), (238, 1, "currency"), (239, 1, "currency"),
             (246, 0)] + [(i, 1) for i in range(247, 263)] + [(263, 2), (264, 2), (265, 2), (266, 2)]),
        "compareVariables": ["229", "230"],
        "mapVariables": [
            mapvar("median_hh_income", "Median household income, 2020", ["229"], "direct", "currency"),
            mapvar("median_hh_income_at", "Median after-tax household income, 2020", ["230"], "direct", "currency"),
        ],
    },
    {
        "key": "low_income",
        "displayName": "Low income (LIM-AT / LICO-AT)",
        "sourceTable": "Census Profile 2021 - Income",
        "universe": "Population in private households (LICO: population to whom the low-income concept applies)",
        "notes": "LIM-AT and LICO-AT are Canada's two low-income lines; prevalence rows are percentages.",
        "variables": block(
            [(321, 0), (326, 1), (331, 1, "percent"), (336, 0), (341, 1), (346, 1, "percent")]),
        "compareVariables": ["331", "346"],
        "mapVariables": [
            mapvar("lim_at_prevalence", "LIM-AT low-income prevalence (%)", ["331"], "direct", "percent"),
        ],
    },
    {
        "key": "education_25_64",
        "displayName": "Highest certificate, diploma or degree (25-64)",
        "sourceTable": "Census Profile 2021 - Education",
        "universe": "Population aged 25 to 64 in private households, 25% sample data",
        "notes": "25% sample data: values carry sampling variability (95% confidence intervals shown where published).",
        "variables": block(
            [(2014, 0), (2015, 1), (2016, 1), (2017, 1), (2018, 2), (2019, 3), (2020, 4), (2021, 4),
             (2022, 3), (2023, 3), (2024, 2), (2025, 3), (2026, 3), (2027, 3), (2028, 3), (2029, 3)]),
        "compareVariables": ["2014", "2024"],
        "mapVariables": [
            mapvar("pct_bachelors_plus", "Bachelor's or higher, 25–64 (%)", ["2024"],
                   "percent", "percent", denominator="2014"),
        ],
    },
]

catalog = {
    "countryCode": "CA",
    "countryName": "Canada",
    "sourceName": "Statistics Canada, Census of Population 2021 (Census Profile)",
    "geoLevels": [
        {"code": "PR", "displayName": "Province / Territory", "parent": None},
        {"code": "CD", "displayName": "Census division", "parent": "PR"},
        {"code": "CSD", "displayName": "Census subdivision (municipality)", "parent": "CD"},
    ],
    "datasets": datasets,
}

out = ROOT.parent / "src/CensusScope.Core/Catalogs/ca-catalog.json"
out.write_text(json.dumps(catalog, indent=2, ensure_ascii=False), encoding="utf-8")
for d in datasets:
    print(d["key"], len(d["variables"]))
print("household income bracket labels:")
for v in datasets[3]["variables"][5:]:
    print("  ", v["code"], "|", v["label"])
