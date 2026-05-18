**English** · [Español](README.es.md)

# GxGenie — MCP Server for GeneXus 17 / 18

GxGenie is a [Model Context Protocol](https://modelcontextprotocol.io) server
that lets **Claude Code** talk to GeneXus 17 / 18 Knowledge Bases directly —
listing objects, reading source code, creating procedures, exporting/importing
XPZs, building objects — **without opening the GeneXus IDE**.

Talk to your KB in plain language:

> *"List all transactions whose name starts with Customer"*
> *"Show me the source of procedure CalcularTotal"*
> *"Create a procedure called DemoMCP that logs 'hello'"*

---

## Requirements

| Component   | Version            | Notes                                                  |
|-------------|--------------------|--------------------------------------------------------|
| Windows     | 10 / 11            | GeneXus SDK is x86                                     |
| GeneXus     | 17U1 / 17U11 / 18  | 17 validated end-to-end; 18 supported via adapter      |
| .NET SDK    | 8.0+               | Installed automatically by `setup.ps1`                 |
| LocalDB     | Bundled with SQL Server Express | KBs are persisted in LocalDB             |
| Claude Code | latest             | Run `claude --version` to check                        |

---

## Installation

The recommended mode is **Per-KB**: the MCP gets attached to each KB folder and
loads automatically when Claude Code opens it. A global mode is also available
for multi-KB scenarios with hot switching.

### Per-KB mode (recommended)

```powershell
git clone https://github.com/aocampol/GxGenie-MCP.git C:\Proyectos\GxGenie
cd C:\Proyectos\GxGenie
.\setup.ps1 -InstallToKb C:\KB\Gx17U1\SampleKB
```

A single command per KB. Re-run for every KB you want to "MCP-enable" —
`setup.ps1` is idempotent. It will:

1. Verify .NET 8 SDK (installs via `winget` if missing).
2. Build Worker + Gateway in Release mode.
3. Drop a `.mcp.json` file in the KB folder, pointing at the compiled Gateway.

Then to use it:

```powershell
cd C:\KB\Gx17U1\SampleKB
claude
```

The first time Claude Code starts in that folder, it will ask you to approve
the `genexus` MCP server. Say yes — the decision is remembered per folder.

**Enabling writes / builds** (off by default for safety):

```powershell
$env:GXGENIE_ALLOW_WRITE = "true"
$env:GXGENIE_ALLOW_BUILD = "true"
claude
```

### Global mode (multi-KB)

Registers a single MCP at the user level and uses a central `config.json`
listing all your KBs. Required if you want to use `gx_switch_kb` to hop
between KBs without restarting Claude Code.

```powershell
.\setup.ps1
```

This scans `C:\KB`, `D:\KB`, `C:\GeneXus\KB` for `.gxw` files, builds a
multi-KB `config.json`, and registers the MCP globally via `claude mcp add`.

### Uninstall

```powershell
.\setup.ps1 -Uninstall
```

Removes the global MCP registration, deletes `bin/` and `obj/`, and clears
environment variables. **Does not** touch `config.json`, `.mcp.json` files
already deployed to KB folders, `audit.log`, or `backups/` — those are
yours to manage.

---

## Updating

If you already cloned the repo and want to pull newer versions, the
fast path is the bundled script:

```powershell
cd C:\Proyectos\GxGenie
.\update.ps1
```

`update.ps1` aborts if any `GxGenie.Worker.exe` / `GxGenie.Gateway.exe`
is still running (typically because Claude Code has the MCP server
attached), then does `git pull` + `dotnet build` for both projects.

**Manual equivalent** (if you prefer to run each step):

```powershell
# 1) Close every Claude Code session that has the MCP server loaded —
#    otherwise the .exe is locked and the build fails. Verify with:
tasklist /FI "IMAGENAME eq GxGenie.Gateway.exe"

# 2) Pull the new commits
git -C C:\Proyectos\GxGenie pull origin main

# 3) Rebuild Worker and Gateway
dotnet build C:\Proyectos\GxGenie\GxGenie.Worker\GxGenie.Worker.csproj  -c Release
dotnet build C:\Proyectos\GxGenie\GxGenie.Gateway\GxGenie.Gateway.csproj -c Release

# 4) Reopen Claude Code — the next tool call relaunches the Gateway with the new binary.
```

Check **[CHANGELOG.md](CHANGELOG.md)** for what every release introduces,
removes, or breaks. The MCP registration (`claude mcp add`) and the
`config.json` / `.mcp.json` files do not need to be re-created across
versions unless the changelog explicitly says so.

If `dotnet build` complains with `error MSB3027` about a file in use,
some Claude Code window still has the Gateway open. Close it and retry,
or as a last resort:

```powershell
taskkill /F /IM GxGenie.Worker.exe /IM GxGenie.Gateway.exe
```

---

## Usage

You don't invoke tools with slash commands — talk to Claude in plain language
and it picks the right tool. See **[USAGE.md](USAGE.md)** for the full guide.
A few quick examples:

### Inspect the KB
> **You:** What objects are in the current Knowledge Base?
> **Claude:** *[calls `gx_kb_info`]* SampleKB (GX17), 138,975 entities. Procedures: 4169, WebPanels: 2373, Transactions: 621, SDTs: 1245...

### Read an object's source
> **You:** Show me the source of procedure CalcularTotal.
> **Claude:** *[calls `gx_read_object` with `name="CalcularTotal"`]*
> ```
> for each Customer
>     &total += CustomerBalance
> endfor
> ```

### Search code for an attribute
> **You:** Where is the attribute ClienteId used in code?
> **Claude:** *[calls `gx_search` with `query="ClienteId"`, `search_in="code"`]* Found in 47 objects: ...

### Create a procedure
> **You:** Create a procedure called DemoMCP that writes "hello" to the log.
> **Claude:** *[calls `gx_create_procedure`]* Procedure created. Backup at `backups\SampleKB\20260514_104530\GX_KB_SampleKB__create_proc_DemoMCP.bak`.

### Work with two KBs in the same session (global mode only)
> **You:** List my KBs.
> **Claude:** *[calls `gx_list_kbs`]* SampleKB (active), SampleKB2, SampleKB4 — all GX17.
>
> **You:** Switch to SampleKB2 and tell me how many procedures it has.
> **Claude:** *[calls `gx_switch_kb`, then `gx_list_objects type=Procedure`]* SampleKB2 has 76 procedures.

---

## Available tools (25)

### Basic reads (direct SQL)

| Tool                 | Description                                                            |
|----------------------|------------------------------------------------------------------------|
| `gx_kb_info`         | KB version, object counts per type, active KB, GeneXus version        |
| `gx_list_objects`    | List objects by type with name filter                                  |
| `gx_read_object`     | Decoded source of every Part (events, rules, body, structure, …)      |
| `gx_search`          | Search by name (fast) or by code content (slow but thorough)          |
| `gx_list_attributes` | List a Transaction's attributes with type, length, PK status          |
| `gx_list_object_parts` | List the Parts known for an object type, with editable flag and kind |

### Structured reads (parsed JSON)

| Tool                 | Description                                                            |
|----------------------|------------------------------------------------------------------------|
| `gx_get_structure`   | Transaction/SDT/DataSelector structure as nested JSON levels           |
| `gx_get_layout`      | Web Form as JSON tree, auto-detecting KIP (legacy) vs GXML (modern)    |
| `gx_get_variables`   | Variables of any object with `data_type` decoded from `AttCustomType`  |

### Writes — objects and source code (MSBuild + GeneXus tasks)

| Tool                    | Notes                                                                |
|-------------------------|----------------------------------------------------------------------|
| `gx_export_xpz`         | Export object(s) to an `.xpz` file                                   |
| `gx_import_xpz`         | Import an `.xpz`. Auto SQL backup before                             |
| `gx_create_procedure`   | Create a new Procedure (minimal XPZ generated in memory + import)    |
| `gx_update_object_code` | Update a Part's source/text for 15 object types (Procedure, WebPanel, Transaction, DataProvider, Domain, SDT, …). Validates editability per Part. |
| `gx_build_object`       | Specify + generate an object (requires `AllowBuild=true`)            |
| `gx_delete_object`      | Delete an object. Auto SQL backup before                             |

### Writes — Transaction Structure (granular)

| Tool                       | Notes                                                              |
|----------------------------|--------------------------------------------------------------------|
| `gx_add_attribute`         | Create attribute and optionally attach it to a Transaction Level. Supports `data_type` (`bas:Numeric`, `bas:VarChar`, …) or `based_on_domain`. |
| `gx_remove_attribute`      | Remove the attribute reference from a Transaction Level. The Attribute stays in the KB. |
| `gx_set_attribute_property`| Patch any Property of an existing Attribute (`Description`, `Length`, `Decimals`, `ATTCUSTOMTYPE`, `idBasedOn`, `AUTONUMBER`, …) |

### Writes — Web Form layout (granular, mostly GXML)

| Tool                      | Notes                                                               |
|---------------------------|---------------------------------------------------------------------|
| `gx_set_control_property` | Modify an XML attribute on a control identified by `controlName`    |
| `gx_add_control`          | Add a new control inside a parent (by `controlName` or `id`). Whitelist-validated; GXML only. |
| `gx_remove_control`       | Remove a control and its descendants. GeneXus BL may reject if the result is invalid (e.g. empty `<cell>`); SQL snapshot rollback is automatic. |

### Multi-KB

| Tool            | Description                                                              |
|-----------------|--------------------------------------------------------------------------|
| `gx_list_kbs`   | List KBs from `config.json` and indicate which is active                 |
| `gx_switch_kb`  | Hot-swap the active KB without restarting Claude Code                    |

> Writing tools require `Security.AllowWrite=true` (and `AllowBuild=true` for
> `gx_build_object`) in `config.json`. Off by default — you have to opt in.
> Every destructive operation snapshots the KB's LocalDB to a `.bak` under
> `backups/` first, so any failed Import is restorable with `RESTORE DATABASE`.

---

## Architecture

```
Claude Code (Anthropic)
    │ stdio — MCP Protocol (JSON-RPC 2.0)
    ▼
GxGenie.Gateway      ← .NET 8 — speaks MCP with Claude Code
    │ stdin/stdout JSON (Worker as child process, long-lived)
    ▼
GxGenie.Worker       ← .NET 8 — dispatcher for 25 tools, multi-KB
    │
    ├── Direct SQL (reads)              → LocalDB hosting the KB
    └── MSBuild + Genexus.Tasks.targets → the same canonical business
        (writes)                          layer the GeneXus IDE uses
```

Key design decisions:

- **No GeneXus DLLs loaded in-process.** The Worker stays in pure .NET 8 and
  delegates every mutating operation to `msbuild.exe`, which loads the
  official `genexus.msbuild.tasks.dll`. No elevation required.
- **Automatic SQL backup before every write.** A `BACKUP DATABASE` snapshot
  goes under `backups/{kb}/{timestamp}/` before any destructive operation,
  restorable with `RESTORE DATABASE … WITH REPLACE`.
- **Append-only audit log** at `audit.log` for every destructive operation.

---

## Project structure

```
GxGenie/
├── GxGenie.Gateway/             ← MCP server (.NET 8) — JSON-RPC over stdio
├── GxGenie.Worker/              ← KB logic (.NET 8) — SQL reads + MSBuild writes
├── setup.ps1                    ← Idempotent installer (per-KB and global modes)
├── config.multi.example.json    ← Example multi-KB config
├── config.example.json          ← Example single-KB config (legacy)
├── update.ps1                   ← Pull + rebuild script for existing installs
├── CHANGELOG.md                 ← Per-release notes
├── README.md / USAGE.md         ← Documentation
└── LICENSE                      ← MIT
```

---

## GeneXus version support

| GeneXus version | Reads (SQL)         | Writes (MSBuild)         | Status            |
|-----------------|---------------------|--------------------------|-------------------|
| 17U1            | Validated E2E       | Validated E2E            | Production-ready  |
| 17U11           | Validated on SampleKB2| Same schema as 17U1      | Production-ready  |
| 18              | Adapter ready       | Depends on Genexus.Tasks | Not validated yet |

The GX18 adapter introspects `INFORMATION_SCHEMA` to handle the historical
typo `KnowlegeBaseVersion` vs `KnowledgeBaseVersion`, and otherwise assumes
schema parity with GX17 until validated against a real GX18 KB.

---

## Known limitations

1. **`gx_delete_object` doesn't find objects created via `gx_create_procedure`.**
   MSBuild returns "Procedure X was not found in the KB" even though the row
   exists. Workaround: delete from the GeneXus IDE.
2. **`gx_create_procedure` doesn't yet support variables or custom rules** —
   the XPZ template leaves them empty. Workaround: create the procedure, then
   edit parts with a follow-up XPZ.
3. **`gx_build_object`** requires an Environment configured in the KB. A
   freshly-created KB with no active generator may produce empty output.
4. **GX18 not validated end-to-end.** The schema adapter is ready, but the
   type GUIDs used by `gx_create_procedure` come from a GX17 export and
   should be stable, but haven't been confirmed.
5. **Switching KBs**: after `gx_switch_kb`, MSBuild may detach the previous
   DB from LocalDB. `LocalDbAttacher.EnsureAttached` re-attaches on the next
   raw SQL call — transparent to the user, but the first tool call after a
   switch may have a 1–2s extra latency.

---

## License

[MIT](LICENSE) — use it freely, modify it, redistribute it.

This repo **does not include any GeneXus binaries**. The MSBuild tasks DLL
and GeneXus environment must come from a licensed installation of GeneXus
17 or 18. GeneXus is a commercial product of GeneXus S.A.
