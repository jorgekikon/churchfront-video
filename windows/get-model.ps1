# Fetches auto tracking's body-pose model into windows\models\rtmo-s.onnx (about 36 MB to
# download, 40 MB unpacked). build.ps1 then copies it next to ChurchfrontVideo.exe in dist.
#
# The model is RTMO-s from OpenMMLab (mmpose, Apache 2.0 code). It isn't kept in this repo:
# its weights were trained on "body7", a mix of seven public datasets, some of which have
# non-commercial terms. Fine for a church's own booth; check the terms before redistributing it.
#
#   .\get-model.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$url = 'https://download.openmmlab.com/mmpose/v1/projects/rtmo/onnx_sdk/rtmo-s_8xb32-600e_body7-640x640-dac2bf74_20231211.zip'
$dir = Join-Path $PSScriptRoot 'models'
$out = Join-Path $dir 'rtmo-s.onnx'
if (Test-Path $out) { "Already there: $out"; return }
New-Item -ItemType Directory -Force $dir | Out-Null
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("rtmo-" + [guid]::NewGuid())
New-Item -ItemType Directory $tmp | Out-Null
try {
    Invoke-WebRequest -UseBasicParsing $url -OutFile (Join-Path $tmp 'rtmo-s.zip')
    Expand-Archive (Join-Path $tmp 'rtmo-s.zip') -DestinationPath $tmp
    Move-Item (Join-Path $tmp 'end2end.onnx') $out
    "Saved $out"
}
finally { Remove-Item -Recurse -Force $tmp }
