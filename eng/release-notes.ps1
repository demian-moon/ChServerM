# 릴리스 노트 생성 (Phase 21) — Conventional Commits 를 섹션별로 묶는다.
#
# 사용:
#   eng/release-notes.ps1                       # 마지막 태그 → HEAD (태그가 없으면 전체 이력)
#   eng/release-notes.ps1 -From v0.1.0 -To HEAD # 명시 구간
#   eng/release-notes.ps1 -Output notes.md      # 파일로
#
# 형식 규약(CLAUDE.md 7절): `type(scope): subject`. 스코프는 어셈블리 축 이름이다.
# 섹션 순서는 소비자 관심 순 — 새 기능 → 성능(수치는 커밋 본문에 있다) → 수정 → 그 외.
# breaking 판정은 자동화하지 않는다 — 표면 5개 점검(docs/VERSIONING.md)은 사람이 diff 로 한다.

param(
    [string] $From,
    [string] $To = 'HEAD',
    [string] $Output
)

$ErrorActionPreference = 'Stop'

if (-not $From) {
    $From = git describe --tags --abbrev=0 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $From) {
        # 첫 릴리스 — 태그가 없으니 전체 이력이 대상이다.
        $From = $null
    }
}

$range = if ($From) { "$From..$To" } else { $To }
Write-Host "구간: $range" -ForegroundColor Cyan

# %x1f(단위 구분자)로 해시와 제목을 안전하게 가른다 — 제목에 공백·콜론이 흔하다.
$lines = git log $range --no-merges --pretty=format:"%h%x1f%s"
if ($LASTEXITCODE -ne 0) {
    Write-Host "git log 실패 — 구간 표기를 확인한다: $range" -ForegroundColor Red
    exit 1
}

$sections = [ordered]@{
    'feat'  = @{ Title = '새 기능';        Items = [System.Collections.Generic.List[string]]::new() }
    'perf'  = @{ Title = '성능';           Items = [System.Collections.Generic.List[string]]::new() }
    'fix'   = @{ Title = '수정';           Items = [System.Collections.Generic.List[string]]::new() }
    'docs'  = @{ Title = '문서';           Items = [System.Collections.Generic.List[string]]::new() }
    'test'  = @{ Title = '테스트';         Items = [System.Collections.Generic.List[string]]::new() }
    'build' = @{ Title = '빌드·패키징';    Items = [System.Collections.Generic.List[string]]::new() }
    'other' = @{ Title = '그 외';          Items = [System.Collections.Generic.List[string]]::new() }
}

$conventional = [regex]'^(?<type>[a-z]+)(\((?<scope>[^)]+)\))?(?<bang>!)?:\s*(?<subject>.+)$'
$breaking = [System.Collections.Generic.List[string]]::new()

# `u{} 이스케이프는 PS 6+ 전용이다 — 5.1 호환을 위해 문자 코드로 만든다.
$separator = [string][char]0x1f

foreach ($line in $lines) {
    if (-not $line) { continue }
    $hash, $subject = $line -split $separator, 2

    $match = $conventional.Match($subject)
    if (-not $match.Success) {
        $sections['other'].Items.Add("- $subject (``$hash``)")
        continue
    }

    $type = $match.Groups['type'].Value
    $scope = $match.Groups['scope'].Value
    $text = $match.Groups['subject'].Value
    $entry = if ($scope) { "- **$scope**: $text (``$hash``)" } else { "- $text (``$hash``)" }

    # `type!:` 은 Conventional Commits 의 파괴적 변경 표기다 — 최상단으로 승격한다.
    if ($match.Groups['bang'].Success) {
        $breaking.Add($entry)
    }

    # chore(standup) 는 작업 일지라 릴리스 노트 소음이다 — 뺀다.
    if ($type -eq 'chore' -and $scope -eq 'standup') { continue }

    $bucket = if ($sections.Contains($type)) { $type } else { 'other' }
    $sections[$bucket].Items.Add($entry)
}

$builder = [System.Text.StringBuilder]::new()
$header = if ($From) { "# 릴리스 노트 ($From → $To)" } else { "# 릴리스 노트 (첫 릴리스, $To 까지)" }
[void]$builder.AppendLine($header)
[void]$builder.AppendLine()

if ($breaking.Count -gt 0) {
    [void]$builder.AppendLine('## ⚠ 파괴적 변경')
    [void]$builder.AppendLine()
    $breaking | ForEach-Object { [void]$builder.AppendLine($_) }
    [void]$builder.AppendLine()
}

foreach ($key in $sections.Keys) {
    $section = $sections[$key]
    if ($section.Items.Count -eq 0) { continue }
    [void]$builder.AppendLine("## $($section.Title)")
    [void]$builder.AppendLine()
    $section.Items | ForEach-Object { [void]$builder.AppendLine($_) }
    [void]$builder.AppendLine()
}

$notes = $builder.ToString()

if ($Output) {
    [System.IO.File]::WriteAllText($Output, $notes, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "저장: $Output" -ForegroundColor Green
}
else {
    $notes
}
