param([string]$ExecutablePath,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(!$OutputDirectory){$OutputDirectory=Join-Path $root 'artifacts/packages'}
$output=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output)|Out-Null
$version='0.10.0'
$staging=Join-Path $output ('source-'+[Guid]::NewGuid().ToString('N'))
$source=Join-Path $staging 'VoiceBridge'
[IO.Directory]::CreateDirectory($source)|Out-Null
$allowed=@('.gitattributes','.gitignore','CHANGELOG.md','Directory.Build.props','global.json','LICENSE','README.md','VoiceBridge.sln','src','tests','scripts','third-party','docs','relay')
$count=0
foreach($file in Get-ChildItem -LiteralPath $root -Recurse -File){
  $relative=[IO.Path]::GetRelativePath($root,$file.FullName).Replace('\','/')
  $parts=$relative.Split('/')
  if($parts[0] -notin $allowed -or ($parts|Where-Object {$_ -in @('bin','obj','node_modules','.git','artifacts')})){continue}
  if($file.Extension -in @('.exe','.pdb','.zip','.pem','.vbrelay') -or $file.Name -in @('shared-secret','relay.env','fixture-links.json','fixture-config.json') -or $file.Name -like '*profile*.json'){throw "Unexpected private/build file: $relative"}
  $target=Join-Path $source $relative
  [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))|Out-Null
  [IO.File]::Copy($file.FullName,$target);$count++
}
$archive=Join-Path $output "VoiceBridge-v$version-source.zip"
if(Test-Path -LiteralPath $archive){throw 'Archive already exists; use a new output directory.'}
[IO.Compression.ZipFile]::CreateFromDirectory($staging,$archive,[IO.Compression.CompressionLevel]::Optimal,$false)
$files=@($archive)
if($ExecutablePath){$exe=Join-Path $output "VoiceBridge-v$version.exe";[IO.File]::Copy([IO.Path]::GetFullPath($ExecutablePath),$exe);$files+=$exe}
$lines=$files|ForEach-Object {'{0}  {1}' -f (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant(),[IO.Path]::GetFileName($_)}
[IO.File]::WriteAllLines((Join-Path $output 'SHA256SUMS.txt'),$lines,[Text.Encoding]::ASCII)
Write-Output "Public source files: $count"
Write-Output $lines
