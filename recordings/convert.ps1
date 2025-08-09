Get-ChildItem -Filter *.mp3 | ForEach-Object {
    $input = $_.FullName
    $output = ".\$($_.BaseName).wav"
    ffmpeg -i "$input" -ac 1 -ar 8000 "$output"
}
