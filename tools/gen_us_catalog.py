"""Generate Catalogs/us-catalog.json from live Census API metadata (keyless endpoints).

Labels, universes and variable lists come straight from
https://api.census.gov/data/2024/acs/acs5/groups/{TABLE}.json so they cannot drift
from the source. Re-run when moving to a new ACS vintage.
"""
import json, re, urllib.request, pathlib

BASE = "https://api.census.gov/data/2024/acs/acs5"
OUT = pathlib.Path(__file__).resolve().parent.parent / "src/CensusScope.Core/Catalogs/us-catalog.json"

def fetch_group(table):
    with urllib.request.urlopen(f"{BASE}/groups/{table}.json", timeout=60) as r:
        return json.loads(r.read().decode("utf-8"))

def auto_vars(table, fmt="count"):
    """All estimate variables of a table, labels derived from the official label strings."""
    g = fetch_group(table)
    out = []
    for name in sorted(g["variables"]):
        if not re.fullmatch(r"[A-Z0-9_]+_\d{3}E", name):
            continue
        raw = g["variables"][name]["label"]           # e.g. "Estimate!!Total:!!Male:!!Under 5 years"
        parts = raw.replace("Estimate!!", "").split("!!")
        label = parts[-1].rstrip(":")
        out.append({"code": name, "label": label, "indent": len(parts) - 1, "format": fmt})
    return out

def var(code, label, fmt="count", indent=0):
    return {"code": code, "label": label, "indent": indent, "format": fmt}

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
        "key": "population_overview",
        "displayName": "Population overview",
        "sourceTable": "B01003 / B01002 / B19301",
        "universe": "Total population",
        "notes": "Median age and per-capita income are point estimates with 90% confidence margins of error.",
        "variables": [
            var("B01003_001E", "Total population"),
            var("B01002_001E", "Median age - Total", "decimal"),
            var("B01002_002E", "Median age - Male", "decimal", 1),
            var("B01002_003E", "Median age - Female", "decimal", 1),
            var("B19301_001E", "Per capita income (past 12 months, 2024 dollars)", "currency"),
        ],
        "compareVariables": ["B01003_001E", "B01002_001E"],
        "mapVariables": [
            mapvar("pop_density", "Population density (per km²)", ["B01003_001E"], "density", "decimal"),
            mapvar("median_age", "Median age", ["B01002_001E"], "direct", "decimal"),
            mapvar("per_capita_income", "Per capita income", ["B19301_001E"], "direct", "currency"),
        ],
    },
    {
        "key": "sex_by_age",
        "displayName": "Sex by age",
        "sourceTable": "B01001",
        "universe": None,  # filled from metadata
        "variables": ("auto", "B01001", "count"),
        "compareVariables": ["B01001_001E", "B01001_002E", "B01001_026E"],
        "mapVariables": [
            mapvar("pct_female", "Female share of population (%)", ["B01001_026E"],
                   "percent", "percent", denominator="B01001_001E"),
            mapvar("pct_65plus", "Population 65 and over (%)",
                   ["B01001_020E", "B01001_021E", "B01001_022E", "B01001_023E", "B01001_024E", "B01001_025E",
                    "B01001_044E", "B01001_045E", "B01001_046E", "B01001_047E", "B01001_048E", "B01001_049E"],
                   "percent", "percent", denominator="B01001_001E"),
        ],
    },
    {
        "key": "household_income",
        "displayName": "Household income (brackets + median)",
        "sourceTable": "B19001 / B19013",
        "universe": None,
        "notes": "Income in the past 12 months, inflation-adjusted to 2024 dollars.",
        "variables": ("auto+", "B19001", "count",
                      [var("B19013_001E", "Median household income (past 12 months, 2024 dollars)", "currency")]),
        "compareVariables": ["B19013_001E"],
        "mapVariables": [
            mapvar("median_hh_income", "Median household income", ["B19013_001E"], "direct", "currency"),
        ],
    },
    {
        "key": "education_by_sex",
        "displayName": "Educational attainment by sex (25+)",
        "sourceTable": "B15002",
        "universe": None,
        "variables": ("auto", "B15002", "count"),
        "compareVariables": ["B15002_001E", "B15002_015E", "B15002_032E"],
        "mapVariables": [
            mapvar("pct_bachelors_plus", "Bachelor's degree or higher, 25+ (%)",
                   ["B15002_015E", "B15002_016E", "B15002_017E", "B15002_018E",
                    "B15002_032E", "B15002_033E", "B15002_034E", "B15002_035E"],
                   "percent", "percent", denominator="B15002_001E"),
        ],
    },
    {
        "key": "race",
        "displayName": "Race",
        "sourceTable": "B02001",
        "universe": None,
        "variables": ("auto", "B02001", "count"),
        "compareVariables": ["B02001_001E", "B02001_002E", "B02001_003E"],
        "mapVariables": [
            mapvar("pct_white", "White alone (%)", ["B02001_002E"],
                   "percent", "percent", denominator="B02001_001E"),
            mapvar("pct_black", "Black or African American alone (%)", ["B02001_003E"],
                   "percent", "percent", denominator="B02001_001E"),
        ],
    },
    {
        "key": "hispanic_or_latino",
        "displayName": "Hispanic or Latino origin by race",
        "sourceTable": "B03002",
        "universe": None,
        "variables": ("auto", "B03002", "count"),
        "compareVariables": ["B03002_001E", "B03002_012E", "B03002_003E"],
        "mapVariables": [
            mapvar("pct_hispanic", "Hispanic or Latino (%)", ["B03002_012E"],
                   "percent", "percent", denominator="B03002_001E"),
        ],
    },
]

for d in datasets:
    v = d["variables"]
    if isinstance(v, tuple):
        if v[0] == "auto":
            d["variables"] = auto_vars(v[1], v[2])
            meta_table = v[1]
        else:  # auto+
            d["variables"] = auto_vars(v[1], v[2]) + v[3]
            meta_table = v[1]
        if d["universe"] is None:
            g = fetch_group(meta_table)
            first = next(iter(g["variables"].values()))
            d["universe"] = first.get("universe", "")
    print(f"{d['key']}: {len(d['variables'])} variables, universe = {d['universe']!r}")

catalog = {
    "countryCode": "US",
    "countryName": "United States",
    "sourceName": "U.S. Census Bureau, American Community Survey 2020-2024 5-Year Estimates",
    "geoLevels": [
        {"code": "state", "displayName": "State", "parent": None},
        {"code": "county", "displayName": "County", "parent": "state"},
        {"code": "place", "displayName": "Place (city / town / CDP)", "parent": "state"},
    ],
    "datasets": datasets,
}
OUT.parent.mkdir(parents=True, exist_ok=True)
OUT.write_text(json.dumps(catalog, indent=2, ensure_ascii=False), encoding="utf-8")
print("wrote", OUT)
