function Get-AirglowApi {
    param(
        [Parameter(Mandatory)][string]$Path,
        [int]$TimeoutMs = 3000
    )

    $client = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'airglow-v1', [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $client.Connect($TimeoutMs)
        $request = [System.Text.Encoding]::ASCII.GetBytes("GET $Path HTTP/1.1`r`nHost: airglow`r`nConnection: close`r`n`r`n")
        $client.Write($request, 0, $request.Length)
        $client.Flush()

        $buffer = [System.IO.MemoryStream]::new()
        $chunk = New-Object byte[] 4096
        while (($read = $client.Read($chunk, 0, $chunk.Length)) -gt 0) { $buffer.Write($chunk, 0, $read) }
        $bytes = $buffer.ToArray()
    }
    finally {
        $client.Dispose()
    }

    $split = -1
    for ($i = 0; $i -le $bytes.Length - 4; $i++) {
        if ($bytes[$i] -eq 13 -and $bytes[$i + 1] -eq 10 -and $bytes[$i + 2] -eq 13 -and $bytes[$i + 3] -eq 10) { $split = $i; break }
    }
    if ($split -lt 0) { throw "Malformed HTTP response from airglow pipe for $Path." }

    $head = [System.Text.Encoding]::ASCII.GetString($bytes, 0, $split)
    $bodyStart = $split + 4
    $statusLine = ($head -split "`r`n")[0]
    if ($statusLine -notmatch '^HTTP/1\.[01] (\d{3})') { throw "Bad status line from airglow pipe: $statusLine" }
    if ([int]$Matches[1] -ne 200) { throw "GET $Path returned: $statusLine" }

    if ($head -match '(?im)^Transfer-Encoding:\s*chunked') {
        $body = [System.IO.MemoryStream]::new()
        $pos = $bodyStart
        while ($true) {
            $eol = $pos
            while ($eol -lt $bytes.Length - 1 -and -not ($bytes[$eol] -eq 13 -and $bytes[$eol + 1] -eq 10)) { $eol++ }
            if ($eol -ge $bytes.Length - 1) { throw 'Truncated chunked response from airglow pipe.' }
            $sizeText = [System.Text.Encoding]::ASCII.GetString($bytes, $pos, $eol - $pos).Split(';')[0].Trim()
            $size = [Convert]::ToInt32($sizeText, 16)
            if ($size -eq 0) { break }
            $pos = $eol + 2
            if ($pos + $size -gt $bytes.Length) { throw 'Truncated chunked response from airglow pipe.' }
            $body.Write($bytes, $pos, $size)
            $pos += $size + 2
        }
        $text = [System.Text.Encoding]::UTF8.GetString($body.ToArray())
    }
    else {
        $text = [System.Text.Encoding]::UTF8.GetString($bytes, $bodyStart, $bytes.Length - $bodyStart)
    }

    $text | ConvertFrom-Json
}

Export-ModuleMember -Function Get-AirglowApi
