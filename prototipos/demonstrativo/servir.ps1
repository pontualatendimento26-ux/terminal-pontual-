# Serve somente o demonstrativo, somente neste computador (localhost).
$ErrorActionPreference = 'Stop'
$pagina = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'Demonstrativo-Terminal.html'))
$servidor = $null
foreach ($porta in 8095..8105) {
  try { $s = New-Object Net.HttpListener; $s.Prefixes.Add("http://localhost:$porta/"); $s.Start(); $servidor = $s; break } catch { }
}
if (-not $servidor) { Write-Host 'Nenhuma porta livre. Abra o arquivo Demonstrativo-Terminal.html com dois cliques.'; exit 1 }
Write-Host "Demonstrativo em http://localhost:$porta/"
Write-Host 'Deixe esta janela aberta durante o teste. Para encerrar, feche-a.'
if (-not $env:PONTUAL_SEM_NAVEGADOR) { Start-Process "http://localhost:$porta/" }
while ($servidor.IsListening) {
  $c = $servidor.GetContext()
  try {
    if ($c.Request.Url.AbsolutePath -eq '/') {
      $c.Response.ContentType = 'text/html; charset=utf-8'
      $c.Response.ContentLength64 = $pagina.Length
      $c.Response.OutputStream.Write($pagina, 0, $pagina.Length)
    } else { $c.Response.StatusCode = 404 }
  } catch { } finally { try { $c.Response.Close() } catch { } }
}
