# this lets me paste the source into Gemini's Thinking 3.5 model via the web UI

$output = "concat-source.txt"
$root   = $PSScriptRoot

Get-ChildItem -Path $root -Recurse -Filter "*.cs" |
    Where-Object { $_.FullName -notmatch "\\(obj|bin)\\" } |
    Sort-Object FullName |
    ForEach-Object {
        $relativePath = $_.FullName.Substring($root.Length + 1)
        "// ============================================================"
        "// $relativePath"
        "// ============================================================"
        Get-Content $_.FullName
        ""
    } |
    Set-Content -Path (Join-Path $root $output) -Encoding UTF8

Write-Host "Written to $output ($((Get-Item (Join-Path $root $output)).Length) bytes)"
