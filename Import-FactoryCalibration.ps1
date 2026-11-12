param([Parameter(Mandatory=$true)][string]$DestinationDirectory)
$ErrorActionPreference = 'Stop'

function Read-FactoryLut([string]$Path, [string]$Mode, [string]$Serial) {
    $settings = New-Object System.Xml.XmlReaderSettings
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.MaxCharactersInDocument = 1048576
    $reader = [System.Xml.XmlReader]::Create($Path, $settings)
    try {
        $document = New-Object System.Xml.XmlDocument
        $document.XmlResolver = $null
        $document.Load($reader)
    } finally {
        $reader.Dispose()
    }
    $root = $document.DocumentElement
    if ($root.Name -cne 'DisplayEngineConfig' -or
        $root.SelectSingleNode('Mode').InnerText.Trim() -cne $Mode -or
        $root.SelectSingleNode('DUTInfo_Feature/DUT_SN').InnerText.Trim() -ne $Serial) {
        throw 'The factory LUT does not identify this device and color mode.'
    }

    $sampleText = $root.SelectSingleNode('GMP_Feature').InnerText.Trim()
    if ($sampleText.EndsWith(',')) { $sampleText = $sampleText.Substring(0, $sampleText.Length - 1) }
    $samples = $sampleText.Split(',')
    if ($samples.Count -ne (17 * 17 * 17 * 3)) {
        throw 'The factory LUT does not contain a 17-point RGB cube.'
    }
    $bytes = [byte[]]::new($samples.Count * 8)
    for ($i = 0; $i -lt $samples.Count; $i++) {
        $sample = 0
        if (-not [int]::TryParse($samples[$i].Trim(), [ref]$sample) -or $sample -lt 0 -or $sample -gt 4095) {
            throw 'The factory LUT contains a value outside the 12-bit range.'
        }
        [Buffer]::BlockCopy([BitConverter]::GetBytes($sample / 4095.0), 0, $bytes, $i * 8, 8)
    }
    return ,$bytes
}

function Read-FactoryIcc([string]$Path) {
    $file = Get-Item -LiteralPath $Path
    if ($file.Length -lt 132 -or $file.Length -gt 4194304) {
        throw 'Unexpected factory ICC profile size.'
    }
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    $ascii = [Text.Encoding]::ASCII
    if ($ascii.GetString($bytes, 36, 4) -cne 'acsp' -or
        $ascii.GetString($bytes, 12, 4) -cne 'mntr' -or
        $ascii.GetString($bytes, 16, 4) -cne 'RGB ') {
        throw 'The factory ICC is not an RGB display profile.'
    }
    $length = [BitConverter]::ToUInt32([byte[]]@($bytes[3], $bytes[2], $bytes[1], $bytes[0]), 0)
    if ($length -ne $bytes.Length) {
        throw 'The factory ICC header does not match the file size.'
    }
    return ,$bytes
}

function Import-FactoryFiles([string]$ColorDirectory, [string]$Serial, [string]$Destination) {
    if ($Serial -notmatch '\A[A-Za-z0-9-]{6,64}\z') {
        return 'Unsupported'
    }
    $profiles = @('HonorNative.icc', 'HonorsRGB.icc', 'HonorP3.icc')
    $xmlFiles = @{
        sRGB = Join-Path $ColorDirectory ($Serial + '_display_factory_calibration_sRGB.xml')
        P3 = Join-Path $ColorDirectory ($Serial + '_display_factory_calibration_P3.xml')
    }
    $profilePaths = $profiles | ForEach-Object { Join-Path $ColorDirectory $_ }
    foreach ($path in @($xmlFiles.Values) + @($profilePaths)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            return 'Missing'
        }
    }

    # Validate the complete set before copying anything into the backend.
    $data = [ordered]@{}
    foreach ($name in $profiles) {
        $data[$name] = Read-FactoryIcc (Join-Path $ColorDirectory $name)
    }
    foreach ($mode in @('sRGB', 'P3')) {
        $data[$mode + '.lut'] = Read-FactoryLut $xmlFiles[$mode] $mode $Serial
    }
    foreach ($name in $data.Keys) {
        $path = Join-Path $Destination $name
        if (Test-Path -LiteralPath $path) {
            $existing = [IO.File]::ReadAllBytes($path)
            if ([Convert]::ToBase64String($existing) -cne [Convert]::ToBase64String($data[$name])) {
                return 'Conflict'
            }
        }
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($name in $data.Keys) {
        $path = Join-Path $Destination $name
        if (Test-Path -LiteralPath $path) { continue }
        $temporary = Join-Path $Destination ([guid]::NewGuid().ToString('N') + '.tmp')
        try {
            [IO.File]::WriteAllBytes($temporary, $data[$name])
            [IO.File]::Move($temporary, $path)
        } finally {
            if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
        }
    }
    return 'Factory'
}

function Import-LocalFactoryCalibration([string]$Destination) {
    $computer = Get-CimInstance -ClassName Win32_ComputerSystem -OperationTimeoutSec 5
    if ($computer.Model -ne 'DRA-XX') {
        return 'Unsupported'
    }
    # This is the internal panel family verified with this calibration format.
    $internal = @(Get-CimInstance -Namespace root/wmi -ClassName WmiMonitorConnectionParams -OperationTimeoutSec 5 |
        Where-Object { $_.Active -and $_.VideoOutputTechnology -eq 2147483648 })
    if ($internal.Count -ne 1 -or $internal[0].InstanceName -notlike 'DISPLAY\TMA2024\*') {
        return 'Unsupported'
    }
    $bios = Get-CimInstance -ClassName Win32_BIOS -OperationTimeoutSec 5
    $system = [Environment]::GetFolderPath([Environment+SpecialFolder]::System)
    return Import-FactoryFiles (Join-Path $system 'spool\drivers\color') ([string]$bios.SerialNumber).Trim() $Destination
}

try {
    Import-LocalFactoryCalibration $DestinationDirectory
} catch {
    'Invalid'
}
