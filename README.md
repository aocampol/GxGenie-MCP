# GxGenie — MCP Server para GeneXus 17 / 18

MCP Server (Model Context Protocol) que permite a **Claude Code** interactuar
directamente con Knowledge Bases de GeneXus 17 y 18 — leer objetos, crear,
modificar y compilar — sin necesidad de tener el IDE de GeneXus abierto.

---

## Requisitos

| Componente | Versión | Notas |
|------------|---------|-------|
| Windows    | 10 / 11 | El SDK de GeneXus es x86 |
| GeneXus    | 17U1 / 17U11 / 18 | 17 validado E2E; 18 soportado a nivel adapter (ver "Estado") |
| .NET SDK   | 8.0+    | Se instala automáticamente vía `setup.ps1` |
| LocalDB    | (incluido con SQL Server Express) | Las KBs persisten en LocalDB |
| Claude Code | última  | `claude --version` para verificar |

---

## Instalación rápida

### Paso 0 — Clonar el repo

```powershell
git clone https://github.com/<tu-usuario>/GxGenie.git C:\Proyectos\GxGenie
cd C:\Proyectos\GxGenie
```

(O descargá el .zip desde "Code → Download ZIP" si preferís).

Después tenés **dos modos de instalación**. El recomendado es el modo Per-KB:
el MCP se ata a la carpeta de cada KB y se carga sólo cuando Claude Code se
abre ahí. El alternativo (global) registra el MCP a nivel usuario y usa un
`config.json` central — útil si querés `gx_switch_kb` o paths no-estándar.

### Opción A — Modo Per-KB (recomendado)

Dropea un `.mcp.json` en la carpeta de cada KB que querés "MCP-habilitar":

```powershell
.\setup.ps1 -InstallToKb C:\KB\Gx17U1\SampleKB
.\setup.ps1 -InstallToKb C:\KB\Gx17U11\SampleKB2
# ... una por KB; setup.ps1 es idempotente, podés repetir
```

Cada invocación: detecta .NET 8 (lo instala si falta), compila Worker+Gateway
(si no están), y crea `.mcp.json` en la carpeta. Para usar el MCP después:

```powershell
cd C:\KB\Gx17U1\SampleKB
claude
```

Claude Code lee el `.mcp.json` automáticamente al abrir y carga el MCP
`genexus` auto-bindeado a esa KB. **No hace falta `config.json`** — el
Worker detecta la KB leyendo el `.gxw` + `knowledgebase.connection`
adyacente.

Para habilitar escritura/build en esa KB:
```powershell
$env:GXGENIE_ALLOW_WRITE = "true"      # antes de lanzar claude
$env:GXGENIE_ALLOW_BUILD = "true"
claude
```
O bien dropeá un `config.json` local en la carpeta de la KB con `Security.AllowWrite=true`.

### Opción B — Modo global (legacy / multi-KB)

Registra un MCP único a nivel usuario que usa un `config.json` central:

```powershell
.\setup.ps1
```

Esto:
1. Detecta `.NET 8 SDK` (lo instala vía `winget` si falta).
2. Detecta una o más instalaciones de GeneXus (17U1, 17U11, 18) en rutas estándar.
3. Detecta `MSBuild .NET 4.x`, LocalDB y Claude Code CLI.
4. Compila Worker y Gateway en `Release`.
5. Escanea `C:\KB`, `D:\KB`, `C:\GeneXus\KB` y arma `config.json` (multi-KB)
   con las KBs encontradas. Por defecto deja `Security.AllowWrite=false`.
6. Registra el MCP `genexus` en Claude Code (`claude mcp add`).
7. Corre un smoke test (`gx_kb_info`).

Ventaja del modo global: podés usar `gx_switch_kb` para cambiar entre KBs
en la misma sesión de Claude.

### Revertir

```powershell
.\setup.ps1 -Uninstall
```
Desregistra el MCP global, borra `bin/` y `obj/`, y limpia env vars.
**No** toca `config.json`, `.mcp.json` ya instalados, `audit.log` ni `backups/`
— eso lo decidís vos.

### Post-instalación

- En **modo Per-KB**: abrí Claude en una carpeta de KB y probá *"¿Qué KB
  tengo cargada?"* (invoca `gx_kb_info`).
- En **modo global**: editá `config.json` y poné `Security.AllowWrite = true`
  si querés tools de escritura. Después `/mcp` en Claude debería listar `genexus`.

---

## Arquitectura

```
Claude Code (Anthropic)
    │ stdio — MCP Protocol (JSON-RPC 2.0)
    ▼
GxGenie.Gateway       ← .NET 8 — habla MCP con Claude Code
    │ stdin/stdout JSON (Worker como proceso hijo, long-lived)
    ▼
GxGenie.Worker        ← .NET 8 — dispatcher de 13 tools, multi-KB
    │
    ├── SQL directo (lecturas, vía IKbSchemaAdapter)  → LocalDB de la KB
    └── MSBuild + Genexus.Tasks.targets (escrituras)   → la BL canónica
                                                          que usa el IDE
```

Decisiones clave:
- **Sin DLLs de GeneXus cargadas en proceso**: el Worker queda en .NET 8 puro
  y delega a `msbuild.exe` (que sí carga `genexus.msbuild.tasks.dll`) para
  toda operación que muta la KB. No requiere elevación.
- **Backup automático antes de cada escritura**: snapshot SQL (`BACKUP DATABASE`)
  bajo `backups/{kb}/{timestamp}/`. Restaurable con `RESTORE DATABASE … WITH REPLACE`.
- **Audit log append-only** en `audit.log` para cada operación destructiva.
- **Schema adapter** (`IKbSchemaAdapter`) encapsula las pocas variaciones entre
  GX17 y GX18 — la mayoría del schema SQL es estable entre versiones.

---

## MCP Tools disponibles (12)

### Lectura (vía SQL directo)

| Tool | Descripción | Parámetros principales |
|------|-------------|------------------------|
| `gx_kb_info` | Versión KB, conteo por tipo, modelos, KB activa, versión de GeneXus | — |
| `gx_list_objects` | Listar objetos por tipo + filtro de nombre | `type`, `filter`, `limit` |
| `gx_read_object` | Código fuente decodificado (events, rules, source body, structure…) | `name`, `type?` |
| `gx_search` | Búsqueda por nombre (rápido) o por código (lento) | `query`, `search_in`, `limit` |
| `gx_list_attributes` | Atributos de una Transaction con tipo, longitud, PK | `transaction` |

### Escritura (vía MSBuild + tasks GeneXus)

| Tool | Task MSBuild | Notas |
|------|--------------|-------|
| `gx_export_xpz` | `Export` | Genera .xpz; sólo lectura sobre la KB |
| `gx_import_xpz` | `Import` | Backup SQL automático antes |
| `gx_create_procedure` | `Import` + XPZ generado | XPZ mínimo en memoria, luego import |
| `gx_update_object_code` | `Export` + modify XPZ + `Import UpdatedAndNew` | Actualiza source de un Part. Hoy sólo Procedure (source/rules/conditions) |
| `gx_build_object` | `BuildOne` | Especifica + genera (requiere `AllowBuild=true`) |
| `gx_delete_object` | `DeleteObject` | Backup automático antes (ver Limitaciones) |

### Multi-KB (Fase 4)

| Tool | Descripción |
|------|-------------|
| `gx_list_kbs` | Lista las KBs definidas en config.json e indica cuál está activa |
| `gx_switch_kb` | Cambia la KB activa en caliente (sin reiniciar Claude Code) |

Todas las tools de escritura requieren `Security.AllowWrite = true` en
`config.json` (y `AllowBuild = true` para `gx_build_object`). Por defecto
están deshabilitadas — el setup deja `AllowWrite=false` y necesitás
editarlas manualmente para autorizar mutaciones.

---

## Ejemplos de uso en Claude Code

### 1) Inspeccionar la KB
```
Tú: ¿Qué objetos hay en la Knowledge Base actual?
Claude: [llama a gx_kb_info]
        SampleKB (GX17), 138975 entidades.
        Procedures: 4169, WebPanels: 2373, Transactions: 621, SDTs: 1245...
```

### 2) Leer código fuente de un objeto
```
Tú: Mostrame el source del procedure "CalcularTotal".
Claude: [llama a gx_read_object con name="CalcularTotal"]
        // Source de CalcularTotal:
        for each ...
```

### 3) Buscar usos de un atributo en el código
```
Tú: ¿Dónde se usa el atributo ClienteId en el código?
Claude: [llama a gx_search con query="ClienteId" y search_in="code"]
        Lo encontré en 47 objetos: ...
```

### 4) Crear un procedimiento nuevo
```
Tú: Creá un procedure llamado "DemoMCP" que escriba "hola" en el log.
Claude: [llama a gx_create_procedure con name="DemoMCP" y source="msg('hola')"]
        Procedure creado. Backup en backups\SampleKB\20260514_104530\GX_KB_SampleKB__create_proc_DemoMCP.bak
```

### 5) Trabajar con dos KBs en la misma sesión
```
Tú: Listame las KBs disponibles.
Claude: [llama a gx_list_kbs]
        SampleKB (activa), SampleKB2, SampleKB4 — todas GX17.

Tú: Cambiá a SampleKB2 y decime cuántos procedures tiene.
Claude: [llama a gx_switch_kb kb_name=SampleKB2] [luego gx_list_objects type=Procedure]
        Switch OK. SampleKB2 tiene 76 Procedures.
```

### 6) Exportar un objeto a XPZ
```
Tú: Exportá el procedure "DemoMCP" a C:\temp\demo.xpz
Claude: [llama a gx_export_xpz objects=["Procedure:DemoMCP"] output_path="C:\temp\demo.xpz"]
        Exportado: 1420 bytes en C:\temp\demo.xpz
```

---

## Configuración (config.json)

GxGenie soporta dos formatos — uno legacy de Fase 1–3 (single-KB) y el de
Fase 4 (multi-KB). Ambos funcionan; el setup.ps1 genera el formato
multi-KB.

### Formato multi-KB (recomendado, Fase 4)

```json
{
  "GeneXus": [
    {
      "Version": "17",
      "InstallationPath": "C:\\Program Files (x86)\\GeneXus\\GeneXus17U1",
      "SdkPath": "C:\\GxSDK17",
      "MSBuildPath": "C:\\Windows\\Microsoft.NET\\Framework\\v4.0.30319\\MSBuild.exe"
    },
    {
      "Version": "18",
      "InstallationPath": "C:\\Program Files (x86)\\GeneXus\\GeneXus18",
      "SdkPath": "C:\\GxSDK18",
      "MSBuildPath": "C:\\Windows\\Microsoft.NET\\Framework\\v4.0.30319\\MSBuild.exe"
    }
  ],
  "KnowledgeBases": [
    {
      "Name": "SampleKB",
      "Path": "C:\\KB\\Gx17U1\\SampleKB\\SampleKB.gxw",
      "ConnectionString": "Server=(LocalDB)\\MSSQLLocalDB;Database=GX_KB_SampleKB;Integrated Security=True;TrustServerCertificate=True",
      "GeneXusVersion": "17"
    },
    {
      "Name": "SampleKB2",
      "Path": "C:\\KB\\Gx17U11\\SampleKB2\\SampleKB2.gxw",
      "ConnectionString": "Server=(LocalDB)\\MSSQLLocalDB;Database=GX_KB_SampleKB2;Integrated Security=True;TrustServerCertificate=True",
      "GeneXusVersion": "17"
    }
  ],
  "ActiveKB": "SampleKB",
  "Worker": {
    "ExecutablePath": "C:\\Proyectos\\GxGenie\\GxGenie.Worker\\bin\\Release\\net8.0\\GxGenie.Worker.exe",
    "TimeoutSeconds": 120
  },
  "Security": {
    "AllowWrite": false,
    "AllowBuild": false,
    "AuditLog": true,
    "AuditLogPath": "C:\\Proyectos\\GxGenie\\audit.log",
    "BackupRoot": "C:\\Proyectos\\GxGenie\\backups"
  }
}
```

Ver `config.multi.example.json` para una versión real.

### Formato legacy (single-KB)

Sigue funcionando — el Worker detecta automáticamente cuál se está usando.
Ver `config.json` actual del repo como ejemplo.

---

## Cómo compilar manualmente

```powershell
dotnet build C:\Proyectos\GxGenie\GxGenie.Worker\GxGenie.Worker.csproj -c Release
dotnet build C:\Proyectos\GxGenie\GxGenie.Gateway\GxGenie.Gateway.csproj -c Release
```

## Cómo registrar manualmente en Claude Code

```powershell
claude mcp add --transport stdio genexus -- `
  "C:\Proyectos\GxGenie\GxGenie.Gateway\bin\Release\net8.0\GxGenie.Gateway.exe"
claude mcp list
```

---

## Estado del soporte por versión

| Versión GeneXus | Lectura (SQL) | Escritura (MSBuild) | Estado |
|-----------------|---------------|---------------------|--------|
| 17U1            | ✓ validado E2E | ✓ validado E2E | Production-ready |
| 17U11           | ✓ validado en SampleKB2 | ✓ (mismo schema que 17U1) | Production-ready |
| 18              | ⚠ adapter conservador | ⚠ depende de Genexus.Tasks.targets | Ver [docs/FASE4_NOTES.md](docs/FASE4_NOTES.md) |

El adapter de GX18 (`Gx18SchemaAdapter`) es defensivo: prueba el nombre de
la columna de versión con introspección de `INFORMATION_SCHEMA` por si
GeneXus corrigió el typo histórico `KnowlegeBaseVersion` → `KnowledgeBaseVersion`.
El resto del schema asume paridad con GX17 hasta que se valide en una KB GX18
real.

---

## Limitaciones conocidas

1. **`gx_delete_object` no encuentra objetos creados vía `gx_create_procedure`** —
   MSBuild devuelve "Procedure X was not found in the KB" aunque la fila exista
   en `EntityVersion`. Hipótesis: falta `parent`/`parentType` en el XPZ
   generado. Borrar manualmente desde el IDE. Tracking en [docs/FASE3_NOTES.md](docs/FASE3_NOTES.md).

2. **`gx_create_procedure` no soporta variables ni rules personalizados** —
   el template XPZ los crea vacíos. Workaround: crear el procedure y luego
   editar las parts con un XPZ adicional (TODO Fase 3.5).

3. **`gx_build_object`** requiere un Environment configurado en la KB. En
   una KB recién creada sin generator activo puede producir output vacío.

4. **GX18 no validado E2E** — sólo el adapter está preparado; los GUIDs
   de tipo (Procedure, Source, Rules, …) usados por `gx_create_procedure`
   provienen de un export de GX17 y deberían ser estables, pero no probado.

5. **Switch de KB en MCP**: tras `gx_switch_kb` la sesión usa la nueva KB
   inmediatamente, pero MSBuild puede detachar la DB anterior del LocalDB.
   `LocalDbAttacher.EnsureAttached` la reatacha al próximo SQL crudo —
   transparente para el usuario, pero la primera tool tras un switch puede
   tardar 1-2s extras.

---

## Estructura del proyecto

```
C:\Proyectos\GxGenie\
├── CLAUDE.md                    ← Contexto para Claude Code
├── README.md                    ← Este archivo
├── docs\                        ← FASE{1..4}_NOTES.md + PROMPT_FASE{1..4}.md
├── setup.ps1                    ← Instalador idempotente
├── config.json                  ← Config principal
├── config.multi.example.json    ← Ejemplo del formato multi-KB
├── audit.log                    ← Una línea por escritura
├── backups/                     ← .bak SQL Server (uno por operación)
├── GxExplorer\                  ← Fase 1, ya no se usa
├── GxGenie.Worker\                ← .NET 8 — 13 tools, multi-KB
│   ├── Program.cs               ← Dispatcher + stdio loop
│   ├── WorkerConfig.cs          ← Soporta single-KB y multi-KB
│   ├── WorkerSession.cs         ← Estado por-KB hot-swappable
│   ├── KbRepository.cs          ← SQL puro, recibe IKbSchemaAdapter
│   ├── IKbSchemaAdapter.cs      ← Gx17 / Gx18 adapters
│   ├── KbDef.cs                 ← KbDef + GxInstall
│   ├── KbDecoder.cs / KbTypeMap.cs
│   ├── MsBuildRunner.cs         ← Auto-detecta Genexus.Tasks.targets
│   ├── BackupHelper.cs / AuditLogger.cs / LocalDbAttacher.cs
│   ├── XpzTemplates.cs          ← XPZ mínimo para create_procedure
│   └── WriteTools.cs            ← 5 tools de escritura
└── GxGenie.Gateway\               ← .NET 8 — MCP server JSON-RPC stdio
    ├── Program.cs / GatewayConfig.cs
    ├── WorkerProxy.cs           ← Spawnea Worker + IPC stdin/stdout
    ├── McpServer.cs             ← JSON-RPC 2.0 manual
    └── ToolSchemas.cs           ← 12 input schemas
```

---

## Contribuir

### Cómo agregar una tool nueva

1. **Worker**: añadir `case "gx_mi_tool":` en `Program.cs:Dispatch` que
   despache a un método de `KbRepository` (lectura) o `WriteTools` (escritura).
2. **Gateway**: añadir entry en `ToolSchemas.All` con su `inputSchema`.
3. **Tests**: añadir al smoke test (`test-mcp.ps1` / `test-mcp-write.ps1`).
4. **Docs**: actualizar la tabla en README.md y CLAUDE.md.

### Reportar bugs

- Reproducir contra `config.test.json` (KB temporal `C:\KB\GxGenieTest`)
  para no tocar KBs de producción.
- Adjuntar la última line del log de stderr del Gateway:
  `& claude mcp logs genexus`.
- Incluir el contenido de `audit.log` si involucra escritura.

---

## Licencia

[MIT](LICENSE) — usalo libremente, modificalo, distribuilo.

Este repo **no incluye binarios de GeneXus** — las DLLs y las tasks de MSBuild
deben provenir de una instalación licenciada de GeneXus 17 o 18. GeneXus es
producto comercial de GeneXus S.A.
