# FILE: jigu-app/tests/make-merge-fixture.ps1
# Builds a fake "remote" data source whose corpus.json is GENUINELY different from the
# shipped one (one extra document appended to an existing book), then rewrites
# data_version.json so the new corpus hash and a higher version number are declared.
#
# Why this exists: the merge path used to key the fetched whole-corpus file by its FILE NAME
# ("corpus") while the local side keys documents by each doc's own "book" field. The two key
# sets never intersect, so the remote corpus was appended as a brand new group instead of
# replacing the local one -- the corpus doubled on every update (measured: 201 -> 403) while
# every check still reported success. A fixture whose corpus is byte-identical cannot expose
# that, because the update never runs at all. Hence: always feed a corpus that really differs.
#
# Usage:
#   powershell -NoProfile -File tests\make-merge-fixture.ps1
#   copy dist\<app> to a scratch dir first, then:
#   <scratch>\<app>.exe --test-update file:///<repo>/test-remote-merge
#
# Expected: 202 docs after the update (201 + 1 sentinel). A "X dup" FAIL means the
# merge is grouping by the wrong key again.
#
# ASCII only, on purpose: PowerShell 5.1 reads .ps1 with the ANSI code page.
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$src  = Join-Path $root "test-remote"
$dst  = Join-Path $root "test-remote-merge"

if (-not (Test-Path (Join-Path $src "corpus.json"))) {
  Write-Host ("missing " + (Join-Path $src "corpus.json"))
  Write-Host "run build.ps1 and copy dist/update to test-remote first (see docs)"
  exit 1
}

if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
New-Item -ItemType Directory -Path $dst | Out-Null
Copy-Item (Join-Path $src "*") $dst -Force

$utf8 = New-Object System.Text.UTF8Encoding($false)
$corpusFile = Join-Path $dst "corpus.json"
$dvFile     = Join-Path $dst "data_version.json"

# Append one sentinel document to the items array. The book name is built from code points
# so this script stays pure ASCII (see header).
$bookName = [string][char]0x79CD + [string][char]0x5B50          # the seed book
$extra = '{"book":"' + $bookName + '","chapter":"test","title":"merge sentinel",' +
         '"original":"aaa","translation":"bbb","figures":[],"decision":"",' +
         '"outcome":"","themes":[]}'

$t = [IO.File]::ReadAllText($corpusFile, [Text.Encoding]::UTF8).TrimEnd()
if (-not $t.EndsWith("]}")) { Write-Host "unexpected corpus shape (expected ...]})"; exit 1 }
$t = $t.Substring(0, $t.Length - 2) + "," + $extra + "]}"
[IO.File]::WriteAllText($corpusFile, $t, $utf8)

$sha = [System.Security.Cryptography.SHA256]::Create()
$hash = ($sha.ComputeHash([IO.File]::ReadAllBytes($corpusFile)) |
         ForEach-Object { $_.ToString('x2') }) -join ''

$dv = [IO.File]::ReadAllText($dvFile, [Text.Encoding]::UTF8)
$dv = [regex]::Replace($dv, '"version":(\s*)"[^"]*"', '"version":$1"9.9.9"')
$dv = [regex]::Replace($dv, '"corpus":(\s*)"[0-9a-f]{64}"', '"corpus":$1"' + $hash + '"')

# Second part: an ADD-ON book published as a separate file, exactly as the hosting doc
# describes it -- and deliberately under a FILE NAME that differs from the book name
# ("books/shiji-extra.json" carrying the book name in $addonBook above). That is the
# documented example,
# and it is the case where keying the merge by file name instead of by the document's own
# book field splits into two groups and duplicates the book on the next round.
$addonBook = [string][char]0x53F2 + [string][char]0x8BB0 + [string][char]0x8865 + [string][char]0x9057
$addonDir = Join-Path $dst "books"
New-Item -ItemType Directory -Path $addonDir | Out-Null
$addon = '{"book":"' + $addonBook + '","items":[' +
         '{"book":"' + $addonBook + '","title":"addon one","original":"x1","translation":"y1"},' +
         '{"book":"' + $addonBook + '","title":"addon two","original":"x2","translation":"y2"}]}'
[IO.File]::WriteAllText((Join-Path $addonDir "shiji-extra.json"), $addon, $utf8)
$addonHash = ($sha.ComputeHash([IO.File]::ReadAllBytes((Join-Path $addonDir "shiji-extra.json"))) |
              ForEach-Object { $_.ToString('x2') }) -join ''

# splice the add-on into books/paths (the JSON is pretty-printed by ConvertTo-Json with a
# single entry per section, so appending one more line is enough)
$dv = [regex]::Replace($dv, '("books":\s*\{\s*"corpus":\s*"[0-9a-f]{64}")(\s*\})',
                       '$1,' + [Environment]::NewLine + '    "' + $addonBook + '": "' + $addonHash + '"$2')
$dv = [regex]::Replace($dv, '("paths":\s*\{\s*"corpus":\s*"[^"]*")(\s*,?)',
                       '$1,' + [Environment]::NewLine + '    "' + $addonBook + '": "books/shiji-extra.json"$2')
[IO.File]::WriteAllText($dvFile, $dv, $utf8)

Write-Host ("fixture dir : " + $dst)
Write-Host ("docs        : " + ([regex]::Matches($t, '"title":').Count) + " corpus + 2 addon")
Write-Host ("corpus sha  : " + $hash)
Write-Host ("addon sha   : " + $addonHash)
