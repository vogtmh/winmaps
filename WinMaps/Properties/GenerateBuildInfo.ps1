$date = [DateTime]::UtcNow.ToString('d. MMM yyyy')
$content = "namespace WinMaps { internal static class BuildInfo { public const string Date = `"$date`"; } }"
[IO.File]::WriteAllText("$PSScriptRoot\BuildInfo.cs", $content, [Text.Encoding]::UTF8)
