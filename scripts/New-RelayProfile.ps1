param([Parameter(Mandatory=$true)][string]$Origin,[string]$OutputPath=(Join-Path $PWD 'VoiceBridge-sender.vbrelay'))
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Security
$uri=[Uri]$Origin
if($uri.Scheme -ne 'https' -or $uri.AbsolutePath -ne '/' -or $uri.Query -or $uri.Fragment -or $uri.UserInfo){throw '请输入 HTTPS 根地址。'}
$canonical=$uri.GetLeftPart([UriPartial]::Authority)
$secure=Read-Host '输入建房密钥（64位十六进制，不会回显）' -AsSecureString
$pointer=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try{
  $secret=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
  if($secret -cnotmatch '^[a-f0-9]{64}$'){throw '密钥必须为64位小写十六进制。'}
  $bytes=[Text.Encoding]::ASCII.GetBytes($secret)
  $protected=[Security.Cryptography.ProtectedData]::Protect($bytes,[Text.Encoding]::UTF8.GetBytes('VoiceBridge.Relay.v1|'+$canonical),[Security.Cryptography.DataProtectionScope]::CurrentUser)
  if(Test-Path -LiteralPath $OutputPath){Copy-Item -LiteralPath $OutputPath -Destination ($OutputPath+'.previous')}
  @{Version=1;Origin=$canonical;ProtectedPublishKey=[Convert]::ToBase64String($protected)} | ConvertTo-Json | Set-Content -LiteralPath $OutputPath -Encoding utf8
  Write-Output ('已保存本机加密配置：'+$OutputPath)
}finally{
  [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
  if($bytes){[Array]::Clear($bytes,0,$bytes.Length)}
  $secret=$null
}
