param(
    [Parameter(Mandatory = $true)][string]$LogPath,
    [int]$GuestMode = 0
)

$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$script:failed = $false

function Log([string]$m) {
    [IO.File]::AppendAllText($LogPath, $m + "`r`n", $utf8)
}

function Step([string]$title, [scriptblock]$body) {
    try {
        & $body
        Log ("[OK] " + $title)
    }
    catch {
        Log ("[ПОМИЛКА] " + $title + ": " + $_.Exception.Message)
        $script:failed = $true
    }
}

function EnsureSids([string]$name, [string]$value, [string[]]$need) {
    $items = @($value.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $sids = @($items | ForEach-Object { ToSid $_ })
    foreach ($n in $need) {
        if ($sids -notcontains $n) { $items += '*' + $n }
    }
    return $name + ' = ' + ($items -join ',')
}

function ToSid([string]$x) {
    if ($x.StartsWith('*')) { return $x.Substring(1) }
    try {
        return (New-Object Security.Principal.NTAccount($x)).Translate([Security.Principal.SecurityIdentifier]).Value
    }
    catch { return $x }
}

$lsa = 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa'
$ws  = 'HKLM:\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters'

# ---------------- Пункт 1: налаштування комп'ютера ----------------

Step 'Мережу переведено в профіль «Приватна»' {
    Get-NetConnectionProfile |
        Where-Object { $_.NetworkCategory -eq 'Public' } |
        Set-NetConnectionProfile -NetworkCategory Private
}

Step 'Мережні служби запущено' {
    foreach ($s in 'fdPHost','FDResPub','SSDPSRV','upnphost','LanmanServer','LanmanWorkstation','lmhosts') {
        & sc.exe config $s start= auto | Out-Null
        & sc.exe start $s | Out-Null
    }
}

Step 'SMB2/SMB3 увімкнено' {
    Set-SmbServerConfiguration -EnableSMB2Protocol $true -Force -Confirm:$false
}

Step 'Правила брандмауера для приватної мережі увімкнено' {
    foreach ($g in '@FirewallAPI.dll,-32752','@FirewallAPI.dll,-28502') {
        Get-NetFirewallRule -Group $g -ErrorAction SilentlyContinue |
            Where-Object { $_.Profile -ne 'Public' } |
            Set-NetFirewallRule -Enabled True
    }
}

# ---------------- Віддалений робочий стіл (RDP) ----------------

$osCaption = ''
$sku = 0
try {
    $os = Get-CimInstance Win32_OperatingSystem
    $osCaption = $os.Caption
    $sku = [int]$os.OperatingSystemSKU
} catch { }
Log ('    Система: ' + $osCaption)

if ($sku -in 98, 99, 100, 101) {
    # Windows Home не може приймати вхідні підключення RDP
    Log '[УВАГА] Віддалений робочий стіл не увімкнено: редакція Windows Home не приймає вхідні підключення (потрібна Pro, Enterprise або Education).'
}
else {
    Step 'Віддалений робочий стіл увімкнено' {
        Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -Value 0 -Type DWord
        Set-Service -Name TermService -StartupType Automatic
        if ((Get-Service -Name TermService).Status -ne 'Running') { Start-Service -Name TermService }
    }

    Step 'Брандмауер: порт 3389 відкрито для приватної та доменної мережі' {
        Get-NetFirewallRule -Group '@FirewallAPI.dll,-28752' -ErrorAction SilentlyContinue |
            Where-Object { $_.Profile -ne 'Public' } |
            Set-NetFirewallRule -Enabled True

        # Власне правило на випадок, якщо стандартні правила відсутні або вимкнені політикою
        Get-NetFirewallRule -DisplayName 'LanShare RDP' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        New-NetFirewallRule -DisplayName 'LanShare RDP' -Direction Inbound -Protocol TCP -LocalPort 3389 `
            -Action Allow -Profile Private,Domain | Out-Null
    }

    Step 'Порт 3389 приймає підключення' {
        $listening = $false
        for ($i = 0; $i -lt 6 -and -not $listening; $i++) {
            $listening = [bool](Get-NetTCPConnection -LocalPort 3389 -State Listen -ErrorAction SilentlyContinue)
            if (-not $listening) { Start-Sleep -Seconds 1 }
        }
        if (-not $listening) {
            Restart-Service -Name TermService -Force
            Start-Sleep -Seconds 3
            $listening = [bool](Get-NetTCPConnection -LocalPort 3389 -State Listen -ErrorAction SilentlyContinue)
        }
        if (-not $listening) { throw 'Служба віддаленого стола запущена, але не слухає порт 3389.' }
    }

    Log '    Підключатися можуть адміністратори та члени групи «Користувачі віддаленого робочого столу». Обліковий запис без пароля допускається лише при увімкненому «вході без пароля».'
}

# Модель входу «звичайна»: при «лише гість» (forceguest=1) віддалений стіл не пускає жодного користувача
Step 'Модель входу: звичайна (потрібна для віддаленого стола)' {
    Set-ItemProperty -Path $lsa -Name forceguest -Value 0 -Type DWord
}

if ($GuestMode -eq 1) {
    Step 'Вхід без пароля дозволено' {
        # Облікові записи без пароля можуть входити через мережу і віддалений стіл
        Set-ItemProperty -Path $lsa -Name LimitBlankPasswordUse -Value 0 -Type DWord
        Set-ItemProperty -Path $ws  -Name AllowInsecureGuestAuth -Value 1 -Type DWord
        # Windows 11 24H2+: без цього гостьові підключення до інших ПК блокуються (потрібен підпис SMB)
        Set-SmbClientConfiguration -EnableInsecureGuestLogons $true -RequireSecuritySignature $false -Force -Confirm:$false
        # Сервер: гість не може підписувати SMB-трафік, тому підпис не має бути обов'язковим
        Set-SmbServerConfiguration -RequireSecuritySignature $false -Force -Confirm:$false
        # Якщо політика Windows перекриває налаштування клієнта, оновлюємо й її
        $pol = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\LanmanWorkstation'
        if (Test-Path $pol) { Set-ItemProperty -Path $pol -Name AllowInsecureGuestAuth -Value 1 -Type DWord }
        # Невідомі користувачі потрапляють під обліковий запис Гість
        Get-LocalUser | Where-Object { $_.SID.Value -like '*-501' } | Enable-LocalUser
    }
}
else {
    Step 'Вхід без пароля вимкнено: потрібні облікові записи з паролем' {
        Set-ItemProperty -Path $lsa -Name LimitBlankPasswordUse -Value 1 -Type DWord
        Set-ItemProperty -Path $ws  -Name AllowInsecureGuestAuth -Value 0 -Type DWord
        Set-SmbClientConfiguration -EnableInsecureGuestLogons $false -Force -Confirm:$false
        Get-LocalUser | Where-Object { $_.SID.Value -like '*-501' } | Disable-LocalUser
    }
}

# ---------------- Пункт 8: права мережевого входу ----------------

Step 'Права мережевого входу налаштовано' {
    $removeGuest = ($GuestMode -eq 1)
    $inf = Join-Path $env:TEMP 'lanshare_sec.inf'
    $sdb = Join-Path $env:TEMP 'lanshare_sec.sdb'
    Remove-Item $inf, $sdb -ErrorAction SilentlyContinue

    & secedit.exe /export /cfg $inf /areas USER_RIGHTS | Out-Null
    if (-not (Test-Path $inf)) { throw 'Не вдалося прочитати політику безпеки.' }

    $result = foreach ($l in (Get-Content $inf -Encoding Unicode)) {
        if ($removeGuest -and $l -match '^SeDenyNetworkLogonRight\s*=(.*)$') {
            $keep = @($Matches[1].Split(',') | ForEach-Object { $_.Trim() } | Where-Object {
                if (-not $_) { return $false }
                $sid = ToSid $_
                -not ($sid -like 'S-1-5-21-*-501' -or $sid -eq 'S-1-5-32-546')
            })
            'SeDenyNetworkLogonRight = ' + ($keep -join ',')
        }
        elseif ($l -match '^SeNetworkLogonRight\s*=(.*)$') {
            EnsureSids 'SeNetworkLogonRight' $Matches[1] @('S-1-1-0', 'S-1-5-32-544', 'S-1-5-32-545')
        }
        elseif ($l -match '^SeRemoteInteractiveLogonRight\s*=(.*)$') {
            EnsureSids 'SeRemoteInteractiveLogonRight' $Matches[1] @('S-1-5-32-544', 'S-1-5-32-555')
        }
        else { $l }
    }

    Set-Content -Path $inf -Value $result -Encoding Unicode
    & secedit.exe /configure /db $sdb /cfg $inf /areas USER_RIGHTS | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'secedit завершився з помилкою.' }
}

try {
    Get-NetConnectionProfile | ForEach-Object {
        Log ('    Мережа «' + $_.Name + '»: ' + $_.NetworkCategory)
    }
} catch { }

if ($script:failed) { exit 1 } else { exit 0 }
