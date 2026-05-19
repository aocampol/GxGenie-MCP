# Changelog

All notable changes to GxGenie are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased] — Phase A + B1 + B2 + B3 (plus per-KB write-enabled fix)

12 new MCP tools, no breaking changes to `config.json` format. The per-KB
install (`setup.ps1 -InstallToKb`) now ships writes/builds enabled by default
(see below).

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
- **Phase B1** — `DemoTransaction` Transaction (SampleKB) returns 2 levels and 14
  attributes correctly; `DemoWebPanel2` (KIP) and `RwdRecentLinks` (GXML)
  detected correctly.
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
