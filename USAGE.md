**English** · [Español](USAGE.es.md)

# Using GxGenie from Claude Code

A practical guide to invoking the MCP tools **once installed** (see
[README.md](README.md) for installation).

---

## The mental model

**You don't invoke tools with slash commands.** You talk to Claude in plain
language and Claude picks which MCP tool to call based on what you asked.

```
cd C:\KB\Gx17U1\SampleKB
claude
```

On startup, Claude detects the `.mcp.json` and the first time asks if you
trust it. Say yes — the decision is remembered for that folder.

Inside the session, `/mcp` shows the loaded servers and their tools.

---

## The 13 tools at a glance

### Reading (no permissions required, Claude uses these freely)

| Tool | What it does | Typical prompt |
|------|--------------|----------------|
| `gx_kb_info` | Active KB, version, object counts by type | *"What KB do I have loaded?"* |
| `gx_list_objects` | List objects by type, with filter | *"List all procedures whose name starts with `Calc`"* |
| `gx_read_object` | Return the decoded source | *"Show me the code of `CalcularTotal`"* |
| `gx_search` | Search by name or inside source code | *"Where is the attribute `ClienteId` used?"* |
| `gx_list_attributes` | Attributes of a Transaction | *"What attributes does the `Cliente` transaction have?"* |
| `gx_list_kbs` | List configured KBs (global mode) | *"What KBs do I have available?"* |

### Writing (require `AllowWrite=true`; Claude also asks for confirmation)

| Tool | What it does | Typical prompt |
|------|--------------|----------------|
| `gx_export_xpz` | Export to `.xpz` | *"Export `CalcularTotal` to `C:\temp\x.xpz`"* |
| `gx_import_xpz` | Import an `.xpz` (auto-backup first) | *"Import `C:\temp\x.xpz`"* |
| `gx_create_procedure` | Create a new Procedure | *"Create a procedure `Demo` with `msg('hello')`"* |
| `gx_update_object_code` | **Replace the source of one of an object's Parts** (export → modify XPZ → import UpdatedAndNew) | *"Optimize the code of `CalcularTotal` like this: ..."* |
| `gx_delete_object` | Delete an object (auto-backup) | *"Delete the procedure `Obsoleto`"* |
| `gx_build_object` | Specify + generate (requires `AllowBuild`) | *"Build `CalcularTotal`"* |
| `gx_switch_kb` | Switch active KB (global mode) | *"Switch to KB `SampleKB2`"* |

---

## Real workflows

### 1) Audit usage of an attribute before a refactor

```
You:     Before renaming `ClienteId` to `CustomerId`, tell me every
         object that uses it in its source code.

Claude:  [gx_search query="ClienteId" search_in="code"]
         Found 47 hits across 23 objects. The most important ones:
         - Procedure CalcularDeuda (5 references)
         - WebPanel ConsultaClientes (3 references)
         - DataProvider DPClientes (2 references)
         ...
```

### 2) Generate documentation for a procedure

```
You:     Read procedure `CalcularDeuda` and document what it does in plain
         English, what parameters it takes, and what side effects it has.

Claude:  [gx_read_object name="CalcularDeuda" type="Procedure"]
         [Returns source, rules, conditions]

         CalcularDeuda does the following:
         1. Receives ClienteId as input parameter
         2. Sums the customer's pending invoices
         3. ...
         Side effects: updates the DEUDA_HISTORICO table.
```

### 3) Create a Procedure from a description

```
You:     Create a procedure called `DemoMCP` that takes a
         ClienteId and msg()s the customer's name.

Claude:  [gx_create_procedure name="DemoMCP"
          source="parm(in:&ClienteId);
                  for each Cliente where ClienteId=&ClienteId
                      msg(ClienteNombre)
                  endfor"]
         Procedure created. Backup at
         .gxmcp\backups\SampleKB\20260514_153012\..._create_proc_DemoMCP.bak
```

### 4) Optimize a Procedure and apply

```
You:     Read `CalcularTotal`, tell me if it can be optimized, and if so,
         apply the change directly.

Claude:  [gx_read_object name="CalcularTotal"]
         I see two optimizations:
         1. The nested `for each` can be merged into a join — saves
            ~80% of fetches in data-heavy KBs.
         2. The `&Total > 0` validation is inside the loop but could
            be lifted out.

         Apply the changes? (I'll show you the diff first)

You:     Yes, go ahead.

Claude:  [gx_update_object_code type="Procedure" name="CalcularTotal"
          new_source="<optimized code>"]
         Applied. Backup at
         .gxmcp\backups\...update_Procedure_CalcularTotal_source.bak.
         To revert: RESTORE DATABASE FROM DISK='...' WITH REPLACE.
```

**Important**: today `gx_update_object_code` only supports `Procedure`
(parts `source` / `rules` / `conditions`). Support for WebPanel Events,
Transaction Rules, etc. is on the roadmap.

### 5) Build what you changed

```
You:     I just imported an XPZ with changes to `CalcularDeuda`. Build
         it and tell me if there are errors.

Claude:  [gx_build_object object_name="CalcularDeuda"]
         Build OK. Spec + Gen completed with no errors. Log:
         ...
```

### 6) Work with multiple KBs (global mode)

```
You:     What KBs do I have available?
Claude:  [gx_list_kbs]
         3 KBs: SampleKB (active, GX17), SampleKB2 (GX17), SampleKB4 (GX17)

You:     Switch to SampleKB2 and tell me how many web panels it has.
Claude:  [gx_switch_kb kb_name="SampleKB2"] [gx_list_objects type="WebPanel"]
         SampleKB2 loaded. It has 40 WebPanels.
```

---

## Permissions at runtime

There are **two layers** of safety:

1. **Claude Code** asks you before running any write tool
   (`Allow once` / `Always allow` / `Deny`). While you're learning,
   `Allow once` is the safest choice.
2. **The Worker itself** checks authorization before touching the KB:
   - Write tools require `AllowWrite=true`
   - `gx_build_object` requires `AllowBuild=true`

If not enabled, the Worker returns an error without touching the KB
(*"Refusing: gx_create_procedure — Security.AllowWrite=false"*).

### How to enable writes

**Per-session (recommended for starting out):**
```powershell
cd C:\KB\<your-kb>
$env:GXGENIE_ALLOW_WRITE = "true"
$env:GXGENIE_ALLOW_BUILD = "true"
claude
```
When you close that terminal, the env vars are gone.

**Persistent for that KB:** drop a `config.json` in the KB folder:
```json
{
  "Security": {
    "AllowWrite": true,
    "AllowBuild": true,
    "AuditLog": true
  }
}
```
The Worker merges this with the auto-detect — the KB stays the one from cwd,
but the flags win.

---

## Backup and audit

**Every write generates a `.bak` snapshot first** under:
```
<KB-folder>\.gxmcp\backups\<timestamp>\<dbname>__<tool>.bak
```
To revert: `RESTORE DATABASE <dbname> FROM DISK='<path>.bak' WITH REPLACE`.

**Every operation is logged** to `<KB-folder>\.gxmcp\audit.log`:
```
2026-05-14 15:30:12 | WRITE   | gx_create_procedure   | DemoMCP  | SUCCESS | backup=...
```

---

## Troubleshooting

### "The MCP doesn't show up in `/mcp`"

1. Verify the `.mcp.json` exists: `Get-Content .\.mcp.json`
2. Verify the `command` path points to an `.exe` that exists
3. Check the Gateway's stderr: `claude --debug`
4. Try invoking the Gateway directly:
   ```
   C:\Proyectos\GxGenie\GxGenie.Gateway\bin\Release\net8.0\GxGenie.Gateway.exe --help
   ```

### "Tool failed: KB not found / no .gxw"

The Worker didn't detect the KB. Possible causes:
- No `.gxw` in the current folder
- Claude was opened from another folder (check with: *"What's my cwd?"*)
- Multiple `.gxw` files in the folder — the Worker uses the first one, move the others

### "Cannot open database … login failed"

LocalDB got detached (MSBuild does this after every `CloseKnowledgeBase`).
The Worker should re-attach automatically on the next raw SQL call, but
if it persists:
```powershell
sqllocaldb start MSSQLLocalDB
```

### "Worker timeout"

Default is 120s. For long builds, raise it with a local `config.json`:
`"Worker": { "TimeoutSeconds": 600 }`.

---

## What this MCP does NOT do (yet)

- **`gx_update_object_code` covers Procedure only** — supports the `source`,
  `rules`, and `conditions` parts of Procedure. WebPanel events, Transaction
  rules, DataProvider source, etc. are on the roadmap.
- **Web panel layouts** — reading the WebForm XML works; modifying layouts
  is not implemented.
- **Creating Transactions / adding attributes** — on the roadmap.
- **GX18 validation** — the adapter is ready, but without a real GX18 install
  on hand it hasn't been validated end-to-end.
- **Team Server / GXserver operations** — out of scope.
