$wemFolder   = ".\WEM"
$jsonFolder  = "..\WemRes"
$outputFolder = ".\RENAMED"

if (!(Test-Path $outputFolder)) {
    New-Item -ItemType Directory -Path $outputFolder | Out-Null
}

Get-ChildItem -Path $wemFolder -Filter *.wem | ForEach-Object {

    $wemFile = $_
    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($wemFile.Name)

    $jsonPath = Join-Path $jsonFolder ($baseName + ".json")

    if (Test-Path $jsonPath) {

        try {
            $jsonContent = Get-Content $jsonPath -Raw | ConvertFrom-Json

            $wemID = $jsonContent.WemID

            if ($wemID) {
				
                $newName = "$wemID.wem"
                $destinationPath = Join-Path $outputFolder $newName

                Move-Item $wemFile.FullName -Destination $destinationPath -Force

                Write-Host "OK: $($wemFile.Name) -> $newName"
            }
            else {
                Write-Host "WARN: Keine WemID in $jsonPath"
            }
        }
        catch {
            Write-Host "ERROR beim Verarbeiten von $jsonPath"
        }

    }
    else {
        Write-Host "SKIP: Kein JSON für $($wemFile.Name)"
    }
}