# this lets me paste the source into Gemini's Thinking 3.5 model via the web UI

$output = "concat-source.txt"
$root   = $PSScriptRoot

$sections = @()

# C# source files
$sections += Get-ChildItem -Path $root -Recurse -Filter "*.cs" |
    Where-Object { $_.FullName -notmatch "\\(obj|bin)\\" } |
    Sort-Object FullName |
    ForEach-Object {
        $relativePath = $_.FullName.Substring($root.Length + 1)
        "// ============================================================"
        "// $relativePath"
        "// ============================================================"
        Get-Content $_.FullName
        ""
    }

# Markdown docs: README, DESIGN, and all ADRs
$mdFiles = @(
    (Join-Path $root "README.md"),
    (Join-Path $root "DESIGN.md")
) + (Get-ChildItem -Path (Join-Path $root "docs\adrs") -Filter "*.md" | Sort-Object Name | ForEach-Object { $_.FullName })

foreach ($mdFile in $mdFiles) {
    if (Test-Path $mdFile) {
        $relativePath = $mdFile.Substring($root.Length + 1)
        $sections += "// ============================================================"
        $sections += "// $relativePath"
        $sections += "// ============================================================"
        $sections += Get-Content $mdFile
        $sections += ""
    }
}

$sections | Set-Content -Path (Join-Path $root $output) -Encoding UTF8

Write-Host "Written to $output ($((Get-Item (Join-Path $root $output)).Length) bytes)"
