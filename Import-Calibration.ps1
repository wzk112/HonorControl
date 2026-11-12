param([Parameter(Mandatory=$true)][string]$SourceDirectory,[string]$DestinationDirectory=(Join-Path $PSScriptRoot 'Backend'))
$ErrorActionPreference='Stop'
$source=[IO.Path]::GetFullPath($SourceDirectory)
$destination=[IO.Path]::GetFullPath($DestinationDirectory)
$files=@('HonorNative.icc','HonorsRGB.icc','HonorP3.icc','sRGB.lut','P3.lut')
foreach($file in $files){if(-not(Test-Path -LiteralPath (Join-Path $source $file) -PathType Leaf)){throw 'Missing calibration file: '+$file}}
foreach($file in @('sRGB.lut','P3.lut')){
    $bytes=[IO.File]::ReadAllBytes((Join-Path $source $file))
    if($bytes.Length -ne 117912){throw 'Invalid 17-point LUT length: '+$file}
    for($offset=0;$offset -lt $bytes.Length;$offset+=8){$value=[BitConverter]::ToDouble($bytes,$offset);if([double]::IsNaN($value) -or [double]::IsInfinity($value) -or $value -lt 0 -or $value -gt 1){throw 'Invalid LUT values: '+$file}}
}
foreach($file in @('HonorNative.icc','HonorsRGB.icc','HonorP3.icc')){
    $bytes=[IO.File]::ReadAllBytes((Join-Path $source $file))
    if($bytes.Length -lt 128 -or [Text.Encoding]::ASCII.GetString($bytes,36,4) -ne 'acsp'){throw 'Invalid ICC profile: '+$file}
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach($file in $files){Copy-Item -LiteralPath (Join-Path $source $file) -Destination (Join-Path $destination $file) -Force}
Write-Host 'Imported local device calibration. Confirm the source belongs to this physical panel before switching color modes.'
