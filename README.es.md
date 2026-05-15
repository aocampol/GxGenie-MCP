[English](README.md) · **Español**

# GxGenie — MCP Server para GeneXus 17 / 18

GxGenie es un servidor [Model Context Protocol](https://modelcontextprotocol.io)
que permite a **Claude Code** hablar directamente con Knowledge Bases de
GeneXus 17 / 18 — listar objetos, leer código, crear procedures, exportar/importar
XPZs, compilar — **sin necesidad de abrir el IDE de GeneXus**.

Hablale a tu KB en lenguaje natural:

> *"Listame todas las transactions cuyo nombre empiece con Customer"*
> *"Mostrame el source del procedure CalcularTotal"*
> *"Creá un procedure llamado DemoMCP que escriba 'hola' en el log"*

---

## Requisitos

| Componente  | Versión            | Notas                                                       |
|-------------|--------------------|-------------------------------------------------------------|
| Windows     | 10 / 11            | El SDK de GeneXus es x86                                    |
| GeneXus     | 17U1 / 17U11 / 18  | 17 validado E2E; 18 soportado vía adapter                   |
| .NET SDK    | 8.0+               | Se instala automáticamente vía `setup.ps1`                  |
| LocalDB     | Viene con SQL Server Express | Las KBs persisten en LocalDB                      |
| Claude Code | última             | `claude --version` para verificar                            |

---

## Instalación

El modo recomendado es **Per-KB**: el MCP se ata a la carpeta de cada KB y se
carga automáticamente cuando Claude Code la abre. También hay un modo global
para escenarios multi-KB con switch en caliente.

### Modo Per-KB (recomendado)

```powershell
git clone https://github.com/aocampol/GxGenie-MCP.git C:\Proyectos\GxGenie
cd C:\Proyectos\GxGenie
.\setup.ps1 -InstallToKb C:\KB\Gx17U1\SampleKB
```

Un comando por KB. Repetilo por cada KB que quieras "MCP-habilitar" —
`setup.ps1` es idempotente. El comando:

1. Verifica el .NET 8 SDK (lo instala con `winget` si falta).
2. Compila Worker + Gateway en modo Release.
3. Dropea un `.mcp.json` en la carpeta de la KB apuntando al Gateway compilado.

Para usarlo después:

```powershell
cd C:\KB\Gx17U1\SampleKB
claude
```

La primera vez que Claude Code arranque en esa carpeta te va a pedir aprobar
el MCP `genexus`. Decí que sí — la decisión queda guardada para esa carpeta.

**Habilitar escritura / build** (deshabilitados por defecto por seguridad):

```powershell
$env:GXGENIE_ALLOW_WRITE = "true"
$env:GXGENIE_ALLOW_BUILD = "true"
claude
```

### Modo global (multi-KB)

Registra un único MCP a nivel usuario y usa un `config.json` central con
todas tus KBs. Necesario si querés usar `gx_switch_kb` para saltar entre KBs
sin reiniciar Claude Code.

```powershell
.\setup.ps1
```

Esto escanea `C:\KB`, `D:\KB`, `C:\GeneXus\KB` buscando `.gxw`, arma un
`config.json` multi-KB y registra el MCP globalmente vía `claude mcp add`.

### Desinstalar

```powershell
.\setup.ps1 -Uninstall
```

Desregistra el MCP global, borra `bin/` y `obj/` y limpia las variables de
entorno. **No** toca `config.json`, los `.mcp.json` ya instalados en carpetas
de KB, `audit.log` ni `backups/` — eso lo decidís vos.

---

## Uso

No invocás las tools con slash commands — le hablás a Claude en lenguaje
natural y él elige la tool adecuada. Ver **[USAGE.es.md](USAGE.es.md)** para
la guía completa. Algunos ejemplos rápidos:

### Inspeccionar la KB
> **Vos:** ¿Qué objetos hay en la Knowledge Base actual?
> **Claude:** *[llama a `gx_kb_info`]* SampleKB (GX17), 138.975 entidades. Procedures: 4169, WebPanels: 2373, Transactions: 621, SDTs: 1245...

### Leer el source de un objeto
> **Vos:** Mostrame el source del procedure CalcularTotal.
> **Claude:** *[llama a `gx_read_object` con `name="CalcularTotal"`]*
> ```
> for each Customer
>     &total += CustomerBalance
> endfor
> ```

### Buscar un atributo en el código
> **Vos:** ¿Dónde se usa el atributo ClienteId en el código?
> **Claude:** *[llama a `gx_search` con `query="ClienteId"`, `search_in="code"`]* Lo encontré en 47 objetos: ...

### Crear un procedure
> **Vos:** Creá un procedure llamado DemoMCP que escriba "hola" en el log.
> **Claude:** *[llama a `gx_create_procedure`]* Procedure creado. Backup en `backups\SampleKB\20260514_104530\GX_KB_SampleKB__create_proc_DemoMCP.bak`.

### Trabajar con dos KBs en la misma sesión (sólo modo global)
> **Vos:** Listame las KBs disponibles.
> **Claude:** *[llama a `gx_list_kbs`]* SampleKB (activa), SampleKB2, SampleKB4 — todas GX17.
>
> **Vos:** Cambiá a SampleKB2 y decime cuántos procedures tiene.
> **Claude:** *[llama a `gx_switch_kb`, después `gx_list_objects type=Procedure`]* SampleKB2 tiene 76 procedures.

---

## Tools disponibles (13)

### Lectura (vía SQL directo)

| Tool                | Descripción                                                              |
|---------------------|--------------------------------------------------------------------------|
| `gx_kb_info`        | Versión KB, conteo de objetos por tipo, KB activa, versión de GeneXus    |
| `gx_list_objects`   | Listar objetos por tipo con filtro de nombre                             |
| `gx_read_object`    | Source decodificado (events, rules, body, structure…)                    |
| `gx_search`         | Búsqueda por nombre (rápido) o por código (lento pero exhaustivo)        |
| `gx_list_attributes`| Atributos de una Transaction con tipo, longitud, PK                       |

### Escritura (vía MSBuild + tasks de GeneXus)

| Tool                    | Notas                                                                 |
|-------------------------|-----------------------------------------------------------------------|
| `gx_export_xpz`         | Exportar objeto(s) a un `.xpz`                                        |
| `gx_import_xpz`         | Importar un `.xpz`. Backup SQL automático antes                       |
| `gx_create_procedure`   | Crear un Procedure nuevo (XPZ mínimo en memoria + import)             |
| `gx_update_object_code` | Actualizar el source de un objeto (hoy sólo Procedure)                |
| `gx_build_object`       | Especificar + generar un objeto (requiere `AllowBuild=true`)          |
| `gx_delete_object`      | Borrar un objeto. Backup SQL automático antes                         |

### Multi-KB

| Tool            | Descripción                                                            |
|-----------------|------------------------------------------------------------------------|
| `gx_list_kbs`   | Listar las KBs del `config.json` e indicar cuál está activa            |
| `gx_switch_kb`  | Cambiar la KB activa en caliente, sin reiniciar Claude Code            |

> Las tools de escritura requieren `Security.AllowWrite=true` (y
> `AllowBuild=true` para `gx_build_object`) en `config.json`. Están
> deshabilitadas por defecto — hay que habilitarlas explícitamente.

---

## Arquitectura

```
Claude Code (Anthropic)
    │ stdio — Protocolo MCP (JSON-RPC 2.0)
    ▼
GxGenie.Gateway      ← .NET 8 — habla MCP con Claude Code
    │ stdin/stdout JSON (Worker como proceso hijo, long-lived)
    ▼
GxGenie.Worker       ← .NET 8 — dispatcher de 13 tools, multi-KB
    │
    ├── SQL directo (lecturas)          → LocalDB de la KB
    └── MSBuild + Genexus.Tasks.targets → la misma BL canónica
        (escrituras)                       que usa el IDE de GeneXus
```

Decisiones clave de diseño:

- **Sin DLLs de GeneXus cargadas en proceso.** El Worker queda en .NET 8
  puro y delega toda operación de mutación a `msbuild.exe`, que carga el
  `genexus.msbuild.tasks.dll` oficial. No requiere elevación.
- **Backup SQL automático antes de cada escritura.** Un snapshot
  `BACKUP DATABASE` va a `backups/{kb}/{timestamp}/` antes de cualquier
  operación destructiva. Restaurable con `RESTORE DATABASE … WITH REPLACE`.
- **Audit log append-only** en `audit.log` para cada operación destructiva.

---

## Estructura del proyecto

```
GxGenie/
├── GxGenie.Gateway/             ← MCP server (.NET 8) — JSON-RPC sobre stdio
├── GxGenie.Worker/              ← Lógica de KB (.NET 8) — SQL reads + MSBuild writes
├── setup.ps1                    ← Instalador idempotente (modos per-KB y global)
├── config.multi.example.json    ← Ejemplo de config multi-KB
├── config.example.json          ← Ejemplo de config single-KB (legacy)
├── README.md / USAGE.md         ← Documentación
└── LICENSE                      ← MIT
```

---

## Soporte por versión de GeneXus

| Versión GeneXus | Lectura (SQL)        | Escritura (MSBuild)      | Estado            |
|-----------------|----------------------|--------------------------|-------------------|
| 17U1            | Validado E2E         | Validado E2E             | Production-ready  |
| 17U11           | Validado en SampleKB2  | Mismo schema que 17U1    | Production-ready  |
| 18              | Adapter listo        | Depende de Genexus.Tasks | Sin validar aún   |

El adapter de GX18 introspecta `INFORMATION_SCHEMA` para manejar el typo
histórico `KnowlegeBaseVersion` vs `KnowledgeBaseVersion`, y asume paridad
de schema con GX17 hasta validar contra una KB GX18 real.

---

## Limitaciones conocidas

1. **`gx_delete_object` no encuentra objetos creados con `gx_create_procedure`.**
   MSBuild devuelve "Procedure X was not found in the KB" aunque la fila exista.
   Workaround: borrar desde el IDE de GeneXus.
2. **`gx_create_procedure` no soporta variables ni rules personalizados** —
   el template XPZ los deja vacíos. Workaround: crear el procedure y luego
   editar las parts con un XPZ adicional.
3. **`gx_build_object`** requiere un Environment configurado en la KB. En
   una KB recién creada sin generator activo puede producir output vacío.
4. **GX18 no validado E2E.** El adapter de schema está listo, pero los GUIDs
   de tipo usados por `gx_create_procedure` provienen de un export GX17 y
   deberían ser estables, pero no confirmado.
5. **Cambio de KB**: tras `gx_switch_kb`, MSBuild puede detachar la DB
   anterior del LocalDB. `LocalDbAttacher.EnsureAttached` la reataca al
   próximo SQL crudo — transparente para el usuario, pero la primera tool
   tras un switch puede tardar 1-2s extras.

---

## Licencia

[MIT](LICENSE) — usalo libremente, modificalo, redistribuilo.

Este repo **no incluye binarios de GeneXus**. El DLL de tasks MSBuild y el
entorno GeneXus deben provenir de una instalación licenciada de GeneXus 17 o 18.
GeneXus es producto comercial de GeneXus S.A.
