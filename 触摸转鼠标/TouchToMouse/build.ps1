# 编译 TouchToMouse.cs -> TouchToMouse.exe（控制台子系统）
$ErrorActionPreference = 'Stop'

$src = Join-Path $PSScriptRoot 'TouchToMouse.cs'
$out = Join-Path $PSScriptRoot 'TouchToMouse.exe'

Get-Process TouchToMouse -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

$code = Get-Content -LiteralPath $src -Raw -Encoding UTF8

Add-Type -TypeDefinition $code -Language CSharp `
  -OutputAssembly $out `
  -OutputType ConsoleApplication `
  -ReferencedAssemblies System.Windows.Forms, System.Drawing

if (-not (Test-Path -LiteralPath $out)) { throw 'compile produced no output' }
'OK: {0} ({1:N0} bytes)' -f $out, (Get-Item -LiteralPath $out).Length
