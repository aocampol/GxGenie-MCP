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

## Available tools (13)

### Reading (via direct SQL)

| Tool                | Description                                                              |
|---------------------|--------------------------------------------------------------------------|
| `gx_kb_info`        | KB version, object counts per type, active KB, GeneXus version          |
| `gx_list_objects`   | List objects by type with name filter                                    |
| `gx_read_object`    | Decoded source code (events, rules, body, structure…)                   |
| `gx_search`         | Search by name (fast) or by code content (slow but thorough)            |
| `gx_list_attributes`| List attributes of a Transaction with type, length, PK status            |

### Writing (via MSBuild + GeneXus tasks)

| Tool                    | Notes                                                                 |
|-------------------------|-----------------------------------------------------------------------|
| `gx_export_xpz`         | Export object(s) to an `.xpz` file                                    |
| `gx_import_xpz`         | Import an `.xpz`. Auto SQL backup before                              |
| `gx_create_procedure`   | Create a new Procedure (minimal XPZ generated in memory + import)     |
| `gx_update_object_code` | Update an object's source (currently Procedure only)                   |
| `gx_build_object`       | Specify + generate an object (requires `AllowBuild=true`)             |
| `gx_delete_object`      | Delete an object. Auto SQL backup before                              |

### Multi-KB

| Tool            | Description                                                            |
|-----------------|------------------------------------------------------------------------|
| `gx_list_kbs`   | List KBs from `config.json` and indicate which is active               |
| `gx_switch_kb`  | Hot-swap the active KB without restarting Claude Code                  |

> Writing tools require `Security.AllowWrite=true` (and `AllowBuild=true` for
> `gx_build_object`) in `config.json`. Off by default — you have to opt in.

---

## Architecture

```
Claude Code (Anthropic)
    │ stdio — MCP Protocol (JSON-RPC 2.0)
    ▼
GxGenie.Gateway      ← .NET 8 — speaks MCP with Claude Code
    │ stdin/stdout JSON (Worker as child process, long-lived)
    ▼
GxGenie.Worker       ← .NET 8 — dispatcher for 13 tools, multi-KB
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
