param([Parameter(Mandatory)][string]$Feed,[Parameter(Mandatory)][string]$PrivateKey)
$ErrorActionPreference='Stop'
# Run only in the restricted publishing environment; the private key is never copied to artifacts.
$key=[Security.Cryptography.ECDsa]::Create()
try {
 $read=0;$key.ImportPkcs8PrivateKey([IO.File]::ReadAllBytes([IO.Path]::GetFullPath($PrivateKey)),[ref]$read)
 $signature=$key.SignData([IO.File]::ReadAllBytes([IO.Path]::GetFullPath($Feed)),[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)
 [IO.File]::WriteAllText([IO.Path]::GetFullPath($Feed)+'.sig',[Convert]::ToBase64String($signature),[Text.Encoding]::ASCII)
} finally {$key.Dispose()}
