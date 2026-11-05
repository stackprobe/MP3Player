$ErrorActionPreference = 'Stop'

$projectPath = Join-Path $PSScriptRoot 'src\MP3Player\MP3Player.csproj'
$docPath = Join-Path $PSScriptRoot 'doc'
$outPath = Join-Path $PSScriptRoot 'out'
$zipPath = Join-Path $outPath 'MP3Player.zip'
$stagingPath = Join-Path $outPath ([Guid]::NewGuid().ToString('N'))
$packagePath = Join-Path $stagingPath 'MP3Player'
$stagedZip = Join-Path $stagingPath 'MP3Player.zip'

$outDirectory = New-Item -ItemType Directory -Path $outPath -Force
if ($outDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw "The output directory must not be a link: $outPath"
}

$stagingDirectory = New-Item -ItemType Directory -Path $stagingPath
try {
    & dotnet build $projectPath --configuration Release --output $packagePath
    if ($LASTEXITCODE -ne 0) {
        throw "Release build failed (exit code $LASTEXITCODE)."
    }

    Copy-Item -LiteralPath $docPath -Destination $packagePath -Recurse -Force

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($stagedZip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $archive.CreateEntry('MP3Player/') | Out-Null
        foreach ($item in Get-ChildItem -LiteralPath $packagePath -Recurse -Force) {
            $relativePath = $item.FullName.Substring($packagePath.Length + 1).Replace('\', '/')
            $entryName = "MP3Player/$relativePath"
            if ($item.PSIsContainer) {
                $archive.CreateEntry("$entryName/") | Out-Null
            }
            else {
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $archive, $item.FullName, $entryName, [IO.Compression.CompressionLevel]::Optimal
                ) | Out-Null
            }
        }
    }
    finally {
        $archive.Dispose()
    }

    Move-Item -LiteralPath $stagedZip -Destination $zipPath -Force
    Write-Host "Created $zipPath"
}
finally {
    if ($stagingDirectory.Parent.FullName -eq $outDirectory.FullName) {
        Remove-Item -LiteralPath $stagingDirectory.FullName -Recurse -Force
    }
}
