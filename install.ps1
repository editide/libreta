# Installs the latest Libreta release, which bundles its own runtime:
#   irm https://raw.githubusercontent.com/editide/libreta/main/install.ps1 | iex
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$releaseUrl = if ($env:LIBRETA_RELEASE_URL) { $env:LIBRETA_RELEASE_URL }
  else { 'https://github.com/editide/libreta/releases/latest/download' }
$applicationDirectory = if ($env:LIBRETA_INSTALL_DIR) { $env:LIBRETA_INSTALL_DIR }
  else { Join-Path $env:LOCALAPPDATA 'Programs\libreta' }
$executable = Join-Path $applicationDirectory 'Libreta.exe'

# The directory is replaced wholesale, so refuse one that does not already hold Libreta.
if ((Test-Path $applicationDirectory) -and -not (Test-Path $executable)) {
  throw "$applicationDirectory exists and does not hold Libreta; set LIBRETA_INSTALL_DIR elsewhere."
}

$archive = 'libreta-win-x64.zip'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid())
New-Item -ItemType Directory $temporary | Out-Null
try {
  Write-Host "Downloading $releaseUrl/$archive"
  Invoke-WebRequest "$releaseUrl/$archive" -OutFile (Join-Path $temporary $archive) -UseBasicParsing
  Expand-Archive (Join-Path $temporary $archive) $temporary
  if (Test-Path $applicationDirectory) {
    Remove-Item -Recurse -Force $applicationDirectory
  }
  New-Item -ItemType Directory -Force (Split-Path $applicationDirectory) | Out-Null
  Move-Item (Join-Path $temporary 'libreta') $applicationDirectory
}
finally {
  Remove-Item -Recurse -Force $temporary
}
Get-ChildItem -Recurse $applicationDirectory | Unblock-File

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (-not (($userPath -split ';') -contains $applicationDirectory)) {
  $newPath = if ($userPath) { "$applicationDirectory;$userPath" } else { $applicationDirectory }
  [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
  $env:Path = "$applicationDirectory;$env:Path"
}
& $executable --help | Out-Null

Write-Host ''
Write-Host "Installed $executable and added its folder to PATH for new terminals."
