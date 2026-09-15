# Stages a portable, no-install Python runtime + page_dewarp.py + its dependencies into
# publish/windows/python/, so the packaged MicroCapture app can call page_dewarp.py
# (https://mzucker.github.io/2016/08/15/page-dewarping.html — vendored at
# MicroCapture.Processing/vendor/page_dewarp/page_dewarp.py, with one deliberate change from
# upstream documented in that file's own header comment) as a subprocess with nothing
# pre-installed on the operator's machine — see MicroCapture.Processing/PythonDewarpRunner.cs,
# which looks for python.exe at "<app-dir>/python/python.exe" and the script at
# "<app-dir>/python/page_dewarp/page_dewarp.py".
#
# Run this AFTER `dotnet publish` has populated publish/windows/ and BEFORE Inno Setup packages
# it — installer/MicroCapture.iss's [Files] section already sweeps up everything under
# publish/windows/ recursively, so nothing in the .iss itself needs to change once this script
# has run; it just needs to have already dropped the python/ folder into place.
#
# Usage (from the repo root):
#   pwsh installer/prepare-python-runtime.ps1
#   pwsh installer/prepare-python-runtime.ps1 -PageDewarpScriptPath "C:\path\to\page_dewarp.py"
#
# Version pins below match what was already proven working in this repo's own macOS .venv
# (numpy/scipy/opencv-python-headless/pillow) — NOT the stale, non-installable package names in
# page_dewarp's own requirements.txt ("Image", "cv2>=3.0"), which were confirmed during
# investigation to be unusable as-is. opencv-python-headless (not plain opencv-python) is used
# deliberately: page_dewarp.py never calls a GUI-only cv2 function at DEBUG_LEVEL=0 (the
# default), so there's no reason to pull in the Qt/GUI native dependencies the non-headless
# build carries.

param(
    [string]$PythonVersion = "3.12.7",
    [string]$NumpyVersion = "2.5.2",
    [string]$ScipyVersion = "1.18.0",
    [string]$OpenCvVersion = "5.0.0.93",
    [string]$PillowVersion = "12.3.0",
    [string]$PublishDir = "publish/windows",
    [string]$PageDewarpScriptPath = "" # defaults to this repo's own MicroCapture.Processing/vendor/page_dewarp/page_dewarp.py if not given
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishPath = Join-Path $repoRoot $PublishDir
if (-not (Test-Path $publishPath)) {
    throw "publish dir not found at '$publishPath' — run 'dotnet publish' first (see .github/workflows/build-installer.yml for the exact command)."
}

$pythonDir = Join-Path $publishPath "python"
if (Test-Path $pythonDir) {
    Write-Host "Removing existing $pythonDir to rebuild cleanly..."
    Remove-Item -Recurse -Force $pythonDir
}
New-Item -ItemType Directory -Path $pythonDir | Out-Null

# --- 1. Download and extract the official embeddable distribution ---
# This is the no-install, no-registry-writes Python build python.org publishes specifically for
# bundling into other applications — exactly this use case.
$embedUrl = "https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-embed-amd64.zip"
$embedZip = Join-Path $env:TEMP "python-embed-$PythonVersion.zip"
Write-Host "Downloading embeddable Python $PythonVersion from $embedUrl ..."
Invoke-WebRequest -Uri $embedUrl -OutFile $embedZip
Expand-Archive -Path $embedZip -DestinationPath $pythonDir -Force
Remove-Item $embedZip

# --- 2. Re-enable site-packages discovery ---
# The embeddable distribution ships with a python3XX._pth file that disables the `site` module
# (and therefore site-packages) by default — has to be un-commented before pip-installed
# packages become importable at all.
$pthFile = Get-ChildItem -Path $pythonDir -Filter "python3*._pth" | Select-Object -First 1
if (-not $pthFile) { throw "Could not find the embeddable distribution's ._pth file in $pythonDir" }
(Get-Content $pthFile.FullName) -replace '^#import site$', 'import site' | Set-Content $pthFile.FullName
Write-Host "Re-enabled site-packages in $($pthFile.Name)"

# --- 3. Bootstrap pip ---
# The embeddable distribution has no pip preinstalled.
$getPipPath = Join-Path $env:TEMP "get-pip.py"
Write-Host "Downloading get-pip.py ..."
Invoke-WebRequest -Uri "https://bootstrap.pypa.io/get-pip.py" -OutFile $getPipPath
& "$pythonDir\python.exe" $getPipPath --no-warn-script-location
Remove-Item $getPipPath

# --- 4. Install pinned dependencies ---
Write-Host "Installing pinned dependencies (numpy $NumpyVersion, scipy $ScipyVersion, opencv-python-headless $OpenCvVersion, pillow $PillowVersion) ..."
& "$pythonDir\python.exe" -m pip install --no-warn-script-location `
    "numpy==$NumpyVersion" `
    "scipy==$ScipyVersion" `
    "opencv-python-headless==$OpenCvVersion" `
    "pillow==$PillowVersion"

# --- 5. Copy in page_dewarp.py (vendored copy, one deliberate change from upstream — see its
# own header comment) ---
if ([string]::IsNullOrWhiteSpace($PageDewarpScriptPath)) {
    $PageDewarpScriptPath = Join-Path $repoRoot "MicroCapture.Processing\vendor\page_dewarp\page_dewarp.py"
}
if (-not (Test-Path $PageDewarpScriptPath)) {
    throw "page_dewarp.py not found at '$PageDewarpScriptPath' — pass -PageDewarpScriptPath explicitly if it isn't at the usual vendored location."
}
$pageDewarpDestDir = Join-Path $pythonDir "page_dewarp"
New-Item -ItemType Directory -Path $pageDewarpDestDir | Out-Null
Copy-Item $PageDewarpScriptPath -Destination (Join-Path $pageDewarpDestDir "page_dewarp.py")
Write-Host "Copied page_dewarp.py from $PageDewarpScriptPath"

# --- 6. Sanity check ---
# Confirms the bundled interpreter can actually import every dependency page_dewarp.py needs
# before this script declares success — catches a bad pip install or a missing native wheel
# immediately, rather than surfacing as a mysterious runtime failure inside the packaged app.
Write-Host "Verifying bundled runtime can import numpy, scipy, cv2, PIL ..."
& "$pythonDir\python.exe" -c "import numpy, scipy, cv2, PIL; print('OK:', numpy.__version__, scipy.__version__, cv2.__version__, PIL.__version__)"
if ($LASTEXITCODE -ne 0) {
    throw "Bundled Python runtime failed its import sanity check — see output above."
}

Write-Host "Python runtime staged successfully at $pythonDir"
