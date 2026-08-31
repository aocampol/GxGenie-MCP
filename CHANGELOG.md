# Changelog

All notable changes to GxGenie are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [1.4.1] — 2026-08-31 — Fix: `gx_update_object_code` on `documentation` · stale worker responses

Fixes the three bugs in
`docs/MCP_GeneXus_Bug_Report_gx_update_object_code_documentation.md`
(reported against the `APEX` KB, `BDevolucionExtraordinaria` Transaction). No
new tool; tool count unchanged.

### Fixed — Bug 1: Part lookup wasn't scoped to the requested `<Object>`

`WriteTools.ReplacePartSourceInXpz` (the XPZ patcher behind
`gx_update_object_code`) searched for `<Part type="guid">` across the
**entire** exported XPZ instead of inside the specific `<Object>` for the
requested object. The `documentation` Part's type-guid is shared by every
object type, including `Attribute`, so exporting a Transaction with N
attributes produced N+1 matching `<Part>` nodes and the call always failed
with `InvalidOperationException: Se encontraron N <Part type="..."> —
esperaba exactamente 1`.

- The XPZ is now searched for the `<Object type="{objectTypeGuid}"
  name="{name}">` matching the request first — the same pattern
  `gx_add_attribute`/`gx_remove_attribute` already used — and the target
  `<Part>` is looked up only among that Object's children.
- New `XpzPartMap.ObjectTypeGuidFor(objectType)` maps every known object type
  name to its `<Object type="...">` GUID.

### Fixed — Bug 2: `documentation` content silently failed to persist

The patcher assumed every editable Part stores its text in a `<Source>`
child element. Parts of `kind="html"` (`documentation`) actually store it in
`<InnerHtml>` — confirmed against
`GxGenie.Worker/probes/discovery/parts-discovery-report.md`, generated in an
earlier discovery session but never cross-checked against this code path. A
Part that was never filled in from the IDE exports with only `<Properties
/>` — no text element at all — so even picking the right tag wasn't enough.
Because MSBuild's `Import` task doesn't error on an unrecognized child
element inside a `<Part>`, the whole operation looked successful (import log
ended in `Import Task Success`) while the content was discarded.

- `ReplacePartSourceInXpz` now picks `Source` or `InnerHtml` based on the
  Part's `Kind` (from `XpzPartMap`), and creates the element (before
  `<Properties>`) when the Part has no prior content.

### Fixed — Bug 3: unrelated tool calls could return a previous call's response

`GxGenie.Worker`'s stdin/stdout loop is strictly synchronous and FIFO: it
reads one request line, blocks until it's fully processed, writes one
response line, then reads the next. `WorkerProxy.CallAsync` didn't account
for what happens when the *Gateway* times out waiting: it abandoned the
read, but the Worker kept running the same request to completion and wrote
its response anyway — left unconsumed in the pipe. The next, unrelated
`CallAsync` would then read that stale line first and return it as if it
were its own result (observed: `gx_read_object` and a following
`gx_build_object` call returning byte-identical output).

- The Worker already echoes the request's `id` in every response
  (`WorkerResponse.Ok/Fail(..., req.Id)`) but the Gateway never checked it.
  `CallAsync` now loops on `ReadLineAsync`, discarding any line whose `id`
  doesn't match the request it just sent, until the matching one arrives (or
  the timeout budget is exhausted). Bounded by construction — the Worker
  processes exactly one request at a time in enqueue order, so at most one
  stale line can be pending per prior timeout.

### Not fixed — deferred

The bug report also suggested a post-import verification step (re-read the
object after `Import` and confirm the expected content actually landed,
instead of trusting the MSBuild log alone). Out of scope for this release;
tracked as a follow-up.

### Validated

`GxGenie.Worker` and `GxGenie.Gateway` both rebuild clean (0 errors). Not yet
re-validated E2E against a live KB — the `documentation`/`InnerHtml` shape
fix in particular should be re-run against the original `APEX` repro
(`BDevolucionExtraordinaria`) once the `genexus` MCP connection is
reconnected.

---

## [1.4.0] — 2026-07-02 — Fix: `gx_search` code-scan cap · homonym disambiguation (`module` param)

Fixes the two bugs in `docs/MCP_GeneXus_Bug_Report_gx_search.md` (reported
against the `APEX` KB, 142k entities). No new tool; tool count unchanged.

### Fixed — Bug 1: `gx_search search_in="code"` silently skipped whole object types

The code scan had a hard-coded `TOP 4000` over `EntityVersionComposition`
with no `ORDER BY`: only the first 4,000 source-bearing part rows (in
physical/index order, grouped by part type) were ever decoded. In a large KB
the WebPanel/Transaction *Events* rows alone filled that budget, so
**Procedures (and any type whose parts sorted later) never appeared in any
code search** — no error, no warning. In `APEX` that hid 53 Procedures
calling `GAMRepository.CheckPermission`.

- The cap is gone: the scan now visits **every** source-bearing part
  (source / rules / events / conditions) and still stops early once `limit`
  hits are collected. Full scan of the 142k-entity `APEX` KB: ~11 s.
- New optional `type` parameter on `gx_search` to restrict the scan to one
  object type (e.g. only `Procedure`) — faster and less noisy for audits.
- New optional `module` parameter on `gx_search` to keep only hits from one
  module (`""` = root module only).
- The response now includes `limit_reached` so a truncated result is visible
  instead of silent.

### Fixed — Bug 2: homonym objects inside Modules were unreachable / silently wrong

`gx_read_object` resolved names with `SELECT TOP 1 … ORDER BY EntityTypeId`:
with a root object and a same-named object inside a GeneXus Module (e.g.
`BuscaConvenioPromo` in root **and** in `Cotizaciones`), it always returned
the root one with no warning, and there was no way to read the module one.

- All name-resolving read tools (`gx_read_object`, `gx_list_attributes`,
  `gx_get_structure`, `gx_get_layout`, `gx_get_variables`,
  `gx_get_unused_variables`) now accept:
  - an optional **`module`** parameter (dotted path, e.g. `"Cotizaciones"` or
    `"LISAPI.V1"`; `""` pins the root module), and
  - a **qualified name** `Modulo.Objeto` in the `name` field (tried when the
    raw name matches nothing — GeneXus names cannot contain dots).
- When a bare name matches objects in **more than one module** and no module
  was given, the tools now fail with an explicit `AmbiguousObjectException`
  listing every candidate (type, id, module) instead of silently picking one.
  Same-module homonyms of different types (a Transaction and its same-named
  Table) keep the historical lowest-`EntityTypeId` pick, so the common
  `gx_read_object` without `type` is not broken.
- The write path benefits indirectly: layout writes (`gx_set_control_property`,
  `gx_add_control`, `gx_remove_control`) resolve the object through the same
  reader and now refuse to run on an ambiguous name instead of mutating the
  wrong object. **Known limitation**: writes that resolve via MSBuild tasks
  (`gx_update_object_code`, `gx_export_xpz`, `gx_build_object`,
  `gx_delete_object`) still pass the unqualified name to GeneXus and are not
  module-aware.

### Validated

E2E against the `APEX` KB (GX17, 142,053 entities) with the bug report's exact repros:

1. `gx_search(query="CheckPermission", search_in="code", limit=5000)` →
   198 hits (was 110) including **43 Procedures** (was 0); all 31 Procedure
   names individually confirmed in the report are present. ~11 s full scan.
2. `gx_read_object(name="Cotizaciones.BuscaConvenioPromo")` → returns the
   module object (id 2491) with all parts decoded (was: not found).
3. `gx_read_object(name="BuscaConvenioPromo", type="WebPanel")` → explicit
   ambiguity error listing both candidates (was: silently returned id 87).
   `module="Cotizaciones"` and `module=""` each select the right one.
4. Regression: `GxGenie.Gateway/test-mcp.ps1` (SampleKB) passes; `gx_search`
   name mode, `type`/`module` filters and qualified names on the inspector
   tools all verified.

---

## [1.3.1] — 2026-05-21 — Fix: objects nested in modules/folders were invisible

Bug fix for `docs/MCP-BUG-REPORT-modulos.md`. Catalog tools silently omitted
every object nested inside a Module, Folder or WorkWithPlus instance — ~1,144
objects in the `APEX_DESA` KB (480 WebPanels, 235 Procedures, 131 SDTs, 114
Tables, 114 Transactions, …). No new tool; tool count stays at 28.

### Root cause

Every read query resolved an object's current version by joining
`EntityVersion` on `Entity.EntityLastVersionId`. For any object (or object
*part*) nested in a container, that pointer is **stale — off by one ahead** of
the real `EntityVersion` row, so the inner join produced zero rows and the
object vanished from `gx_list_objects`, `gx_search` and `gx_read_object`. The
diagnosis in the bug report ("only objects in sub-modules") was incomplete: the
offending object was in the root module, and the same staleness hits objects
under Folders and WorkWithPlus too — it is unrelated to nesting depth.

The authoritative current version lives in `ModelEntityVersion` (the
design-model pointer, `ModelId = 1`), correct for 100 % of objects and parts.

### Fixed

- New `CurrentVersionJoin` SQL fragment in `KbRepository` resolves the current
  `EntityVersion` via `ModelEntityVersion`, with a `COALESCE` fallback to
  `EntityLastVersionId` for the handful of part entities that have no
  `ModelEntityVersion` row (8 of 91,292 in `APEX_DESA`) — zero regressions.
- Applied to `gx_list_objects`, `gx_search` (`name` **and** `code`),
  `gx_read_object` (object resolution **and** each part's version), and the
  internal name/attribute resolvers. The structured-read tools
  (`gx_get_structure`, `gx_get_variables`, …) inherit the fix through
  `KbRepository`.
- `gx_list_objects` count now matches `gx_kb_info` object counts exactly
  (previously the list under-reported).

### Added — module path in catalog output

- `gx_list_objects`, `gx_search` and `gx_read_object` now return a `module`
  field with the dotted GeneXus module namespace of each object (e.g.
  `"LISAPI.V1"`, `"GeneXus.Common.Notifications"`). Omitted when the object
  lives directly in the root module. Folders and WorkWithPlus instances are
  traversed but not part of the namespace. This disambiguates homonym objects
  living in different modules. Resolution is backed by a lazily-cached
  container tree built from `ModelEntityVersion`.

### Validated

E2E against the `APEX_DESA` KB (GeneXus 17 U1):

1. The bug report's exact repro — `gx_read_object`, `gx_search`,
   `gx_list_objects` for `PBuscaServicioDomicilio` — all now succeed.
2. `gx_list_objects type=Procedure` returns 4,169 (was 3,934) and `WebPanel`
   2,652 (was 2,172), both matching `gx_kb_info`.
3. `gx_read_object` on the sub-module object `PLISGuardaFechaPromesa` returns
   `module = "LISAPI.V1"` with all parts (help/source/rules/variables) decoded.
4. Regression — previously-visible objects and the `GxGenieTest` KB
   (list count == sum of `kb_info` counts) unchanged.

---

## [1.3.0] — 2026-05-20 — `gx_create_transaction` · multi-level (sub-levels)

`gx_create_transaction` gains an optional `levels` parameter for creating
multi-level (master-detail) Transactions in a single call. Fully backward
compatible — omitting `levels` produces the exact same flat single-level
Transaction as 1.2.0. Tool count stays at 28.

### Added — `levels` parameter on `gx_create_transaction`

`levels` is an array of sub-level definitions nested under the root level, and
it is **recursive**: every level carries `name`, `attributes`, and optionally
its own `levels` (so Order → OrderLine → OrderLineSerial works in one call).
Each attribute has `name`, `data_type` (same `bas:*` enum as the root key),
`length`, `decimals` and `is_key`.

- Per-attribute defaults mirror the root key: `data_type` → `bas:Numeric`,
  `length` → 8 (Numeric) / 40 (Character/VarChar), length-less for
  Date/DateTime/Boolean. Numeric **key** attributes are emitted as AUTONUMBER.
- Every level needs exactly one key; if the caller flags none with `is_key`,
  the first attribute of that level is auto-promoted to key.
- An attribute name already present in the KB is reused (`Import OnlyNew`),
  same as the root key. The same name used across levels references the one
  KB attribute (the parallel `<Attributes>` section is de-duplicated by name).
- The root level still carries only its key attribute — add non-key root
  attributes with `gx_add_attribute` after creating the Transaction.
- Validation runs entirely before the SQL snapshot: name regex on every level
  and attribute, data-type enum, no duplicate attribute within a level, and a
  nesting-depth cap of 8 levels under the root.
- The response gains `sub_levels_created` — the recursive count of sub-levels.

### Validated

Full roundtrip in `GxGenie.Worker/probes/discovery/e-multilevel-transaction.ps1`
against the `GxGenieTest` KB:

1. Two-level `E1Invoice` + `E1InvoiceLine` (3 attributes) — `gx_get_structure`
   shows the detail level with `E1LineId` KEY, `E1Product` Character 60, `E1Qty`.
2. Three-level `E2Order` → `E2OrderLine` → `E2OrderLineSerial` — structure nests
   3 deep, `sub_levels_created` = 2.
3. A sub-level with no `is_key` flagged — the first attribute is auto-promoted.
4. Regression — `gx_create_transaction` with no `levels` still yields a flat
   single-level Transaction (`sub_levels_created` = 0).

The 1.2.0 `d-create-transaction.ps1` probe still passes unchanged.

---

## [1.2.0] — 2026-05-20 — `gx_create_transaction`

One new MCP tool that closes the last "create" gap left in 1.1.0: Transactions
could only be created by hand-assembling an XPZ and calling `gx_import_xpz`.
Total tool count goes from 27 → 28. No breaking changes.

### Added — `gx_create_transaction(name, description?, key_attribute?, key_data_type?, key_length?, module?)`

Creates a new Transaction with a single root Level and one key Attribute —
the same ergonomics as `gx_create_procedure`. Internally it materialises a
minimal XPZ (an `<Object>` of type Transaction with its Structure Part, the
parallel `<Attributes>` section holding the key Attribute, and the
`<Dependencies>` the Import task expects) and drives `MSBuild Import` with
`ImportType=OnlyNew`. A SQL `BACKUP DATABASE` snapshot is taken before the
import, and every call is recorded in the audit log.

Parameters:

- `name` — Transaction name, regex `^[A-Za-z][A-Za-z0-9_]{0,63}$`.
- `description` — optional, defaults to `name`.
- `key_attribute` — name of the key attribute created with the Transaction.
  Defaults to `<name>Id` (the GeneXus convention).
- `key_data_type` — one of `bas:Numeric` (default), `bas:Character`,
  `bas:VarChar`, `bas:Date`, `bas:DateTime`, `bas:Boolean`.
- `key_length` — defaults to 8 (Numeric) or 40 (Character/VarChar); ignored for
  the length-less types. Numeric keys are emitted as `AUTONUMBER`.
- `module` — accepted but ignored for now (same as `gx_create_procedure`).

**Attribute reuse**: if an attribute with the requested `key_attribute` name
already exists in the KB, `Import OnlyNew` reuses it with its current type —
it is not overwritten. The response field `key_attribute_reused` (bool) tells
the caller which happened; pass a different `key_attribute` to force a new one.

Pre-Import validations (the KB is not touched until all pass): the `name` regex,
`key_data_type` is in the allowed enum, and **no Transaction with that name
already exists** — without that last check `Import OnlyNew` would "succeed"
silently without creating anything, which is confusing for the caller.

Sub-levels are out of scope for this version: for a multi-level Transaction,
create the root with this tool and add sub-levels via `gx_import_xpz`.

### Validated

Full roundtrip in `GxGenie.Worker/probes/discovery/d-create-transaction.ps1`
against the `GxGenieTest` KB:

1. `gx_create_transaction D1TestTrn` with defaults → `gx_get_structure` shows a
   root Level with `D1TestTrnId` Numeric 8 flagged KEY; `key_attribute_reused`
   is `false`.
2. `gx_create_transaction D1TestTrn` again → rejected pre-Import (already
   exists), KB unchanged.
3. `gx_create_transaction D1OtherTrn key_attribute=D1TestTrnId` → success with
   `key_attribute_reused=true`; the new Transaction references the existing
   `D1TestTrnId` attribute.
4. `gx_create_transaction D1WithChar key_attribute=D1WithCharCod
   key_data_type=bas:Character key_length=10` → structure shows
   `D1WithCharCod` Character 10 KEY.
5. `gx_create_transaction ThisHasABadKey$$$` → rejected (name regex mismatch).

The probe is idempotent: it restores the KB from the SQL backup the first
create takes automatically (see the known limitation below).

### Known limitation

`gx_delete_object` cannot remove a Transaction created by this tool — the
MSBuild `DeleteObject` task reports "not found" for objects imported through a
minimal XPZ (the same documented limitation that affects `gx_create_procedure`;
see README "Known limitations"). To undo a `gx_create_transaction`, restore the
`.bak` it took before the import, or delete the object from the GeneXus IDE.

---

## [1.1.0] — 2026-05-19 — Phase C · Variable management

Two new MCP tools that close the variables-management gap left in 1.0.0:
`gx_get_variables` could read but there was no way to remove anything.
Total tool count goes from 25 → 27. No breaking changes.

### Added — `gx_get_unused_variables(name, type?)`

Read-only. Detects variables declared in the Variables Part of a Procedure /
DataProvider / WebPanel / Transaction that aren't referenced anywhere else in
the same object. Internally reuses `KbRepository.ReadObject` (so all Parts are
already decoded) and runs a regex (`&Name` + word-boundary, case-insensitive)
against each of `events`, `rules`, `conditions`, `source` that the object has.

Response carries one entry per variable with `name`, `data_type`, `length`,
`referenced` (bool), `reference_count`, `is_standard`, and `references_by_part`
(map: part → count). A separate `candidates` array surfaces the non-standard
variables with zero references — the ones that would be safe to remove with
`gx_remove_variable`. `<StandardVariable>` entries (Today, Time, Pgmname, …)
are reported under `standard_unused` but never enter `candidates`: they belong
to the GeneXus runtime and `gx_remove_variable` will reject them anyway.

Caveat documented in the tool description: the regex counts occurrences in
string literals and comments too, so a variable used only inside `/* &X */`
will still be reported as "referenced". This is intentional — false-positives
on the *referenced* side are safer than false-positives on the *unused* side.

### Added — `gx_remove_variable(object, name)`

Removes a single `<Variable>` from the Variables Part of an object. `object`
is a `Type:Name` string (e.g. `Procedure:MyProc`). Pre-checks (the KB is not
touched until all of them pass):

1. The variable must exist in the object's Variables Part.
2. `<StandardVariable>` is rejected with a clear "part of the runtime" message.
3. The variable must not be referenced — internally calls the same scanner
   that powers `gx_get_unused_variables` and lists which Parts hit if not.

If pre-checks pass: SQL snapshot via `BackupHelper.Snapshot` →
MSBuild `Export` to a temp `.xpz` → remove `<Variable Name="X">` from the
host object's `<Part type="e4c4ade7-…">` block → re-zip →
`NormalizeXpzForImport` (strips the same `<StructureTypeReference>` tokens
documented in 1.0.0) → MSBuild `Import` with `ImportType=UpdatedAndNew`.

Response: `{ success, object, name, kb_variable_was_referenced: false,
tokens_stripped, backup_path, xpz_path, log_tail }`. Every call (success or
failure) goes to the audit log.

### Validated

Full roundtrip in `GxGenie.Worker/probes/discovery/c-variables-roundtrip.ps1`
against the `GxGenieTest` KB:

1. Imports `Procedure:C1TestProc` with two user variables (`C1UsedVar` used in
   `source`, `C1UnusedVar` unused).
2. `gx_get_unused_variables` reports exactly `C1UnusedVar` under `candidates`;
   the auto-included standards (`Today`, `Time`, `Pgmname`, `Pgmdesc`, `Page`,
   `Line`, `Output`) all land under `standard_unused`.
3. `gx_remove_variable Procedure:C1TestProc C1UnusedVar` succeeds. A re-read
   confirms only `C1UsedVar` remains among user variables, and the `.bak` is
   on disk.
4. `gx_remove_variable Procedure:C1TestProc C1UsedVar` is rejected pre-Import
   with `Variable '&C1UsedVar' is still referenced (1 time(s)) in: source=1`.
5. `gx_remove_variable Procedure:C1TestProc Today` is rejected pre-Import
   with `'&Today' is a StandardVariable … cannot be removed`.
6. `gx_remove_variable Procedure:C1TestProc C1DoesNotExist` is rejected with
   an "Available" hint listing the variables that *are* present.

---

## [1.0.0] — 2026-05-19 — Phase A + B1 + B2 + B3 stabilised

First production-ready milestone. 25 MCP tools, multi-KB hot switching,
write-enabled per-KB by default, recoverable via automatic SQL backup
before every destructive op. The plan from `CLAUDE.md` Phases 0–4 is
fully covered; Phases A / B1 / B2 / B3 (read/write granularity over
Parts, Structure and Layout) were added on top during the road to 1.0.

12 new MCP tools relative to 0.1.0, no breaking changes to `config.json`
format. The per-KB install (`setup.ps1 -InstallToKb`) now ships
writes/builds enabled by default (see "Fixed" below).

### Fixed — Import fails on `<StructureTypeReference>` tokens in events

`gx_import_xpz` (and by extension `gx_update_object_code`, which imports a
patched XPZ internally) failed with `exit=1` and parser errors like
`src0059 Expecting 'EndFor'` / `ENDFOR (... Events, Line: 765)` when the XPZ
contained `new <StructureTypeReference><Type>…</Type><Id>…</Id></StructureTypeReference>()`
in any text Part. These tokens are how GeneXus stores `new()` with explicit
SDT type information in the SQL blob — the IDE writes them, MSBuild Export
strips them on the way out, but MSBuild Import rejects them on the way in.

The asymmetric Export/Import behaviour broke this flow:
1. User reads a Part via `gx_read_object` — gets text with tokens (SQL direct).
2. User edits the text (text + tokens still present).
3. User calls `gx_update_object_code` with the edited text.
4. The internal Export → patch → Import pipeline writes the edited text
   (tokens included) into the XPZ and Import dies on the tokens.

**Fix**: `WriteTools.NormalizeXpzForImport` runs over every `<Source>` element
inside the XPZ right before MSBuild Import. It strips
`new <StructureTypeReference>…</StructureTypeReference>(` down to `new (`,
rewrites the XPZ in place, and reports the strip count back to the caller.
The IDE re-infers the type annotation on next save, so the loss is cosmetic.

Called from `ImportXpz` (covers `gx_import_xpz` and external XPZs), and
inside the import phase of `UpdateObjectCode` (covers `gx_update_object_code`,
plus the B3 layout writers `gx_set_control_property` / `gx_add_control` /
`gx_remove_control` which all flow through it).

The response object now includes a `tokens_stripped` field so the caller
can see how many tokens were normalised; the audit log includes the same.
Validated with a synthetic XPZ containing two tokens: pre-call scan finds
them, post-call scan finds zero, and the source is rewritten to `new (`.

### Fixed — Per-KB install was silently read-only

The `.mcp.json` that `setup.ps1 -InstallToKb` generated had no `env` block, so
the Worker entered auto-detect-by-cwd mode (which defaults `AllowWrite=false`
and `AllowBuild=false`) and **ignored the `Security.AllowWrite=true` set in
the global `config.json`**. The user-visible symptom was every write tool
refusing to run with "Write operations are disabled" even after explicitly
enabling them in `config.json`. The root cause is that Claude Code spawns the
MCP server with a clean environment, so env vars exported in the user's shell
don't reach the Worker process — they have to live inside `.mcp.json`.

The fix changes the **default** of `setup.ps1 -InstallToKb`: the generated
`.mcp.json` now ships with
```json
"env": { "GXGENIE_ALLOW_WRITE": "true", "GXGENIE_ALLOW_BUILD": "true" }
```
out of the box. The rationale is that the *point* of the MCP is to modify the
KB, and the safety net (automatic SQL `BACKUP DATABASE` to `.bak` before every
destructive op + append-only `audit.log`) is already on for every write — so
"safe by default" doesn't actually require disabling the feature, it requires
the recovery path to be automatic.

Two new switches restore explicit control:

- **`-ReadOnly`** — emits a `.mcp.json` *without* the `env` block, keeping the
  Worker in the original read-only mode. Useful for read-only access to a KB
  that other people share.
- **`-ConfigPath <file>`** — injects `GXGENIE_CONFIG` into the `env` so the
  Worker uses the specified `config.json` instead of the per-cwd auto-detect.
  Useful when you maintain Security/backup settings centrally.

**Action for existing per-KB installs**: re-run `setup.ps1 -InstallToKb <kb>`
after `git pull` to regenerate the `.mcp.json` with the new default. Or hand-
edit the existing `.mcp.json` to add the `env` block. No KB data is touched.

The auto-detect-by-cwd behaviour itself in `WorkerConfig.Load` is unchanged
— it still wins over the repo's `config.json` when there's a `.gxw` in the
cwd. Changing that priority would break the per-KB isolation principle and
is intentionally out of scope.



### Added — Phase A · Full Part catalog and editability

- **Rewrite `XpzPartMap`** with 17 GeneXus object types and ~70 Parts. Each Part
  carries `(Guid, Editable, Kind)` so writers can validate before touching the
  KB. The catalogue was discovered by introspecting the `<Dependencies>` section
  that GeneXus itself emits in every XPZ export — no manual mapping, no guessing.
- **`gx_list_object_parts(type)`** — new tool. Returns the Parts known for an
  object type with `editable` flag and `kind` (`text` / `xml` / `html` /
  `structured` / `metadata`). Useful for discovering what can be modified before
  calling `gx_update_object_code`.
- **`gx_update_object_code` extended** from 1 object type (Procedure) to 15:
  Procedure, DataProvider, WebPanel, Transaction, Domain, DataSelector, DataView,
  SDT, Query, Module, Image, Theme, WebTheme, ExternalObject, Category. Each
  type has a sensible default Part (`events` for WebPanel/Transaction, `source`
  for the rest). Editability is validated up-front — non-text Parts return a
  clear error before any KB mutation.

### Added — Phase B1 · Structured reads (new `KbInspector`)

- **`gx_get_structure(name)`** — returns Transaction/SDT/DataSelector levels as
  a JSON tree, recursively. Attributes are enriched with `data_type`, `length`,
  `decimals`, `header`, `position` from the `ATTRIBUTE` table. Uses the new
  `AttributeInfo.AttriNum` field to correlate the `<Attribute Id="…">` in the
  SQL-persisted XML with attribute metadata.
- **`gx_get_layout(name)`** — returns the Web Form as a JSON tree, auto-detecting
  whether the underlying format is **KIP** (legacy HTML-like — `<body>`,
  `<gxGrid>`, `<gxTextBlock>`) or **GXML** (modern abstract layout, GX17 U11+ /
  GX18 — `<GxMultiForm>`, `<canvas>`, `<flex>`, `<grid>`, `<tab>`). Includes the
  flat list of `control_names` for quick reference.
- **`gx_get_variables(name)`** — variables as a JSON list with `data_type`
  decoded from the embedded `ATTCUSTOMTYPE` blob (codes 4 = Numeric, 5 = Character,
  6 = Date, 13 = VarChar, 15 = Boolean, 254 = SDT, 255 = ExternalObject, etc.).

### Added — Phase B2 · Granular Structure writes

- **`gx_add_attribute`** — creates a new attribute, with two modes:
  - *Standalone*: just adds the Attribute to the KB (`Import OnlyNew`).
  - *Attach*: same plus references it from a Transaction's Level
    (`Import UpdatedAndNew`). Supports `is_key`, `autonumber`, `level` (root or
    sub-level), and either explicit `data_type` (`bas:Numeric`, `bas:VarChar`,
    `bas:Date`, etc.) with `length`/`decimals`, or `based_on_domain`.
- **`gx_remove_attribute`** — removes the `<Attribute>` reference from a
  Transaction Level. The Attribute itself stays in the KB and can be used in
  other Transactions; delete it explicitly with `gx_delete_object` if you want
  it gone. Logs a `WARN` to `audit.log` when the removed attribute was a key.
- **`gx_set_attribute_property`** — patches any Property of an existing
  Attribute (`Description`, `Length`, `Decimals`, `ATTCUSTOMTYPE`, `idBasedOn`,
  `AUTONUMBER`, `ControlType`, …) via Export → patch → Import UpdatedAndNew.

### Added — Phase B3 · Granular layout writes

- **`gx_set_control_property`** — modifies an XML attribute on a control
  identified by `controlName` (case-insensitive, accepts both KIP PascalCase
  and GXML lowercase). Reuses `gx_update_object_code` as the persistence engine.
- **`gx_add_control`** — adds a new control inside a parent (identified by
  `controlName` or `id`). Generates a fresh GUID for the new element, applies
  user-supplied attributes, and validates the parent/child combination against
  a whitelist before touching the KB. **GXML only** for now; for KIP use
  `gx_update_object_code` with the full XML.
- **`gx_remove_control`** — removes a control and its descendants from the
  layout. GeneXus's BL may reject the resulting layout if it would leave a
  `<cell>` empty (cells require exactly one child); in that case the SQL
  snapshot is rolled back automatically.

### Discovered — technical findings worth recording

- **Two coexisting Web Form formats in GeneXus 17.** KIP (`<body classref="Form">…`)
  is the legacy HTML-flavoured layout — still default in templates like
  `csharp.kbtemplate` and present in older WebPanels. GXML (`<GxMultiForm>
  <Form><layout><table><row><cell>…`) is the modern abstract layout introduced
  in GX17 U11 and is the only format documented at
  [docs.genexus.com/en/wiki?46876](https://docs.genexus.com/en/wiki?46876).
  GxGenie tools auto-detect by inspecting the root element name.
- **Structure XML differs between SQL and XPZ.** The SQL-persisted Structure
  Part references attributes by `<Attribute Id="1922" IsKey="True">` (numeric
  `attri_num`). The XPZ-exported equivalent inlines the name as
  `<Attribute key="True">AttrName</Attribute>`. `KbInspector` accepts both;
  `WriteTools` consistently emits the XPZ flavour.
- **XPZ has a separate `<Attributes>` section.** Attribute definitions
  (`<Attribute>` with `<Properties>` containing `ATTCUSTOMTYPE`, `Length`,
  `idBasedOn`, …) live in a top-level `<Attributes>` section parallel to
  `<Objects>`. Transaction Structures reference them by name. This is what
  allows `gx_add_attribute` to create a new attribute and attach it to a
  Transaction atomically in a single `Import UpdatedAndNew`.
- **NULL bytes in SQL blobs.** Some Parts persisted in LocalDB contain stray
  `\0` bytes (likely zero-padding); `KbInspector.StripNullBytes` removes them
  before parsing so the XML reader doesn't fail.
- **GeneXus validates layout structure on Import.** Removing the only child of
  a `<cell>` is rejected because GXML cells require exactly one child element.
  This is canonical BL behaviour, not a GxGenie limitation — callers must
  reorganise containers (or use `gx_update_object_code` with a full edited
  layout) for transformations that pass through an inconsistent intermediate
  state.

### Validation

- **Phase A** — round-trip on `WebPanel:RwdRecentLinks` (GxGenieTest):
  read events → append marker → write → re-read finds marker → restore →
  re-read shows byte-exact match (1474 bytes).
- **Phase B1** — a real two-level Transaction returns its 2 levels and 14
  attributes correctly; a legacy KIP Web Form and `RwdRecentLinks` (GXML) are
  both detected correctly.
- **Phase B2** — synthetic `B2TestTrn` Transaction: add Numeric/VarChar/
  based-on-domain attributes, change `Description` and `Length`, remove an
  attribute, confirm the orphan Attribute is still exportable.
- **Phase B3** — `RwdRecentLinks` caption change is byte-exact restorable;
  adding a `<row>` to a `<table>` succeeds; removing a `<textblock>` from a
  `<cell>` triggers expected BL rejection with automatic rollback.

### Not changed

- `config.json` / `.mcp.json` formats are 100% backwards-compatible.
- `claude mcp add` registration is unchanged — no need to re-register.
- Existing `setup.ps1` and KB-attach behaviour are untouched.

---

## [0.1.0] — 2026-05-14 — Initial release (Phases 0–4)

Initial public release after Phases 0 through 4. See
`docs/FASE{1..4}_NOTES.md` for the detailed development history.

### Added

- **GxGenie.Worker** (.NET 8) with SQL-direct reads against the LocalDB
  that hosts each GeneXus KB: `gx_kb_info`, `gx_list_objects`,
  `gx_read_object`, `gx_search`, `gx_list_attributes`.
- **GxGenie.Gateway** (.NET 8) — MCP server speaking JSON-RPC 2.0 over stdio,
  written without external MCP libraries. Manages a long-lived Worker child
  process per session.
- **Write tools via MSBuild + `genexus.msbuild.tasks.dll`** — the same canonical
  business layer the GeneXus IDE uses, no elevation required:
  `gx_export_xpz`, `gx_import_xpz`, `gx_create_procedure`,
  `gx_update_object_code` (Procedure-only at this stage), `gx_build_object`,
  `gx_delete_object`.
- **Multi-KB support** with hot switching: `gx_list_kbs`, `gx_switch_kb`.
- **Two installation modes**: per-KB (`.mcp.json` per folder, auto-loaded by
  Claude Code) and global (single MCP at user level with central `config.json`).
- **Automatic SQL backups** (`BACKUP DATABASE` to `.bak`) before every write,
  and append-only `audit.log`.
- **`setup.ps1`** — idempotent installer with `-InstallToKb`, `-Uninstall`,
  and auto-detection of .NET 8, LocalDB, and GeneXus installations.

### Known limitations (as of 0.1.0)

- `gx_delete_object` doesn't find objects created via `gx_create_procedure`
  (XPZ template lacks `parent` / `parentType`).
- `gx_create_procedure` doesn't yet support custom variables or rules.
- GX18 supported via schema adapter but not validated end-to-end.

---

## Versioning policy

This project follows [Semantic Versioning](https://semver.org). Tool additions
that don't break existing callers are **minor** bumps; changes to tool input
schemas, `config.json` format, or installation flow are **major** bumps.

Until 1.0.0 the API surface is considered unstable — pin to a specific commit
if you need reproducibility.
