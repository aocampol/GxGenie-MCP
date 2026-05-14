# GxGenie — MCP Server para GeneXus 17/18
> Archivo de contexto para Claude Code. Colócalo en: `C:\Proyectos\GxGenie\CLAUDE.md`

## Objetivo del proyecto
Construir un MCP Server (Model Context Protocol) que permita a Claude Code interactuar 
directamente con Knowledge Bases de GeneXus 17 y 18 — leer objetos, crear, modificar 
y ejecutar builds — sin necesidad de tener el IDE de GeneXus abierto.

## Entorno del desarrollador
- OS: Windows 11 Enterprise
- GeneXus: 17U1 instalado en `C:\Program Files (x86)\GeneXus\GeneXus17U1`
- GeneXus SDK DLLs: extraídas en `C:\GxSDK17` (35 DLLs de Artech.*)
- .NET Framework: 4.8 (Release=533509)
- .NET SDK: 8.0.421
- Compilador .NET 4.8: `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`
- Visual Studio: 2019 Community + 2022 Community instalados
- PowerShell: 7.6.1
- Variables de entorno del sistema:
  - GX_PROGRAM_DIR = `C:\Program Files (x86)\GeneXus\GeneXus17U1`
  - GX_SDK_DIR     = `C:\GxSDK17`

## Knowledge Bases disponibles para pruebas
- `C:\KB\Gx17U1\SampleKB\SampleKB.gxw`          ← KB principal de prueba
- `C:\KB\Gx17U11\SampleKB2\SampleKB2.gxw`
- `C:\KB\Gx17U11\SampleKB3\SampleKB3.gxw`
- `C:\KB\Gx17U11\SampleKB4\SampleKB4.gxw`

## DLLs clave disponibles en C:\GxSDK17
```
Artech.Architecture.BL.Framework.dll
Artech.Architecture.Common.dll
Artech.Architecture.Interfaces.dll
Artech.Architecture.Language.dll
Artech.Architecture.UI.Framework.dll
Artech.Common.dll
Artech.Common.Framework.dll
Artech.Common.Helpers.dll
Artech.Common.Language.dll
Artech.Common.Properties.dll
Artech.CommonI.dll
Artech.FrameworkDE.dll
Artech.Genexus.Common.dll
Artech.Layers.Framework.dll
Artech.MsBuild.Common.dll
Artech.Packages.Patterns.dll
Artech.Template.Base.dll
Artech.Template.Helper.dll
Artech.Template.Parser.dll
Artech.Udm.Architecture.Common.dll
Artech.Udm.BL.dll
Artech.Udm.Data.dll
Artech.Udm.Framework.dll
Artech.Udm.Layers.BL.dll
Artech.Udm.Layers.Common.dll
Artech.Udm.Layers.Data.SQL.dll
Artech.Udm.Layers.dll
```
Además en `C:\Program Files (x86)\GeneXus\GeneXus17U1\` hay ~323 DLLs adicionales
incluyendo `Artech.Genexus.Environment.dll` si existe.

## Arquitectura final (post Fase 3)

```
Claude Code (tu suscripción)
    │ stdio — MCP Protocol (JSON-RPC 2.0)
    ▼
GxGenie.Gateway      ← .NET 8 — habla MCP con Claude Code
    │ stdin/stdout JSON (Worker como proceso hijo)
    ▼
GxGenie.Worker       ← .NET 8 — dispatcher de 10 tools
    │
    ├── SQL directo (lecturas)           → LocalDB de la KB
    └── MSBuild + Genexus.Tasks.targets  → escrituras seguras
                                            (Export, Import, BuildOne,
                                             DeleteObject, …)
```

**Cambio respecto al plan original**: el Worker no necesita
cargar DLLs de GeneXus (queda en .NET 8 puro). Las escrituras se
hacen vía MSBuild + `genexus.msbuild.tasks.dll`, que es la misma vía
que usa el IDE de GeneXus internamente. No requiere elevación.

## Estructura de carpetas del proyecto
```
C:\Proyectos\GxGenie\
├── CLAUDE.md                    ← este archivo
├── docs\                        ← FASE{1..4}_NOTES.md + PROMPT_FASE{1..4}.md
├── config.json                  ← config principal (SampleKB)
├── config.test.json             ← config para la KB temporal de pruebas
├── audit.log / audit-test.log   ← una línea por escritura
├── backups/ + backups-test/     ← .bak SQL Server (uno por operación)
├── GxExplorer\                  ← Fase 1, intacto, ya no se usa
├── GxGenie.Worker\                ← .NET 8 — toda la lógica de KB
│   ├── Program.cs               ← dispatcher de 10 tools
│   ├── WorkerConfig.cs / Models.cs
│   ├── KbTypeMap.cs / KbDecoder.cs / KbRepository.cs   ← lectura SQL
│   ├── MsBuildRunner.cs         ← genera .msbuild + invoca msbuild.exe
│   ├── BackupHelper.cs          ← BACKUP DATABASE a .bak
│   ├── AuditLogger.cs / LocalDbAttacher.cs
│   ├── XpzTemplates.cs          ← XPZ mínimo para create_procedure
│   ├── WriteTools.cs            ← 5 tools de escritura
│   └── GxGenie.Worker.csproj
└── GxGenie.Gateway\               ← .NET 8 — MCP server (stdio JSON-RPC)
    ├── Program.cs / GatewayConfig.cs
    ├── WorkerProxy.cs           ← spawneo Worker + IPC stdin/stdout
    ├── McpServer.cs             ← JSON-RPC 2.0 manual
    ├── ToolSchemas.cs           ← 10 input schemas
    ├── test-mcp.ps1             ← E2E read-only
    ├── test-mcp-write.ps1       ← E2E read + write roundtrip
    └── GxGenie.Gateway.csproj
```

## config.json — dos formatos soportados

GxGenie acepta dos formatos de config.json. El Worker detecta cuál se está
usando y normaliza al modelo interno multi-KB.

### Formato legacy (single-KB) — Fase 1–3
```json
{
  "GeneXus": {
    "Version": "17",
    "InstallationPath": "C:\\Program Files (x86)\\GeneXus\\GeneXus17U1",
    "SdkPath": "C:\\GxSDK17",
    "MSBuildPath": "C:\\Windows\\Microsoft.NET\\Framework\\v4.0.30319\\MSBuild.exe"
  },
  "KnowledgeBase": {
    "Path": "C:\\KB\\Gx17U1\\SampleKB\\SampleKB.gxw",
    "ConnectionString": "Server=(LocalDB)\\MSSQLLocalDB;Database=GX_KB_SampleKB;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=15"
  },
  "Worker": { "ExecutablePath": "...", "TimeoutSeconds": 120 },
  "Security": { "AllowWrite": true, "AllowBuild": true, "AuditLog": true, "AuditLogPath": "...", "BackupRoot": "..." }
}
```

### Formato multi-KB — Fase 4 (recomendado)
```json
{
  "GeneXus": [
    { "Version": "17", "InstallationPath": "...", "SdkPath": "...", "MSBuildPath": "..." },
    { "Version": "18", "InstallationPath": "...", "SdkPath": "...", "MSBuildPath": "..." }
  ],
  "KnowledgeBases": [
    { "Name": "SampleKB", "Path": "...", "ConnectionString": "...", "GeneXusVersion": "17" },
    { "Name": "SampleKB2",   "Path": "...", "ConnectionString": "...", "GeneXusVersion": "17" }
  ],
  "ActiveKB": "SampleKB",
  "Worker": { "ExecutablePath": "...", "TimeoutSeconds": 120 },
  "Security": { "AllowWrite": false, "AllowBuild": false, "AuditLog": true, "AuditLogPath": "...", "BackupRoot": "..." }
}
```

Ver `config.multi.example.json` para un ejemplo real con dos KBs.

## MCP Tools implementadas (todas activas)

### Lectura (vía SQL directo, Fase 2)
| Tool | Descripción |
|------|-------------|
| `gx_kb_info` | Versión KB, conteo por tipo, modelos |
| `gx_list_objects` | Listar objetos por tipo + filtro de nombre |
| `gx_read_object` | Código fuente decodificado de un objeto |
| `gx_search` | Búsqueda por nombre (rápido) o por código (lento) |
| `gx_list_attributes` | Atributos de una Transaction |

### Escritura (vía MSBuild + tasks GeneXus, Fase 3)
| Tool | Task MSBuild | Notas |
|------|--------------|-------|
| `gx_export_xpz` | `Export` | Genera .xpz |
| `gx_import_xpz` | `Import` | Hace `BACKUP DATABASE` automático antes |
| `gx_create_procedure` | `Import` + XPZ generado | Crea un Procedure nuevo |
| `gx_update_object_code` | `Export` + modify XPZ + `Import UpdatedAndNew` | Actualiza source de un Part. Hoy sólo Procedure (source/rules/conditions) — ver `XpzPartMap.cs`. |
| `gx_build_object` | `BuildOne` | Especifica + genera |
| `gx_delete_object` | `DeleteObject` | Backup automático antes (limitación: ver FASE3_NOTES.md) |

### Multi-KB (Fase 4)
| Tool | Descripción |
|------|-------------|
| `gx_list_kbs` | Lista las KBs configuradas e indica cuál está activa |
| `gx_switch_kb` | Cambia la KB activa en caliente (reusa el proceso Worker y la sesión MCP) |

## Convenciones de código
- C# para todo. Encoding UTF-8.
- GxGenie.Worker y GxGenie.Gateway: **.NET 8**, compilar con `dotnet build`.
- GxExplorer (Fase 1): net48, intacto pero ya no se usa.
- No se cargan DLLs de GeneXus en proceso (descubrimiento Fase 2/3). Para
  escritura usamos `genexus.msbuild.tasks.dll` ejecutada por MSBuild
  como subproceso — la BL canónica corre allí.
- Antes de cualquier escritura: `BACKUP DATABASE` a `.bak` (BackupHelper).
- Toda escritura queda registrada en `audit.log` (AuditLogger).
- Tras cualquier `CloseKnowledgeBase` MSBuild detacha la DB del LocalDB
  → usar `LocalDbAttacher.EnsureAttached` antes de SQL crudo.

## Cómo compilar
```powershell
dotnet build C:\Proyectos\GxGenie\GxGenie.Worker\GxGenie.Worker.csproj -c Release
dotnet build C:\Proyectos\GxGenie\GxGenie.Gateway\GxGenie.Gateway.csproj -c Release
```

## Cómo registrar el Gateway en Claude Code
```powershell
claude mcp add --transport stdio genexus -- `
  "C:\Proyectos\GxGenie\GxGenie.Gateway\bin\Release\net8.0\GxGenie.Gateway.exe"
claude mcp list
```

El Gateway resuelve `config.json` desde `--config`, `$env:GXGENIE_CONFIG`,
junto al exe, o `../../../../config.json`.

## Estado del proyecto — **COMPLETADO** (Fases 0–4)
- [x] Fase 0: Entorno validado — DLLs en C:\GxSDK17, variables configuradas
- [x] Fase 1: GxExplorer — prueba de concepto
- [x] Fase 2: Worker (.NET 8) con 5 tools de lectura vía SQL directo
- [x] Fase 2.5: Gateway MCP — JSON-RPC manual sobre stdio (sin paquetes externos)
- [x] Fase 3: 5 tools de escritura (MSBuild + tasks GeneXus + backup + audit)
- [x] **Fase 4**: Adapter pattern GX17/GX18, multi-KB config, `gx_switch_kb`
      + `gx_list_kbs`, `setup.ps1` idempotente con `-Uninstall`, `README.md`.
      Detalle en **FASE4_NOTES.md**.
- [ ] Fase 3.5 (pendiente): fix `delete_object` post-import, `update_object_code`,
      `create_transaction`, `add_attribute`

### Resumen final
- **13 tools** activas (5 lectura + 6 escritura + 2 multi-KB).
- **Versiones de GeneXus soportadas**: GX17U1 / GX17U11 (validado E2E),
  GX18 (adapter preparado, validación pendiente sobre instalación real).
- **Dos modos de instalación**:
  - **Per-KB (recomendado)**: `.\setup.ps1 -InstallToKb C:\KB\<X>` dropea un
    `.mcp.json` en la carpeta. Claude detecta la KB por cwd via
    `WorkerConfig.AutoDetectFromDirectory()` — sin `config.json` central.
  - **Global (legacy)**: `.\setup.ps1` registra un MCP a nivel usuario que
    usa `config.json` multi-KB (necesario para `gx_switch_kb`).
- **Limitaciones conocidas**: ver "Limitaciones" en README.md y FASE3_NOTES.md
  / FASE4_NOTES.md.

## Fase 1 — completada (resumen)
GxExplorer compila con `csc.exe` de .NET Framework 4.8 y corre contra
`C:\KB\Gx17U1\SampleKB\SampleKB.gxw`. La app llega a invocar
`KnowledgeBase.Open(OpenOptions)` y obtiene una NullReferenceException
**adentro del SDK** (línea 1057 de `KnowledgeBase.cs`).

Causa identificada (vía desensamblado IL del método): el campo estático
`KnowledgeBase.m_KBFactory` (tipo `IKnowledgeBaseFactory`) está en `null`
porque el bootstrap del Package Manager del IDE no se ejecuta en un host
externo. No es un error de licencia.

Tipos del SDK que ya sabemos manipular:
- `Artech.Architecture.Common.Objects.KnowledgeBase` (+ nested `OpenOptions`)
- `KBModel`, `IKBModelObjects`, `KBObject`, `KBObjectDescriptor`
- `Artech.Architecture.UI.Framework.Services.UIServices` (solo getters)
- `Artech.Udm.Framework.UdmKnowledgeBase` (capa más baja, candidata Fase 2)

Detalle completo, IL del Open, y plan para Fase 2 en **FASE1_NOTES.md**.

Compilar y correr GxExplorer:
```powershell
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$refs = Get-ChildItem "C:\GxSDK17\*.dll" | ForEach-Object { "/r:$($_.FullName)" }
& $csc /nologo /target:exe /platform:x64 /debug:full `
    /out:GxExplorer\bin\GxExplorer.exe @refs GxExplorer\Program.cs
GxExplorer\bin\GxExplorer.exe
```

Nota: `csc.exe v4.0.30319` solo soporta **C# 5** — evitar `when` filters,
`out var`, `$"..."`. Para C# 6+ usar el csc de Roslyn (Visual Studio).

## Fase 2 — completada (resumen)
Camino C (SQL directo a la LocalDB que GeneXus usa como storage) resultó
ganador. La KB de GeneXus 17/18 se persiste en SQL Server LocalDB local
— el archivo `knowledgebase.connection` de cada KB apunta a la instancia
y nombre de DB. Tablas relevantes: `Entity`, `EntityVersion`,
`EntityVersionComposition`, `ATTRIBUTE`, `TRN_DSD`.

El source code (events, rules, body de Procedure, etc.) se guarda como
`varbinary` con envoltorio `[4 bytes magic 01 02 03 04][7 bytes header]
[gzip stream]`; descomprime a un XML `<TokenDataList>` cuyo source se
reconstruye concatenando los `<Word>`.

**No se usaron las DLLs de GeneXus** — el Worker quedó en .NET 8
puro, sin Worker net48 ni AssemblyResolve. Se sigue conservando el
plan original (Worker net48 + Gateway net8) **para Fase 3**, cuando
las operaciones de escritura probablemente sí requieran la capa BL.

Tools implementadas (todas validadas contra `SampleKB`):
- `gx_kb_info`, `gx_list_objects`, `gx_read_object`,
  `gx_search` (name + code), `gx_list_attributes`.

Detalle completo, schema y limitaciones en **FASE2_NOTES.md**.

### Cómo correr el Worker
```powershell
dotnet build C:\Proyectos\GxGenie\GxGenie.Worker\GxGenie.Worker.csproj -c Release

$exe = "C:\Proyectos\GxGenie\GxGenie.Worker\bin\Release\net8.0\GxGenie.Worker.exe"
$env:GXGENIE_CONFIG = "C:\Proyectos\GxGenie\config.json"

# Una request y sale
& $exe --once '{"tool":"gx_kb_info"}'

# Loop stdin/stdout (una request JSON por línea, una response por línea).
# Es el modo que invocará el futuro Gateway MCP vía IPC.
& $exe
```

## Fase 3 — completada (resumen)

Se construyó **GxGenie.Gateway** (MCP server JSON-RPC stdio) y se sumaron
**5 tools de escritura** al Worker, todas vía **MSBuild + tasks de
GeneXus** (camino oficial, sin elevación, sin DLLs en proceso).

Decisión clave: el SDK no expone una implementación discoverable de
`IKnowledgeBaseFactory` y `GeneXus.exe` requiere elevación UAC; en
cambio `genexus.msbuild.tasks.dll` (el mismo DLL que usa el IDE)
expone tasks `OpenKnowledgeBase`, `Export`, `Import`, `BuildOne`,
`DeleteObject`, `CreateKnowledgeBase` que se invocan via MSBuild sin
problema.

Validado E2E vía Gateway: `initialize` → `tools/list` (10 tools) →
`tools/call gx_create_procedure` → `gx_list_objects` (encuentra el
objeto creado) → `gx_read_object` (source roundtripea) →
`gx_export_xpz` (1420 bytes generados).

Detalle completo, esquema del XPZ generado por `create_procedure`,
GUIDs descubiertos y limitaciones en **FASE3_NOTES.md**.

## Próxima fase: FASE 3.5 / FASE 4
**Fase 3.5 (write tools complementarias):**
- Fix de `gx_delete_object` post-import (incluir `parent`/`parentType` en el XPZ).
- `gx_update_object_code` (read → modify → re-import con `ImportType=UpdatedAndNew`).
- `gx_create_transaction` y `gx_add_attribute`.

**Fase 4 (setup + GX18):**
- `setup.ps1` que detecte versión de GeneXus, valide LocalDB, compile
  ambos proyectos y registre el Gateway en Claude Code.
- Probar contra una install de GX18 y ajustar GUIDs si difieren.

### KB de prueba para Fase 2
`C:\KB\Gx17U1\SampleKB\SampleKB.gxw` (138.975 entidades). Si el `.mdf`
no está adjuntado al LocalDB:
```sql
CREATE DATABASE GX_KB_SampleKB ON
  (FILENAME = N'C:\KB\Gx17U1\SampleKB\GX_KB_SampleKB.mdf'),
  (FILENAME = N'C:\KB\Gx17U1\SampleKB\GX_KB_SampleKB_log.ldf')
FOR ATTACH;
```

## Notas importantes
- Las DLLs de GeneXus pueden requerir licencia activa para abrir una KB
  Si ocurre error de licencia, reportarlo claramente — es información útil
- El AssemblyResolve handler es CRÍTICO — sin él las DLLs de GeneXus
  no se encuentran en runtime aunque estén en C:\GxSDK17
- Nunca hardcodear rutas — usar config.json o variables de entorno
- Nunca operar sobre KB de producción en fases de prueba
