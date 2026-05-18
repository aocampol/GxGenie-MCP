<#
.SYNOPSIS
    Instalador idempotente del MCP Server para GeneXus (GxGenie).

.DESCRIPTION
    Detecta el entorno, compila Worker + Gateway, genera config.json y registra
    el Gateway en Claude Code. Re-ejecutarlo no rompe nada — solo actualiza lo
    que cambió.

.PARAMETER Uninstall
    Revierte la instalación: desregistra el MCP de Claude Code, borra binarios
    compilados y opcionalmente el config.json y la KB de prueba.

.PARAMETER GxSdkExtractDir
    Ruta donde extraer las DLLs del SDK de GeneXus si se provee un instalador
    (gxplatformsdk17u1.exe / gxplatformsdk18.exe). Default: C:\GxSDK<ver>.

.PARAMETER GxSdkInstaller
    Ruta opcional a un .exe del SDK de GeneXus para extracción automática.
    Si no se provee y el SDK no existe, se omite (se usa solo lo que ya hay
    en GX_PROGRAM_DIR).

.PARAMETER SkipBuild
    No compila los proyectos .NET (útil para re-registrar sin recompilar).

.PARAMETER SkipClaudeRegister
    No invoca 'claude mcp add'. Útil para validar el setup sin tocar Claude.

.PARAMETER Force
    Sobreescribe config.json y variables de entorno aunque ya existan.

.EXAMPLE
    .\setup.ps1
    Instalación interactiva estándar.

.EXAMPLE
    .\setup.ps1 -Uninstall
    Revierte todo.

.EXAMPLE
    .\setup.ps1 -GxSdkInstaller C:\Downloads\gxplatformsdk17u1.exe
    Instalación con extracción automática del SDK.
#>
[CmdletBinding()]
param(
    [switch]$Uninstall,
    [string]$GxSdkInstaller,
    [string]$GxSdkExtractDir,
    [switch]$SkipBuild,
    [switch]$SkipClaudeRegister,
    [switch]$Force,

    # Modo per-KB: dropea un .mcp.json en la carpeta de la KB indicada para que
    # Claude Code cargue el MCP "genexus" solo cuando se abre en esa carpeta.
    # Si se usa, NO registra globalmente (a menos que también pases -RegisterGlobal).
    [string]$InstallToKb,
    [switch]$RegisterGlobal,

    # Modo per-KB: por default el .mcp.json generado habilita writes y builds
    # (AllowWrite + AllowBuild) — el propósito del MCP es modificar la KB, no
    # solo leerla. Cada destructive op snapshotea el LocalDB a un .bak antes y
    # queda registrada en audit.log, así que la red de seguridad existe.
    # Pasá -ReadOnly si querés instalar el MCP en una KB para sólo inspeccionar
    # (sin riesgo de modificación ni necesidad de aprobar prompts destructivos).
    [switch]$ReadOnly,

    # Modo per-KB: opcional. Si se especifica, el .mcp.json va a inyectar
    # GXGENIE_CONFIG apuntando a este config.json — útil cuando querés que la
    # KB use settings de Security/backup compartidos en lugar del auto-detect
    # por carpeta. Si no se especifica, el Worker entra en modo auto-detect
    # by-cwd (lee la KB de la carpeta donde corre Claude Code).
    [string]$ConfigPath
)

$ErrorActionPreference = 'Stop'
$script:ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:LogFile = Join-Path $ProjectRoot 'setup_log.txt'
$script:Summary = [System.Collections.Generic.List[string]]::new()

# ---------------- helpers de logging ----------------

function Write-Log {
    param([string]$Message, [string]$Level = 'INFO')
    $ts = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $line = "[$ts][$Level] $Message"
    Add-Content -Path $LogFile -Value $line -Encoding UTF8
    $color = switch ($Level) {
        'OK'   { 'Green' }
        'WARN' { 'Yellow' }
        'FAIL' { 'Red' }
        'STEP' { 'Cyan' }
        default { 'Gray' }
    }
    Write-Host $line -ForegroundColor $color
}

function Add-Summary { param([string]$Line) $script:Summary.Add($Line) }

function Test-Admin {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($id)
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ---------------- detección ----------------

function Get-DotNet8Sdk {
    try {
        $sdks = & dotnet --list-sdks 2>$null
        if (-not $sdks) { return $null }
        $match = $sdks | Where-Object { $_ -match '^8\.\d+\.\d+' } | Select-Object -First 1
        return $match
    } catch { return $null }
}

function Get-GeneXusInstalls {
    $candidates = @(
        @{ Version='17U1'; Path='C:\Program Files (x86)\GeneXus\GeneXus17U1' },
        @{ Version='17U11'; Path='C:\Program Files (x86)\GeneXus\GeneXus17U11' },
        @{ Version='17'; Path='C:\Program Files (x86)\GeneXus\GeneXus17' },
        @{ Version='18'; Path='C:\Program Files (x86)\GeneXus\GeneXus18' },
        @{ Version='18U1'; Path='C:\Program Files (x86)\GeneXus\GeneXus18U1' }
    )
    $found = @()
    foreach ($c in $candidates) {
        if (Test-Path $c.Path) {
            $found += [PSCustomObject]@{
                Version = $c.Version
                InstallationPath = $c.Path
            }
        }
    }
    return $found
}

function Get-MsBuildExe {
    $paths = @(
        'C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe',
        'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe'
    )
    foreach ($p in $paths) { if (Test-Path $p) { return $p } }
    return $null
}

function Get-LocalDbInstance {
    try {
        $out = & sqllocaldb info 2>$null
        if (-not $out) { return $null }
        return ($out -split "`n" | Where-Object { $_ -match '^\S+$' } | Select-Object -First 1).Trim()
    } catch { return $null }
}

function Get-ClaudeCodeCli {
    $cmd = Get-Command claude -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = @(
        "$env:LOCALAPPDATA\Programs\claude-code\claude.exe",
        "$env:APPDATA\npm\claude.cmd"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    return $null
}

function Discover-KnowledgeBases {
    $roots = @('C:\KB', 'D:\KB', 'C:\GeneXus\KB')
    $kbs = @()
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        Get-ChildItem -Path $root -Filter '*.gxw' -Recurse -Depth 3 -ErrorAction SilentlyContinue | ForEach-Object {
            $connFile = Join-Path $_.DirectoryName 'knowledgebase.connection'
            $dbName = $null
            $version = '17'
            if (Test-Path $connFile) {
                try {
                    [xml]$xml = Get-Content $connFile -Raw
                    $dbName = $xml.ConnectionInformation.DBName
                } catch { }
            }
            if (-not $dbName) {
                $dbName = "GX_KB_$([IO.Path]::GetFileNameWithoutExtension($_.Name))"
            }
            # Best-effort detección de versión por path
            if ($_.FullName -match 'Gx?18') { $version = '18' }
            elseif ($_.FullName -match 'Gx?17') { $version = '17' }

            $kbs += [PSCustomObject]@{
                Name = [IO.Path]::GetFileNameWithoutExtension($_.Name)
                Path = $_.FullName
                DBName = $dbName
                Version = $version
            }
        }
    }
    return $kbs
}

# ---------------- pasos ----------------

function Install-DotNet8IfMissing {
    Write-Log 'Verificando .NET 8 SDK...' 'STEP'
    $sdk = Get-DotNet8Sdk
    if ($sdk) {
        Write-Log ".NET 8 SDK presente: $sdk" 'OK'
        Add-Summary ".NET 8 SDK: $sdk"
        return $true
    }
    Write-Log '.NET 8 SDK no encontrado.' 'WARN'
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
        Write-Log 'Instalando vía winget (Microsoft.DotNet.SDK.8)...'
        try {
            & winget install --id Microsoft.DotNet.SDK.8 --silent --accept-package-agreements --accept-source-agreements
            $sdk = Get-DotNet8Sdk
            if ($sdk) {
                Write-Log "Instalado: $sdk" 'OK'
                Add-Summary ".NET 8 SDK: instalado vía winget ($sdk)"
                return $true
            }
        } catch {
            Write-Log "winget falló: $_" 'WARN'
        }
    }
    Write-Log 'No se pudo instalar .NET 8 SDK automáticamente. Instálalo manualmente: https://dotnet.microsoft.com/download/dotnet/8.0' 'FAIL'
    return $false
}

function Extract-GxSdkIfNeeded {
    param([string]$Installer, [string]$Version, [string]$TargetDir)
    if (-not $Installer -or -not (Test-Path $Installer)) {
        Write-Log "SDK installer no provisto o inexistente — omitiendo extracción." 'WARN'
        return $false
    }
    if (Test-Path $TargetDir) {
        $count = (Get-ChildItem $TargetDir -Filter 'Artech.*.dll' -ErrorAction SilentlyContinue).Count
        if ($count -gt 10) {
            Write-Log "SDK ya extraído en $TargetDir ($count DLLs Artech.*) — omitiendo." 'OK'
            Add-Summary "GX_SDK_DIR: $TargetDir (ya existía)"
            return $true
        }
    }
    Write-Log "Extrayendo $Installer a $TargetDir..." 'STEP'
    New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null

    $sevenZip = Get-Command 7z -ErrorAction SilentlyContinue
    if (-not $sevenZip) {
        $sevenZipPath = "$env:ProgramFiles\7-Zip\7z.exe"
        if (Test-Path $sevenZipPath) { $sevenZip = $sevenZipPath }
    }
    if (-not $sevenZip) {
        Write-Log '7-Zip no disponible. Intentando winget install...' 'WARN'
        try {
            & winget install --id 7zip.7zip --silent --accept-package-agreements --accept-source-agreements
            $sevenZipPath = "$env:ProgramFiles\7-Zip\7z.exe"
            if (Test-Path $sevenZipPath) { $sevenZip = $sevenZipPath }
        } catch { }
    }
    if (-not $sevenZip) {
        Write-Log '7-Zip no disponible. Extraé el SDK manualmente a ' + $TargetDir 'FAIL'
        return $false
    }
    try {
        & $sevenZip x $Installer "-o$TargetDir" -y *.dll | Out-Null
        $count = (Get-ChildItem $TargetDir -Filter 'Artech.*.dll' -ErrorAction SilentlyContinue).Count
        if ($count -gt 0) {
            Write-Log "Extracción OK: $count DLLs en $TargetDir" 'OK'
            Add-Summary "GX_SDK_DIR: $TargetDir (extraído, $count DLLs)"
            return $true
        }
        Write-Log 'No se encontraron DLLs Artech.* tras extracción' 'FAIL'
        return $false
    } catch {
        Write-Log "Fallo extracción: $_" 'FAIL'
        return $false
    }
}

function Set-EnvironmentVariables {
    param([string]$GxProgramDir, [string]$GxSdkDir)
    Write-Log 'Configurando variables de entorno (User scope)...' 'STEP'
    foreach ($pair in @(
        @{ Name='GX_PROGRAM_DIR'; Value=$GxProgramDir },
        @{ Name='GX_SDK_DIR';     Value=$GxSdkDir }
    )) {
        if (-not $pair.Value) { continue }
        $current = [System.Environment]::GetEnvironmentVariable($pair.Name, 'User')
        if ($current -eq $pair.Value -and -not $Force) {
            Write-Log "$($pair.Name) ya está seteado a '$current'" 'OK'
        } else {
            [System.Environment]::SetEnvironmentVariable($pair.Name, $pair.Value, 'User')
            Write-Log "$($pair.Name) = $($pair.Value)" 'OK'
            Add-Summary "Env $($pair.Name) = $($pair.Value)"
        }
    }
}

function Build-Projects {
    if ($SkipBuild) {
        Write-Log '--SkipBuild: omitiendo compilación.' 'WARN'
        return $true
    }
    Write-Log 'Compilando GxGenie.Worker...' 'STEP'
    & dotnet build (Join-Path $ProjectRoot 'GxGenie.Worker\GxGenie.Worker.csproj') -c Release --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { Write-Log 'Build Worker FAIL' 'FAIL'; return $false }
    Write-Log 'GxGenie.Worker compilado' 'OK'

    Write-Log 'Compilando GxGenie.Gateway...' 'STEP'
    & dotnet build (Join-Path $ProjectRoot 'GxGenie.Gateway\GxGenie.Gateway.csproj') -c Release --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { Write-Log 'Build Gateway FAIL' 'FAIL'; return $false }
    Write-Log 'GxGenie.Gateway compilado' 'OK'

    Add-Summary 'Compilación: OK (Release)'
    return $true
}

function New-ConfigJson {
    param($GxInstalls, $KBs)
    $configPath = Join-Path $ProjectRoot 'config.json'
    if ((Test-Path $configPath) -and -not $Force) {
        Write-Log "config.json ya existe — preservando ('-Force' para sobreescribir)." 'OK'
        Add-Summary "config.json: ya existía (preservado)"
        return $configPath
    }

    $msbuild = Get-MsBuildExe
    if (-not $msbuild) { $msbuild = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe' }

    $sdkDir = $env:GX_SDK_DIR
    if (-not $sdkDir) { $sdkDir = 'C:\GxSDK17' }

    $gxArr = @($GxInstalls | ForEach-Object {
        @{
            Version = $_.Version
            InstallationPath = $_.InstallationPath
            SdkPath = $sdkDir
            MSBuildPath = $msbuild
        }
    })

    $kbArr = @($KBs | ForEach-Object {
        @{
            Name = $_.Name
            Path = $_.Path
            ConnectionString = "Server=(LocalDB)\MSSQLLocalDB;Database=$($_.DBName);Integrated Security=True;TrustServerCertificate=True;Connect Timeout=15"
            GeneXusVersion = $_.Version
        }
    })

    $activeKb = if ($kbArr.Count -gt 0) { $kbArr[0].Name } else { '' }

    $cfg = @{
        GeneXus = $gxArr
        KnowledgeBases = $kbArr
        ActiveKB = $activeKb
        Worker = @{
            ExecutablePath = (Join-Path $ProjectRoot 'GxGenie.Worker\bin\Release\net8.0\GxGenie.Worker.exe')
            TimeoutSeconds = 120
        }
        Security = @{
            AllowWrite = $false
            AllowBuild = $false
            AuditLog = $true
            AuditLogPath = (Join-Path $ProjectRoot 'audit.log')
            BackupRoot = (Join-Path $ProjectRoot 'backups')
        }
    }

    $json = $cfg | ConvertTo-Json -Depth 6
    Set-Content -Path $configPath -Value $json -Encoding UTF8
    Write-Log "config.json generado con $($kbArr.Count) KB(s); ActiveKB=$activeKb" 'OK'
    Add-Summary "config.json: $($kbArr.Count) KB(s), activa=$activeKb, AllowWrite=false (editar manualmente)"
    return $configPath
}

function Register-WithClaudeCode {
    param([string]$GatewayExe)
    if ($SkipClaudeRegister) {
        Write-Log '--SkipClaudeRegister: omitiendo registro en Claude Code.' 'WARN'
        return
    }
    $claude = Get-ClaudeCodeCli
    if (-not $claude) {
        Write-Log 'Claude Code CLI no encontrado en PATH — registralo manualmente con: claude mcp add --transport stdio genexus -- "<exe>"' 'WARN'
        return
    }
    Write-Log 'Registrando MCP "genexus" en Claude Code...' 'STEP'
    try {
        # Si ya existe lo removemos primero (idempotencia)
        & $claude mcp remove genexus 2>$null | Out-Null
    } catch { }
    try {
        & $claude mcp add --transport stdio genexus -- $GatewayExe
        Write-Log "MCP genexus registrado: $GatewayExe" 'OK'
        Add-Summary "Claude Code: MCP 'genexus' registrado"
    } catch {
        Write-Log "Fallo al registrar MCP: $_" 'FAIL'
    }
}

function Install-McpJsonToKb {
    param(
        [string]$KbFolder,
        # Si $true, NO se inyectan GXGENIE_ALLOW_WRITE / GXGENIE_ALLOW_BUILD;
        # el Worker arranca read-only (el comportamiento "safe by default" original).
        [bool]$ReadOnly = $false,
        # Si se pasa, se inyecta GXGENIE_CONFIG apuntando a este archivo.
        # Útil para que el .mcp.json use un config.json compartido en lugar del auto-detect.
        [string]$ExplicitConfigPath = ''
    )
    if (-not (Test-Path $KbFolder)) {
        Write-Log "Carpeta de KB no existe: $KbFolder" 'FAIL'
        return $false
    }
    $gxw = Get-ChildItem $KbFolder -Filter '*.gxw' -ErrorAction SilentlyContinue
    if (-not $gxw) {
        Write-Log "No se encontró ningún .gxw en $KbFolder — ¿es realmente una carpeta de KB?" 'WARN'
    } else {
        Write-Log "KB detectada: $($gxw[0].Name)" 'OK'
    }

    $gatewayExe = Join-Path $ProjectRoot 'GxGenie.Gateway\bin\Release\net8.0\GxGenie.Gateway.exe'
    if (-not (Test-Path $gatewayExe)) {
        Write-Log "Gateway no compilado en $gatewayExe. Corré sin -SkipBuild primero." 'FAIL'
        return $false
    }

    # El env del .mcp.json es CRÍTICO: Claude Code spawnea el MCP server con
    # environment limpio (no hereda env vars del shell), así que cualquier
    # configuración que el Worker necesite por env var tiene que vivir acá.
    # Sin este bloque, el modo auto-detect-by-cwd defaultea a AllowWrite=false
    # ignorando cualquier config.json global con AllowWrite=true.
    $envBlock = [ordered]@{}
    if (-not $ReadOnly) {
        $envBlock['GXGENIE_ALLOW_WRITE'] = 'true'
        $envBlock['GXGENIE_ALLOW_BUILD'] = 'true'
    }
    if (-not [string]::IsNullOrWhiteSpace($ExplicitConfigPath)) {
        $envBlock['GXGENIE_CONFIG'] = $ExplicitConfigPath
    }

    $genexusEntry = [ordered]@{
        command = $gatewayExe
        args = @()
    }
    if ($envBlock.Count -gt 0) {
        $genexusEntry['env'] = $envBlock
    }

    $mcpFile = Join-Path $KbFolder '.mcp.json'
    $mcp = [ordered]@{
        mcpServers = [ordered]@{
            genexus = $genexusEntry
        }
    }
    $json = $mcp | ConvertTo-Json -Depth 6
    Set-Content -Path $mcpFile -Value $json -Encoding UTF8
    Write-Log ".mcp.json instalado en $mcpFile" 'OK'
    if ($ReadOnly) {
        Write-Log '  Mode: read-only (writes y builds NO habilitados — agregá -ReadOnly:$false o sacá el flag para habilitarlos).' 'INFO'
    } else {
        Write-Log '  Mode: write-enabled (GXGENIE_ALLOW_WRITE + GXGENIE_ALLOW_BUILD = true). Cada op destructiva snapshotea LocalDB a .bak antes.' 'INFO'
    }
    if (-not [string]::IsNullOrWhiteSpace($ExplicitConfigPath)) {
        Write-Log "  Config path explícito: $ExplicitConfigPath" 'INFO'
    }
    Add-Summary ".mcp.json: $mcpFile"

    # Para que el MCP funcione, también necesitamos que GeneXus esté en una ruta detectable.
    # Avisamos si GX_PROGRAM_DIR no está seteado.
    if (-not $env:GX_PROGRAM_DIR -and -not [Environment]::GetEnvironmentVariable('GX_PROGRAM_DIR','User')) {
        Write-Log "GX_PROGRAM_DIR no está seteado. El Worker intentará paths estándar, pero conviene setearlo." 'WARN'
    }

    return $true
}

function Test-McpEndpoint {
    param([string]$WorkerExe, [string]$ConfigPath)
    Write-Log 'Smoke test: invocando gx_kb_info vía Worker...' 'STEP'
    $env:GXGENIE_CONFIG = $ConfigPath
    try {
        $out = & $WorkerExe --once '{"tool":"gx_kb_info"}' 2>&1
        if ($out -match '"success":true') {
            Write-Log 'Smoke test OK' 'OK'
            Add-Summary 'Smoke test: OK'
        } else {
            Write-Log "Smoke test devolvió respuesta no esperada: $out" 'WARN'
            Add-Summary 'Smoke test: WARN (revisar setup_log.txt)'
        }
    } catch {
        Write-Log "Smoke test FAIL: $_" 'WARN'
    }
}

# ---------------- uninstall ----------------

function Invoke-Uninstall {
    Write-Log '=== UNINSTALL GxGenie ===' 'STEP'

    $claude = Get-ClaudeCodeCli
    if ($claude) {
        try {
            & $claude mcp remove genexus 2>$null
            Write-Log 'MCP genexus removido de Claude Code' 'OK'
        } catch {
            Write-Log "No se pudo remover MCP de Claude: $_" 'WARN'
        }
    }

    foreach ($dir in @('GxGenie.Worker\bin', 'GxGenie.Worker\obj', 'GxGenie.Gateway\bin', 'GxGenie.Gateway\obj')) {
        $p = Join-Path $ProjectRoot $dir
        if (Test-Path $p) {
            Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue
            Write-Log "Borrado: $p" 'OK'
        }
    }

    foreach ($var in @('GX_PROGRAM_DIR','GX_SDK_DIR')) {
        $cur = [System.Environment]::GetEnvironmentVariable($var, 'User')
        if ($cur) {
            [System.Environment]::SetEnvironmentVariable($var, $null, 'User')
            Write-Log "Env $var removido (estaba: $cur)" 'OK'
        }
    }

    Write-Log "config.json, audit.log, backups/ preservados — borrar manualmente si querés." 'WARN'
    Write-Log '=== UNINSTALL COMPLETO ===' 'OK'
}

# ---------------- main ----------------

function Invoke-Install {
    Write-Log '=== INSTALL GxGenie — Fase 4 ===' 'STEP'
    Write-Log "Project root: $ProjectRoot"
    Write-Log "Log file: $LogFile"

    if (Test-Admin) {
        Write-Log 'Corriendo como administrador (no necesario, pero OK).' 'WARN'
    }

    # 1) .NET 8 SDK
    if (-not (Install-DotNet8IfMissing)) {
        Write-Log 'Abortado por falta de .NET 8 SDK.' 'FAIL'
        return $false
    }

    # 2) Detectar GeneXus
    $gxInstalls = Get-GeneXusInstalls
    if ($gxInstalls.Count -eq 0) {
        Write-Log 'No se encontró ninguna instalación de GeneXus en las rutas estándar. Editá config.json manualmente.' 'WARN'
    } else {
        foreach ($g in $gxInstalls) {
            Write-Log "GeneXus $($g.Version) — $($g.InstallationPath)" 'OK'
            Add-Summary "GeneXus $($g.Version): $($g.InstallationPath)"
        }
    }

    # 3) MSBuild .NET Framework
    $msb = Get-MsBuildExe
    if ($msb) { Write-Log "MSBuild .NET 4.x: $msb" 'OK' } else { Write-Log 'MSBuild .NET 4.x no localizado' 'WARN' }

    # 4) LocalDB
    $localDb = Get-LocalDbInstance
    if ($localDb) { Write-Log "LocalDB instancia detectada: $localDb" 'OK'; Add-Summary "LocalDB: $localDb" }
    else { Write-Log 'LocalDB no detectado — viene con SQL Server Express. Instalá si las KBs son locales.' 'WARN' }

    # 5) Claude Code
    $claude = Get-ClaudeCodeCli
    if ($claude) { Write-Log "Claude Code CLI: $claude" 'OK' } else { Write-Log 'Claude Code CLI no encontrado en PATH' 'WARN' }

    # 6) SDK extraction si se pidió
    if ($GxSdkInstaller) {
        $ver = if ($GxSdkInstaller -match '18') { '18' } else { '17' }
        if (-not $GxSdkExtractDir) { $GxSdkExtractDir = "C:\GxSDK$ver" }
        Extract-GxSdkIfNeeded -Installer $GxSdkInstaller -Version $ver -TargetDir $GxSdkExtractDir | Out-Null
        Set-EnvironmentVariables -GxProgramDir $gxInstalls[0].InstallationPath -GxSdkDir $GxSdkExtractDir
    } elseif ($gxInstalls.Count -gt 0) {
        $sdkDir = if ($env:GX_SDK_DIR) { $env:GX_SDK_DIR } else { 'C:\GxSDK17' }
        Set-EnvironmentVariables -GxProgramDir $gxInstalls[0].InstallationPath -GxSdkDir $sdkDir
    }

    # 7) Build
    if (-not (Build-Projects)) {
        Write-Log 'Build falló — revisá los logs de dotnet.' 'FAIL'
        return $false
    }

    # 8) Discover KBs y generar config.json
    Write-Log 'Buscando KBs en C:\KB, D:\KB, C:\GeneXus\KB...' 'STEP'
    $kbs = Discover-KnowledgeBases
    if ($kbs.Count -eq 0) {
        Write-Log 'No se encontraron KBs (.gxw). Editá config.json manualmente.' 'WARN'
    } else {
        foreach ($kb in $kbs) { Write-Log "KB: $($kb.Name) — $($kb.Path)" 'OK' }
    }

    $configPath = New-ConfigJson -GxInstalls $gxInstalls -KBs $kbs

    # 9) Registrar Gateway en Claude Code
    $gatewayExe = Join-Path $ProjectRoot 'GxGenie.Gateway\bin\Release\net8.0\GxGenie.Gateway.exe'
    if (Test-Path $gatewayExe) {
        Register-WithClaudeCode -GatewayExe $gatewayExe
    } else {
        Write-Log "Gateway exe no encontrado en $gatewayExe — saltando registro." 'WARN'
    }

    # 10) Smoke test
    $workerExe = Join-Path $ProjectRoot 'GxGenie.Worker\bin\Release\net8.0\GxGenie.Worker.exe'
    if ((Test-Path $workerExe) -and (Test-Path $configPath) -and $kbs.Count -gt 0) {
        Test-McpEndpoint -WorkerExe $workerExe -ConfigPath $configPath
    }

    Write-Log '=== INSTALL COMPLETO ===' 'OK'
    return $true
}

# ---------------- entry ----------------

"" | Out-File -FilePath $LogFile -Encoding UTF8

if ($Uninstall) {
    Invoke-Uninstall
    return
}

if ($InstallToKb) {
    Write-Log "=== INSTALL per-KB en $InstallToKb ===" 'STEP'
    if (-not (Install-DotNet8IfMissing)) { Write-Log 'Abortado por falta de .NET 8 SDK.' 'FAIL'; return }
    if (-not $SkipBuild) {
        if (-not (Build-Projects)) { Write-Log 'Build falló.' 'FAIL'; return }
    }
    $ok = Install-McpJsonToKb -KbFolder $InstallToKb -ReadOnly:$ReadOnly -ExplicitConfigPath $ConfigPath
    if ($ok -and $RegisterGlobal) {
        $gatewayExe = Join-Path $ProjectRoot 'GxGenie.Gateway\bin\Release\net8.0\GxGenie.Gateway.exe'
        Register-WithClaudeCode -GatewayExe $gatewayExe
    }
    Write-Host ""
    Write-Host "================ RESUMEN ================" -ForegroundColor Cyan
    foreach ($s in $Summary) { Write-Host "  - $s" }
    Write-Host ""
    if ($ok) {
        Write-Host "Listo. Para usar el MCP:" -ForegroundColor Green
        Write-Host "  cd `"$InstallToKb`""
        Write-Host "  claude"
        Write-Host "Claude detectará el .mcp.json y cargará 'genexus' auto-bindeado a esa KB."
    }
    return
}

$ok = Invoke-Install
Write-Host ""
Write-Host "================ RESUMEN ================" -ForegroundColor Cyan
foreach ($s in $Summary) { Write-Host "  - $s" }
Write-Host ""
Write-Host "Log completo: $LogFile" -ForegroundColor Gray
Write-Host ""
if ($ok) {
    Write-Host "Próximos pasos:" -ForegroundColor Green
    Write-Host "  1. Editá config.json para habilitar Security.AllowWrite / AllowBuild si los necesitás."
    Write-Host "  2. Abrí Claude Code y ejecutá '/mcp' para confirmar que 'genexus' aparece."
    Write-Host "  3. Probá: 'Listame las KBs disponibles' (usa gx_list_kbs)."
    Write-Host ""
    Write-Host "Modo per-KB (alternativa):" -ForegroundColor Cyan
    Write-Host "  .\setup.ps1 -InstallToKb C:\KB\<tu-kb>"
    Write-Host "  → dropea un .mcp.json en esa carpeta, sin registro global."
} else {
    Write-Host "Setup incompleto — revisá los mensajes FAIL arriba o $LogFile" -ForegroundColor Red
}
