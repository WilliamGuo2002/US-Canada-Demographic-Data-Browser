# CensusScope — US & Canada Demographic Data Browser

A Windows desktop app (C# / WPF, .NET 10) for querying official demographic data —
population, age, gender, income, and education — for the United States and Canada,
at every major administrative level, with an optional plain-language query box
powered by the Gemini API.

Data always comes from the two national statistical agencies, never from an LLM:

| | United States | Canada |
|---|---|---|
| Source | U.S. Census Bureau, **ACS 2020–2024 5-Year Estimates** (`api.census.gov`) | Statistics Canada, **Census of Population 2021** Census Profile (SDMX Web Data Service) |
| Levels | State → County / Place | Province/Territory → Census division → Census subdivision |
| Area IDs | FIPS **GEOID** (`06` = California, `06075` = San Francisco County) | **DGUID** (`2021A000235` = Ontario, `2021A00053520005` = Toronto) |
| Reliability info | 90 % margin of error shown per estimate | Random rounding to base 5; suppression flags shown |

## Setup

1. **.NET 10 SDK** — https://dotnet.microsoft.com/download
2. **U.S. Census API key** (free, required for U.S. data):
   https://api.census.gov/data/key_signup.html
3. **Gemini API key** (free tier, only needed for the plain-language box):
   https://aistudio.google.com/apikey
4. Run the app and enter both keys under **Settings…**. They are stored in
   `%APPDATA%\CensusScope\settings.json`, never in the repository.

```
dotnet run --project src/CensusScope.App
```

Tests:

```
dotnet test
```

### Running without API keys

The whole application runs with no keys at all:

```
dotnet run --project src/CensusScope.App -- --demo
```

Demo mode substitutes canned responses for the two services that require a key (Census and
Gemini) while Statistics Canada still comes from the live service, so Canadian figures are
genuine. U.S. figures are clearly-labelled synthetic placeholders and a banner across the top
of the window says so. Requests still pass through the real `ApiClient` and the real providers,
so demo mode exercises the production URL assembly, chunking, parsing and rendering code —
only the network response is substituted.

The test suite is likewise fully offline: `ApiClient` accepts an injected `HttpMessageHandler`,
so `UsCensusProvider` and `GeminiService` are tested end-to-end against canned responses
without a key or a network connection.

## How to use

1. Pick a **country**, a **geographic level**, and an **area**
   — or pick *(Compare all …)* to rank every area at that level.
2. Pick a **dataset** (each shows its statistical *universe* — the population the
   numbers describe — always read it before dividing anything by anything).
3. **Load data.** Every result shows its full source citation.
4. **Export CSV…** saves the loaded table. The file carries raw numeric values (not the
   formatted strings on screen), a separate margin-of-error column, each row's source code
   (ACS variable id / Census characteristic id in profile mode, GEOID / DGUID in comparison
   mode) as a join key, and a `#` provenance block with the title, source citation and
   statistical universe needed to cite the figures.
5. **Map tab** — an offline choropleth view for U.S. State/County and Canadian
   Province/Census-division levels. Pick a *map variable* and press *Render map*.
   Design rules the map enforces:
   - **Raw counts are never shaded.** Only rates, medians, densities and
     percentages are offered (a raw-count choropleth mostly renders population
     size, not the phenomenon). Percent variables carry an explicit denominator
     from the same dataset; denominators under 100 render as no-data rather than
     fabricating precision from rounded counts.
   - Quantile classification (up to 5 classes, ties collapsed), ColorBrewer
     YlGnBu sequential ramp, gray for no-data, and a legend showing class
     ranges, per-class area counts, the method, and both data and boundary
     citations.
   - Boundaries: U.S. Census cartographic boundary files (2024, 1:20m,
     GEOID-keyed) read natively from shapefile — no GDAL required; Statistics
     Canada 2021 cartographic boundaries fetched as WGS84 GeoJSON from the
     official REST service (DGUID-keyed). Converted once, cached in
     %LOCALAPPDATA%\CensusScope\geo. Alaska is re-wrapped across the
     antimeridian; county/CD maps are scoped to the selected state/province.
   - Rendered by MapLibre GL JS (bundled offline, BSD-3) inside
     WebView2CompositionControl; no tile server, no external requests.
6. Or type a question — *"Which Ontario census division has the highest median
   household income?"* — into the Gemini box. The model only translates your
   question into a structured query (country, level, dataset, area); the numbers
   themselves are fetched from the official APIs, and the interpretation used is
   displayed so you can verify it.

## Architecture

```
src/CensusScope.Core            business logic, no UI dependency
  Models/                     GeoUnit, DemographicQuery, QueryResult, catalog models
  Providers/
    IDemographicProvider.cs   the country abstraction
    UsCensusProvider.cs       ACS via api.census.gov (JSON)
    StatCanProvider.cs        Census Profile via SDMX REST (CSV, codelists)
  Services/                   GeminiService (intent parsing + summarising),
                              GeoResolver (accent-insensitive name matching),
                              AppSettings, CatalogService
  Parsing/                    RFC-4180 CSV, SDMX-ML codelist XML
  Catalogs/                   us-catalog.json / ca-catalog.json  ← the dimension
                              catalogs: datasets, variables, labels, universes
src/CensusScope.App             WPF shell (MVVM, CommunityToolkit.Mvvm)
tools/                        catalog generators (labels come verbatim from the
                              agencies' own metadata — rerun for a new vintage)
tests/                        xunit tests with real API response fixtures
```

Design rules the code follows:

- **Geographic IDs are strings, always.** `06` must never become `6`.
- **Classifications are data, not code.** No shared enum forces U.S. and Canadian
  categories to align; each country ships its native classification in its
  catalog JSON. Adding a dataset is a config edit, not a code change.
- **No cross-border comparison.** U.S. and Canadian figures use different
  definitions (income concepts, education systems, race/ethnicity frameworks)
  and are deliberately presented separately.
- **Gemini never produces numbers.** It maps natural language → a structured
  query and summarises tables it is shown. Data flows only from the agencies.

## Reading the numbers correctly

- **U.S.:** every ACS figure is a survey estimate with a 90 % margin of error
  (shown as ±). Small areas can have MOEs larger than the estimate. Jam values
  (insufficient sample, not applicable…) display as flags, not numbers.
- **Canada:** all counts are randomly rounded to base 5, so categories rarely
  sum exactly to totals — this is normal, not a bug. Education figures are 25 %
  sample data. Income is from CRA tax records (2020 income year); both before-
  and after-tax measures exist and differ.
- **Universes differ per dataset** (e.g. U.S. education covers population 25+;
  Canadian education covers 25–64 in private households). The universe is shown
  with every result.


## Attribution and terms

This application accesses two public government data APIs. Both require attribution,
which the app displays permanently in the left panel — do not remove it:

- **U.S. Census Bureau API** ([Terms of Service](https://www.census.gov/data/developers/about/terms-of-service.html)).
  The notice wording is prescribed verbatim by the "Attribution" section.
- **Statistics Canada**, Census of Population 2021, used under the
  [Statistics Canada Open Licence](https://www.statcan.gc.ca/en/reference/licence).

The Census terms also prohibit using the data — alone or combined with any other data —
to identify any individual person, household, or business, and prohibit circumventing
rate limits. This app only retrieves published aggregate tables, so neither applies to
normal use.

### A note on API keys

The Census key is issued to whoever accepts the terms of service, and the agreement's
indemnity clause survives termination. **For production or team use, obtain the key under
the organization's name and email rather than reusing an individual's personal key.**

Keys are stored per-user in `%APPDATA%\CensusScope\settings.json` and are never written to
this repository. `ApiClient` redacts credential query parameters from error messages, so a
key cannot leak into the status bar or a screenshot.

### Handover checklist

API keys are per-user and are never part of this repository, so a handover is a
configuration step, not a code change.

1. The receiving organization obtains its **own** keys, under its own name and email:
   - Census: https://api.census.gov/data/key_signup.html (free; the emailed
     activation link must be clicked or the key is rejected)
   - Gemini: https://aistudio.google.com/apikey — **this one is billed**, so it must
     be on an organization account, not an individual's
2. They enter both under **Settings…** on each machine that runs the app.
3. The outgoing developer clears their own keys from
   `%APPDATA%\CensusScope\settings.json` (delete the file) and, optionally, the
   response cache at `%LOCALAPPDATA%\CensusScope\cache`.

Liability under the Census terms attaches to use of the API *under a given key*, so
once the organization's keys are in place the previous holder has no remaining
exposure. Do not ship or commit a key with the application.
