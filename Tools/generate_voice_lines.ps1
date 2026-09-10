param(
    [string]$JsonPath = "$PSScriptRoot\..\Assets\CAIVR\Resources\CAIVR\Conversations\consultation_demo.json",
    [string]$OutDir   = "$PSScriptRoot\..\Assets\CAIVR\Resources\CAIVR\VO",
    [string]$VoiceMatch = "Zira"
)

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$json = Get-Content $JsonPath -Raw | ConvertFrom-Json

$voice = New-Object -ComObject SAPI.SpVoice

# Prefer a female-presenting voice: the stakeholder asked for a female professor.
$tokens = $voice.GetVoices()
$chosen = $null
for ($i = 0; $i -lt $tokens.Count; $i++) {
    $t = $tokens.Item($i)
    if ($t.GetDescription() -like "*$VoiceMatch*") { $chosen = $t; break }
}
if ($null -ne $chosen) {
    $voice.Voice = $chosen
    Write-Output "voice: $($chosen.GetDescription())"
} else {
    Write-Output "voice: (default - '$VoiceMatch' not found)"
}

# Slightly slower than default reads more like considered speech than an announcement.
$voice.Rate = -1

function Write-Line {
    param([string]$Text, [string]$Path)

    if ([string]::IsNullOrWhiteSpace($Text)) { return $false }

    $stream = New-Object -ComObject SAPI.SpFileStream
    # 22kHz 16-bit mono: small files, and Unity imports it without resampling complaints.
    $stream.Format.Type = 22
    $stream.Open($Path, 3, $false)   # 3 = SSFMCreateForWrite
    $voice.AudioOutputStream = $stream
    $null = $voice.Speak($Text, 0)   # 0 = synchronous
    $stream.Close()
    return $true
}

$count = 0
foreach ($node in $json.nodes) {
    $id = $node.id

    if (Write-Line -Text $node.speakerLine -Path (Join-Path $OutDir "$id.wav")) {
        $count++
        Write-Output "  $id.wav"
    }

    if (Write-Line -Text $node.reprompt -Path (Join-Path $OutDir "${id}_reprompt.wav")) {
        $count++
        Write-Output "  ${id}_reprompt.wav"
    }
}

# One shared line for when nothing matched and the node has no custom reprompt.
$null = Write-Line -Text "Sorry, I didn't catch that. Could you say it again?" -Path (Join-Path $OutDir "_fallback_reprompt.wav")
$count++

Write-Output "GENERATED=$count"
