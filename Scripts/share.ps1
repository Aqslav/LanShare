param(
    [Parameter(Mandatory = $true)][string]$LogPath,
    [Parameter(Mandatory = $true)][string]$PayloadPath
)

$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Log([string]$m) {
    [IO.File]::AppendAllText($LogPath, $m + "`r`n", $utf8)
}

# Коди завершення: 0 - успіх, 1 - помилка, 2 - папку не знайдено, 3 - некоректне ім'я ресурсу
try {
    $p = Get-Content -Raw -Encoding UTF8 -LiteralPath $PayloadPath | ConvertFrom-Json
    Log ('Дія: ' + $p.Action + '; ім''я: ' + $p.Name + '; шлях: ' + $p.Path + '; доступ: ' + $p.Access)

    if ($p.Action -eq 'Remove') {
        if (Get-SmbShare -Name $p.Name -ErrorAction SilentlyContinue) {
            Remove-SmbShare -Name $p.Name -Force
        }
        Log 'Спільний доступ скасовано.'
        exit 0
    }

    if (-not (Test-Path -LiteralPath $p.Path -PathType Container)) {
        Log 'Папку не знайдено.'
        exit 2
    }
    if ([string]::IsNullOrWhiteSpace($p.Name) -or $p.Name -match '[\\/:*?"<>|]') {
        Log 'Некоректне ім''я ресурсу.'
        exit 3
    }

    # Група «Усі» (за SID, тож працює в будь-якій мові Windows)
    $everyone = (New-Object Security.Principal.SecurityIdentifier 'S-1-1-0').Translate([Security.Principal.NTAccount]).Value

    # Права NTFS
    $perm = '(OI)(CI)M'
    if ($p.Access -eq 'Read') { $perm = '(OI)(CI)RX' }
    & icacls.exe $p.Path /grant ('*S-1-1-0:' + $perm) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'icacls завершився з помилкою.' }

    # Спільний ресурс (якщо з таким іменем уже є, перестворюємо)
    if (Get-SmbShare -Name $p.Name -ErrorAction SilentlyContinue) {
        Remove-SmbShare -Name $p.Name -Force
    }
    if ($p.Access -eq 'Read') {
        New-SmbShare -Name $p.Name -Path $p.Path -ReadAccess $everyone | Out-Null
    }
    else {
        New-SmbShare -Name $p.Name -Path $p.Path -FullAccess $everyone | Out-Null
    }

    Log 'Папку відкрито для мережі.'
    exit 0
}
catch {
    Log ('Помилка: ' + $_.Exception.Message)
    exit 1
}
