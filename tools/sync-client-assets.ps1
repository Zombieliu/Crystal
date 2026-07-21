param(
    [string]$PatchHost = "https://mirfiles.co.uk/resources/mir2/crystal/patch/",
    [string]$PatchListPath = "E:\mir2\downloads\PList.gz",
    [string]$ClientDir = "E:\mir2\Crystal\Build\Client\Debug",
    [int]$MaxFiles = 0,
    [int]$IncludeData = 1,
    [int]$IncludeMap = 1,
    [int]$IncludeSound = 1
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Net.Http

function Expand-GzipBytes {
    param([byte[]]$Bytes)

    $input = [System.IO.MemoryStream]::new($Bytes)
    try {
        $gzip = [System.IO.Compression.GZipStream]::new($input, [System.IO.Compression.CompressionMode]::Decompress)
        try {
            $output = [System.IO.MemoryStream]::new()
            try {
                $gzip.CopyTo($output)
                return $output.ToArray()
            }
            finally {
                $output.Dispose()
            }
        }
        finally {
            $gzip.Dispose()
        }
    }
    finally {
        $input.Dispose()
    }
}

function Read-PatchList {
    param([string]$Path)

    $raw = [System.IO.File]::ReadAllBytes($Path)
    $ms = [System.IO.MemoryStream]::new($raw)
    try {
        $reader = [System.IO.BinaryReader]::new($ms)
        try {
            $count = $reader.ReadInt32()
            $items = New-Object System.Collections.Generic.List[object]

            for ($i = 0; $i -lt $count; $i++) {
                $fileName = $reader.ReadString()
                $length = $reader.ReadInt32()
                $compressed = $reader.ReadInt32()
                $creation = [DateTime]::FromBinary($reader.ReadInt64())

                $items.Add([pscustomobject]@{
                    FileName = $fileName
                    Length = $length
                    Compressed = $compressed
                    Creation = $creation
                })
            }

            return $items
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $ms.Dispose()
    }
}

function Needs-Download {
    param(
        [string]$Path,
        [int]$Length,
        [datetime]$Creation
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $true
    }

    $info = Get-Item -LiteralPath $Path
    return ($info.Length -ne $Length) -or ($info.LastWriteTime -ne $Creation)
}

if (-not (Test-Path -LiteralPath $PatchListPath)) {
    throw "Patch list not found: $PatchListPath"
}

$entries = Read-PatchList -Path $PatchListPath | Where-Object {
    ($IncludeData -and $_.FileName -like 'Data\*') -or
    ($IncludeMap -and $_.FileName -like 'Map\*') -or
    ($IncludeSound -and $_.FileName -like 'Sound\*')
} | Sort-Object @{
        Expression = {
            if ($_.FileName -like 'Data\*') { return 0 }
            if ($_.FileName -like 'Sound\*') { return 1 }
            if ($_.FileName -like 'Map\Unused\*') { return 3 }
            return 2
        }
    }, FileName

$todo = foreach ($entry in $entries) {
    $target = Join-Path $ClientDir $entry.FileName
    if (Needs-Download -Path $target -Length $entry.Length -Creation $entry.Creation) {
        [pscustomobject]@{
            FileName = $entry.FileName
            Length = $entry.Length
            Compressed = $entry.Compressed
            Creation = $entry.Creation
            Target = $target
        }
    }
}

if ($MaxFiles -gt 0) {
    $todo = $todo | Select-Object -First $MaxFiles
}

$http = [System.Net.Http.HttpClient]::new()
try {
    $http.DefaultRequestHeaders.UserAgent.ParseAdd("Codex/1.0")

    $index = 0
    foreach ($entry in $todo) {
        $index++
        $remoteName = $entry.FileName.Replace('\', '/')
        if ($remoteName -ne 'PList.gz' -and ($entry.Compressed -ne $entry.Length -or $entry.Compressed -eq 0)) {
            $remoteName = "$remoteName.gz"
        }

        $url = "$PatchHost$remoteName"
        Write-Host ("[{0}/{1}] {2}" -f $index, $todo.Count, $entry.FileName)

        $bytes = $http.GetByteArrayAsync($url).GetAwaiter().GetResult()
        if ($entry.Compressed -gt 0 -and $entry.Compressed -ne $entry.Length) {
            $bytes = Expand-GzipBytes -Bytes $bytes
        }

        $dir = Split-Path -Parent $entry.Target
        if (-not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
        }

        [System.IO.File]::WriteAllBytes($entry.Target, $bytes)
        [System.IO.File]::SetLastWriteTime($entry.Target, $entry.Creation)
    }
}
finally {
    $http.Dispose()
}

Write-Host ("Completed. Downloaded {0} file(s)." -f $todo.Count)
