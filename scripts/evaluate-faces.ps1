[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$FixturesPath,
    [string]$BaseUrl = 'http://localhost:5276',
    [string]$OutputCsv = 'face-verification-results.csv'
)

$ErrorActionPreference = 'Stop'
$fixturesRoot = (Resolve-Path -LiteralPath $FixturesPath).Path
$cases = @(
    [pscustomobject]@{ Name = 'A1-A1'; Reference = 'person-a-1.jpg'; Captured = 'person-a-1.jpg'; Expected = 'Match' },
    [pscustomobject]@{ Name = 'A1-A2'; Reference = 'person-a-1.jpg'; Captured = 'person-a-2.jpg'; Expected = 'Match' },
    [pscustomobject]@{ Name = 'A1-B'; Reference = 'person-a-1.jpg'; Captured = 'person-b.jpg'; Expected = 'NotMatch' },
    [pscustomobject]@{ Name = 'A1-C'; Reference = 'person-a-1.jpg'; Captured = 'person-c.jpg'; Expected = 'NotMatch' },
    [pscustomobject]@{ Name = 'A1-D'; Reference = 'person-a-1.jpg'; Captured = 'person-d.jpg'; Expected = 'NotMatch' },
    [pscustomobject]@{ Name = 'B-C'; Reference = 'person-b.jpg'; Captured = 'person-c.jpg'; Expected = 'NotMatch' },
    [pscustomobject]@{ Name = 'A1-NoFace'; Reference = 'person-a-1.jpg'; Captured = 'no-face.jpg'; Expected = 'FaceNotDetected' },
    [pscustomobject]@{ Name = 'A1-MultipleFaces'; Reference = 'person-a-1.jpg'; Captured = 'multiple-faces.jpg'; Expected = 'MultipleFacesDetected' }
)

$requiredFiles = $cases.Reference + $cases.Captured | Sort-Object -Unique
foreach ($file in $requiredFiles) {
    $path = Join-Path $fixturesRoot $file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing local fixture: $path"
    }
}

$uri = "$($BaseUrl.TrimEnd('/'))/api/v1/verification-sessions/00000000-0000-0000-0000-000000000000/verify"
$results = foreach ($case in $cases) {
    $startedAt = Get-Date
    try {
        $response = Invoke-RestMethod -Uri $uri -Method Post -Form @{
            referenceImage = Get-Item -LiteralPath (Join-Path $fixturesRoot $case.Reference)
            capturedImage = Get-Item -LiteralPath (Join-Path $fixturesRoot $case.Captured)
        }
        [pscustomobject]@{
            Case = $case.Name; Expected = $case.Expected; Actual = $response.decision
            Passed = $response.decision -eq $case.Expected; CosineSimilarity = $response.similarityScore
            MatchThreshold = $response.matchThreshold; NonMatchThreshold = $response.nonMatchThreshold
            ReferenceQuality = $response.referenceImageQuality; CapturedQuality = $response.capturedImageQuality
            Model = "$($response.modelName):$($response.modelVersion)"; ApiProcessingMs = $response.processingTimeMs
            ClientElapsedMs = [long]((Get-Date) - $startedAt).TotalMilliseconds; Error = $null
        }
    }
    catch {
        [pscustomobject]@{
            Case = $case.Name; Expected = $case.Expected; Actual = 'RequestError'; Passed = $false
            CosineSimilarity = $null; MatchThreshold = $null; NonMatchThreshold = $null
            ReferenceQuality = $null; CapturedQuality = $null; Model = $null; ApiProcessingMs = $null
            ClientElapsedMs = [long]((Get-Date) - $startedAt).TotalMilliseconds; Error = $_.Exception.Message
        }
    }
}

$results | Format-Table -AutoSize
$results | Export-Csv -LiteralPath $OutputCsv -NoTypeInformation -Encoding utf8
if ($results.Passed -contains $false) {
    throw "One or more acceptance cases failed. Results were written to $OutputCsv"
}
