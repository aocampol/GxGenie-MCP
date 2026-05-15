[English](USAGE.md) · **Español**

# Usando GxGenie desde Claude Code

Guía práctica de cómo invocar las tools del MCP **una vez instalado** (ver
[README.es.md](README.es.md) para instalación).

---

## El modelo mental

**No invocás las tools con comandos slash.** Le hablás a Claude en lenguaje
natural y Claude elige qué tool del MCP llamar según lo que pediste.

```
cd C:\KB\Gx17U1\SampleKB
claude
```

Al iniciar, Claude detecta el `.mcp.json` y la primera vez te pregunta si
confiás en él. Decí que sí — esa decisión queda guardada para esa carpeta.

Dentro de la sesión, `/mcp` muestra los servers cargados y sus tools.

---

## Las 13 tools en una tabla

### Lectura (sin permisos, Claude las usa libremente)

| Tool | Qué hace | Prompt típico |
|------|----------|---------------|
| `gx_kb_info` | KB activa, versión, conteo por tipo | *"¿Qué KB tengo cargada?"* |
| `gx_list_objects` | Lista objetos por tipo + filtro | *"Listame todos los procedures que empiezan con `Calc`"* |
| `gx_read_object` | Devuelve el source decodificado | *"Mostrame el código de `CalcularTotal`"* |
| `gx_search` | Busca por nombre o dentro del código | *"¿Dónde se usa el atributo `ClienteId`?"* |
| `gx_list_attributes` | Atributos de una Transaction | *"¿Qué atributos tiene la transacción `Cliente`?"* |
| `gx_list_kbs` | Lista KBs configuradas (modo global) | *"¿Qué KBs tengo disponibles?"* |

### Escritura (requieren `AllowWrite=true`, Claude te pide confirmación)

| Tool | Qué hace | Prompt típico |
|------|----------|---------------|
| `gx_export_xpz` | Exporta a `.xpz` | *"Exportá `CalcularTotal` a `C:\temp\x.xpz`"* |
| `gx_import_xpz` | Importa un `.xpz` (backup automático antes) | *"Importá `C:\temp\x.xpz`"* |
| `gx_create_procedure` | Crea un Procedure nuevo | *"Creá un procedure `Demo` con `msg('hola')`"* |
| `gx_update_object_code` | **Reemplaza el source de un Part de un objeto** (export → modify XPZ → import UpdatedAndNew) | *"Optimizá el código de `CalcularTotal` así: ..."* |
| `gx_delete_object` | Borra un objeto (backup antes) | *"Borrá el procedure `Obsoleto`"* |
| `gx_build_object` | Especifica + genera (requiere `AllowBuild`) | *"Compilá `CalcularTotal`"* |
| `gx_switch_kb` | Cambia de KB (modo global) | *"Cambiá a la KB `SampleKB2`"* |

---

## Workflows reales

### 1) Auditar uso de un atributo antes de un refactor

```
Vos:  Antes de renombrar `ClienteId` a `CustomerId`, decime todos los
      objetos que lo usan en su código fuente.

Claude:  [gx_search query="ClienteId" search_in="code"]
         Encontré 47 hits en 23 objetos. Los más importantes:
         - Procedure CalcularDeuda (5 referencias)
         - WebPanel ConsultaClientes (3 referencias)
         - DataProvider DPClientes (2 referencias)
         ...
```

### 2) Generar documentación de un procedure

```
Vos:  Leé el procedure `CalcularDeuda` y documentame qué hace en español
      plano, qué parámetros recibe, y qué efectos secundarios tiene.

Claude:  [gx_read_object name="CalcularDeuda" type="Procedure"]
         [Devuelve source, rules, conditions]

         CalcularDeuda hace lo siguiente:
         1. Recibe ClienteId como parámetro de entrada
         2. Suma las facturas pendientes del cliente
         3. ...
         Efectos secundarios: actualiza la tabla DEUDA_HISTORICO.
```

### 3) Crear un Procedure desde una descripción

```
Vos:  Creá un procedure llamado `DemoMCP` que reciba un
      ClienteId y haga un msg() con el nombre del cliente.

Claude:  [gx_create_procedure name="DemoMCP"
          source="parm(in:&ClienteId);
                  for each Cliente where ClienteId=&ClienteId
                      msg(ClienteNombre)
                  endfor"]
         Procedure creado. Backup en
         .gxmcp\backups\SampleKB\20260514_153012\..._create_proc_DemoMCP.bak
```

### 4) Optimizar un Procedure y aplicarlo

```
Vos:  Leé `CalcularTotal`, decime si se puede optimizar, y si sí,
      aplicá el cambio directo.

Claude:  [gx_read_object name="CalcularTotal"]
         Veo dos optimizaciones:
         1. El `for each` anidado puede fusionarse en un join — ahorra
            ~80% de fetches en KBs con mucha data.
         2. La validación `&Total > 0` está adentro del loop pero
            puede salir afuera.

         ¿Aplico los cambios? (te aviso antes con el diff)

Vos:  Sí, dale.

Claude:  [gx_update_object_code type="Procedure" name="CalcularTotal"
          new_source="<código optimizado>"]
         Aplicado. Backup en
         .gxmcp\backups\...update_Procedure_CalcularTotal_source.bak.
         Si querés revertir: RESTORE DATABASE FROM DISK='...' WITH REPLACE.
```

**Importante**: hoy `gx_update_object_code` soporta solo `Procedure`
(parts `source`/`rules`/`conditions`). Soporte para WebPanel Events,
Transaction Rules, etc. está pendiente.

### 5) Compilar lo que cambiaste

```
Vos:  Recién importé un XPZ con cambios en `CalcularDeuda`. Compilalo
      y decime si hay errores.

Claude:  [gx_build_object object_name="CalcularDeuda"]
         Build OK. Spec + Gen completados sin errores. Log:
         ...
```

### 6) Trabajar con varias KBs (modo global)

```
Vos:  ¿Qué KBs tengo disponibles?
Claude:  [gx_list_kbs]
         3 KBs: SampleKB (activa, GX17), SampleKB2 (GX17), SampleKB4 (GX17)

Vos:  Cambiate a SampleKB2 y decime cuántos webpanels tiene.
Claude:  [gx_switch_kb kb_name="SampleKB2"] [gx_list_objects type="WebPanel"]
         SampleKB2 cargada. Tiene 40 WebPanels.
```

---

## Permisos durante la ejecución

Hay **dos capas** de seguridad:

1. **Claude Code** te pregunta antes de ejecutar cualquier tool de escritura
   (`Allow once` / `Always allow` / `Deny`). Mientras estás aprendiendo,
   `Allow once` es lo más prudente.
2. **El Worker mismo** chequea autorización antes de tocar la KB:
   - Tools de escritura requieren `AllowWrite=true`
   - `gx_build_object` requiere `AllowBuild=true`

Si no están habilitados, el Worker devuelve un error sin tocar la KB
("Refusing: gx_create_procedure — Security.AllowWrite=false").

### Cómo habilitar escritura

**Por sesión (recomendado para empezar):**
```powershell
cd C:\KB\<tu-kb>
$env:GXGENIE_ALLOW_WRITE = "true"
$env:GXGENIE_ALLOW_BUILD = "true"
claude
```
Cuando cerrás esa terminal, las env vars desaparecen.

**Persistente para esa KB:** dropeá un `config.json` en la carpeta de la KB:
```json
{
  "Security": {
    "AllowWrite": true,
    "AllowBuild": true,
    "AuditLog": true
  }
}
```
El Worker mergea esto con el auto-detect — la KB sigue siendo la del cwd,
pero los flags ganan.

---

## Backup y audit

**Cada escritura genera un `.bak` automático antes** bajo:
```
<carpeta-KB>\.gxmcp\backups\<timestamp>\<dbname>__<tool>.bak
```
Para revertir: `RESTORE DATABASE <dbname> FROM DISK='<path>.bak' WITH REPLACE`.

**Cada operación queda registrada** en `<carpeta-KB>\.gxmcp\audit.log`:
```
2026-05-14 15:30:12 | WRITE   | gx_create_procedure   | DemoMCP  | SUCCESS | backup=...
```

---

## Troubleshooting

### "El MCP no aparece en `/mcp`"

1. Verificá que el `.mcp.json` existe: `Get-Content .\.mcp.json`
2. Verificá que la ruta del `command` apunta a un `.exe` que existe
3. Mirá el stderr del Gateway: `claude --debug`
4. Probá invocar el Gateway directo:
   ```
   C:\Proyectos\GxGenie\GxGenie.Gateway\bin\Release\net8.0\GxGenie.Gateway.exe --help
   ```

### "Tool failed: KB not found / no .gxw"

El Worker no detectó la KB. Causas posibles:
- No hay `.gxw` en la carpeta actual
- Claude se abrió desde otra carpeta (verificá con: *"¿Cuál es mi cwd?"*)
- Múltiples `.gxw` en la carpeta — el Worker usa el primero, mové los otros

### "Cannot open database … login failed"

La LocalDB se desatacheó (MSBuild lo hace tras cada `CloseKnowledgeBase`).
El Worker debería re-atacharla automáticamente al próximo SQL crudo,
pero si persiste:
```powershell
sqllocaldb start MSSQLLocalDB
```

### "Worker timeout"

El default es 120s. Para builds largos podés subirlo con un `config.json`
local: `"Worker": { "TimeoutSeconds": 600 }`.

---

## Lo que NO hace este MCP (hoy)

- **`gx_update_object_code` cubre sólo Procedure** — soporta los parts
  `source`, `rules` y `conditions` de Procedure. WebPanel events,
  Transaction rules, DataProvider source, etc. están pendientes.
- **Web panel layout** — leer el WebForm XML funciona; modificar layouts
  no está implementado.
- **Crear Transactions / agregar atributos** — pendiente.
- **Validación contra GX18** — el adapter está preparado pero sin
  instalación real de GX18 a la mano no se validó.
- **Operaciones de team server / GXserver** — fuera de scope.
