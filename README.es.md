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
3. Dropea un `.mcp.json` en la carpeta de la KB apuntando al Gateway compilado,
   **con `GXGENIE_ALLOW_WRITE=true` y `GXGENIE_ALLOW_BUILD=true` en su bloque
   `env`** — writes y builds quedan habilitados desde la primera ejecución.
   Cada op destructiva igual snapshotea la LocalDB a un `.bak` y queda
   registrada en `audit.log` — la red de seguridad es automática, no opt-in.

Para usarlo después:

```powershell
cd C:\KB\Gx17U1\SampleKB
claude
```

La primera vez que Claude Code arranque en esa carpeta te va a pedir aprobar
el MCP `genexus`. Decí que sí — la decisión queda guardada para esa carpeta.

**Deshabilitar escritura** (instalación read-only — útil cuando compartís una
KB para acceso de sólo inspección):

```powershell
.\setup.ps1 -InstallToKb C:\KB\Gx17U1\SampleKB -ReadOnly
```

Esto genera un `.mcp.json` sin bloque `env`; el Worker entra en auto-detect
por cwd y queda en modo read-only (el default seguro original).

**Usar un `config.json` compartido** (en lugar del auto-detect per-KB):

```powershell
.\setup.ps1 -InstallToKb C:\KB\Gx17U1\SampleKB -ConfigPath C:\Proyectos\GxGenie\config.json
```

Inyecta `GXGENIE_CONFIG` en el `env` del `.mcp.json`, apuntando al archivo
explícito. Útil si mantenés settings de Security/backup centralizados en
lugar de uno por KB.

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

## Actualizar a una versión nueva

Si ya clonaste el repo y querés traer una versión más nueva, el camino
rápido es el script incluido:

```powershell
cd C:\Proyectos\GxGenie
.\update.ps1
```

`update.ps1` aborta si encuentra algún `GxGenie.Worker.exe` /
`GxGenie.Gateway.exe` corriendo (típicamente porque Claude Code tiene el MCP
cargado), y después hace `git pull` + `dotnet build` para los dos proyectos.

**Equivalente manual** (si preferís correr cada paso):

```powershell
# 1) Cerrá toda sesión de Claude Code que tenga el MCP cargado — sino el .exe
#    queda lockeado y el build falla. Verificá con:
tasklist /FI "IMAGENAME eq GxGenie.Gateway.exe"

# 2) Traer los commits nuevos
git -C C:\Proyectos\GxGenie pull origin main

# 3) Recompilar Worker y Gateway
dotnet build C:\Proyectos\GxGenie\GxGenie.Worker\GxGenie.Worker.csproj  -c Release
dotnet build C:\Proyectos\GxGenie\GxGenie.Gateway\GxGenie.Gateway.csproj -c Release

# 4) Volvé a abrir Claude Code — la próxima llamada a una tool relanza el
#    Gateway con el binario nuevo.
```

Revisá **[CHANGELOG.md](CHANGELOG.md)** para ver qué incorpora, remueve o rompe
cada versión. El registro del MCP (`claude mcp add`) y los archivos
`config.json` / `.mcp.json` **no** se vuelven a crear entre versiones, salvo
que el changelog lo aclare explícitamente.

Si `dotnet build` se queja con `error MSB3027` por un archivo en uso, alguna
ventana de Claude Code todavía tiene el Gateway abierto. Cerrala y reintentá,
o como último recurso:

```powershell
taskkill /F /IM GxGenie.Worker.exe /IM GxGenie.Gateway.exe
```

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

## Tools disponibles (28)

### Lectura básica (SQL directo)

| Tool                   | Descripción                                                            |
|------------------------|------------------------------------------------------------------------|
| `gx_kb_info`           | Versión KB, conteo de objetos por tipo, KB activa, versión de GeneXus  |
| `gx_list_objects`      | Listar objetos por tipo con filtro de nombre                           |
| `gx_read_object`       | Source decodificado de cada Part (events, rules, body, structure, …)   |
| `gx_search`            | Búsqueda por nombre (rápido) o por código (lento pero exhaustivo)      |
| `gx_list_attributes`   | Atributos de una Transaction con tipo, longitud, PK                    |
| `gx_list_object_parts` | Listar los Parts de un tipo de objeto, con flag de editabilidad y kind |

### Lectura estructurada (JSON parseado)

| Tool                | Descripción                                                              |
|---------------------|--------------------------------------------------------------------------|
| `gx_get_structure`        | Estructura de Transaction/SDT/DataSelector como árbol JSON con niveles   |
| `gx_get_layout`           | Web Form como árbol JSON, autodetectando KIP (legacy) vs GXML (moderno)  |
| `gx_get_variables`        | Variables con `data_type` decodificado del `AttCustomType`               |
| `gx_get_unused_variables` | Variables no referenciadas en `events` / `rules` / `conditions` / `source` del mismo objeto. Separa los candidatos eliminables de las `<StandardVariable>` auto-incluidas. |

### Escritura — objetos y código (MSBuild + tasks de GeneXus)

| Tool                    | Notas                                                                |
|-------------------------|----------------------------------------------------------------------|
| `gx_export_xpz`         | Exportar objeto(s) a un `.xpz`                                       |
| `gx_import_xpz`         | Importar un `.xpz`. Backup SQL automático antes                      |
| `gx_create_procedure`   | Crear un Procedure nuevo (XPZ mínimo en memoria + import)            |
| `gx_create_transaction` | Crear una Transaction nueva — nivel raíz + atributo clave, más sub-niveles anidados opcionales (`levels`, master-detail recursivo). Reusa atributos que ya existen en la KB. |
| `gx_update_object_code` | Actualizar el source/text de un Part para 15 tipos de objeto (Procedure, WebPanel, Transaction, DataProvider, Domain, SDT, …). Valida editabilidad por Part. |
| `gx_build_object`       | Especificar + generar un objeto (requiere `AllowBuild=true`)         |
| `gx_delete_object`      | Borrar un objeto. Backup SQL automático antes                        |

### Escritura — Structure de Transaction (granular)

| Tool                       | Notas                                                              |
|----------------------------|--------------------------------------------------------------------|
| `gx_add_attribute`         | Crear atributo y opcionalmente asociarlo a un Level de Transaction. Soporta `data_type` (`bas:Numeric`, `bas:VarChar`, …) o `based_on_domain`. |
| `gx_remove_attribute`      | Quita la referencia del atributo del Level — el Attribute queda en la KB. |
| `gx_set_attribute_property`| Modifica cualquier Property de un Attribute existente (`Description`, `Length`, `Decimals`, `ATTCUSTOMTYPE`, `idBasedOn`, `AUTONUMBER`, …) |
| `gx_remove_variable`       | Quita una `<Variable>` de un Procedure/DataProvider/WebPanel/Transaction. Los pre-checks rechazan standards y cualquier variable aún referenciada en `events`/`rules`/`conditions`/`source`. |

### Escritura — Layout del Web Form (granular, principalmente GXML)

| Tool                      | Notas                                                               |
|---------------------------|---------------------------------------------------------------------|
| `gx_set_control_property` | Modifica un attribute XML de un control identificado por `controlName` |
| `gx_add_control`          | Agrega un control nuevo dentro de un parent (por `controlName` o `id`). Whitelist-validated; sólo GXML. |
| `gx_remove_control`       | Quita un control y sus descendientes. La BL de GeneXus puede rechazar si el resultado queda inválido (ej: `<cell>` vacío); rollback automático del snapshot SQL. |

### Multi-KB

| Tool            | Descripción                                                              |
|-----------------|--------------------------------------------------------------------------|
| `gx_list_kbs`   | Listar las KBs del `config.json` e indicar cuál está activa              |
| `gx_switch_kb`  | Cambiar la KB activa en caliente, sin reiniciar Claude Code              |

> Las tools de escritura requieren `Security.AllowWrite=true` (y
> `AllowBuild=true` para `gx_build_object`) en `config.json`. Están
> deshabilitadas por defecto — hay que habilitarlas explícitamente.
> Cada operación destructiva snapshotea la LocalDB de la KB a un `.bak` bajo
> `backups/` antes — cualquier Import fallido es restaurable con
> `RESTORE DATABASE`.

---

## Arquitectura

```
Claude Code (Anthropic)
    │ stdio — Protocolo MCP (JSON-RPC 2.0)
    ▼
GxGenie.Gateway      ← .NET 8 — habla MCP con Claude Code
    │ stdin/stdout JSON (Worker como proceso hijo, long-lived)
    ▼
GxGenie.Worker       ← .NET 8 — dispatcher de 28 tools, multi-KB
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
├── update.ps1                   ← Script de pull + rebuild para instalaciones existentes
├── CHANGELOG.md                 ← Notas por release
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

1. **`gx_delete_object` no puede borrar objetos creados con `gx_create_procedure`
   ni `gx_create_transaction`.** GeneXus registra los objetos en dos niveles: el
   nivel *diseño* (`Entity`/`EntityVersion`, que usan las lecturas SQL de GxGenie)
   y el nivel *modelo* (`ModelEntityVersion`). Los objetos creados vía el import
   XPZ de GxGenie no quedan registrados de forma confiable en el nivel modelo, y
   la task MSBuild `DeleteObject` resuelve por ahí → reporta "X was not found".
   Sobre objetos que ya existían en la KB funciona bien. Workaround: borrar desde
   el IDE de GeneXus, o restaurar el `.bak` SQL que toda tool `gx_create_*` toma
   automáticamente justo antes del import. La vía de fix oficial
   (`gxnext` / "GeneXus for Agents", abril 2026) requiere GeneXus 18 / Next —
   GeneXus 17 no tiene tooling oficial de agentes, que es justamente el nicho
   que cubre GxGenie. Investigación completa en las notas locales
   `docs/DELETE_OBJECT_LIMITATION.md`.
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
