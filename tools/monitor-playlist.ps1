param(
    [string]$PlaylistId = '51549f0b-5509-46d7-a82f-f6ee5d9f06e0',
    [string]$OutputDirectory = (Join-Path $env:PI_SCRATCH_DIR 'playlist-monitor'),
    [switch]$Once
)
$ErrorActionPreference = 'Stop'
$parsedId = [guid]::Parse($PlaylistId).ToString()
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$sql = @"
SELECT json_build_object(
 'at',now(),'playlistId','$parsedId',
 'linked',(SELECT count(*) FROM user_playlist_tracks WHERE playlist_id='$parsedId'),
 'states',(SELECT coalesce(json_agg(s),'[]'::json) FROM (SELECT status,count(*) AS count FROM user_playlist_pending_tracks WHERE playlist_id='$parsedId' GROUP BY status) s),
 'issues',(SELECT coalesce(json_agg(s),'[]'::json) FROM (SELECT p.title,p.source_track_id,p.status,t.source_provider,t.status AS download_status,t.error_code,t.auto_retry_count,i.status AS import_status,i.auto_score,i.warnings_json,round(extract(epoch from (now()-t.updated_at_utc))/60) AS download_age_minutes FROM user_playlist_pending_tracks p LEFT JOIN external_download_tasks t ON t.id=p.download_task_id LEFT JOIN library_import_items i ON i.id=t.import_item_id WHERE p.playlist_id='$parsedId' AND (p.status IN ('failed','rejected','no_source','needs_review') OR (t.status IN ('queued','downloading','import_queued') AND t.updated_at_utc<now()-interval '10 minutes')) ORDER BY p.sort_order LIMIT 100) s)
);
"@
do {
    try {
        $raw = & docker exec shinemusic-postgres psql -X -q -t -A -U shinemusic -d shinemusic -c $sql
        if ($LASTEXITCODE -ne 0) { throw 'database_poll_failed' }
        $snapshot = ($raw -join "`n") | ConvertFrom-Json
        try { $health = (Invoke-WebRequest 'http://127.0.0.1:5180/health' -UseBasicParsing -TimeoutSec 8).StatusCode } catch { $health = 0 }
        $snapshot | Add-Member -NotePropertyName apiStatus -NotePropertyValue $health
        $json = $snapshot | ConvertTo-Json -Depth 12 -Compress
        $json | Set-Content (Join-Path $OutputDirectory 'latest.json') -Encoding UTF8
        Add-Content (Join-Path $OutputDirectory 'history.jsonl') $json -Encoding UTF8
        Write-Output ("PLAYLIST_MONITOR " + $json)
    } catch {
        Write-Output ('PLAYLIST_MONITOR_ERROR ' + $_.Exception.GetType().Name)
    }
    if (!$Once) { Start-Sleep -Seconds 60 }
} while (!$Once)
