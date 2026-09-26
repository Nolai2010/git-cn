# 把 git-cn 装到用户目录并加入用户 PATH。原 PATH 会先备份。
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'git-cn'
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'git_cn.py'),
          (Join-Path $PSScriptRoot 'git-cn.cmd'),
          (Join-Path $PSScriptRoot 'git-cn') $target -Force
Write-Host "已复制文件到 $target"

$cur = [Environment]::GetEnvironmentVariable('Path', 'User')
if (-not $cur) { $cur = '' }
Set-Content -Path (Join-Path $target 'PATH-backup.txt') -Value $cur -Encoding utf8
Write-Host "原用户 PATH 已备份到 $target\PATH-backup.txt"

if ($cur -notlike '*git-cn*') {
    [Environment]::SetEnvironmentVariable('Path', ($cur.TrimEnd(';') + ';' + $target), 'User')
    Write-Host "已把 $target 加入用户 PATH"
} else {
    Write-Host '用户 PATH 里已经有 git-cn，跳过'
}
Write-Host ''
Write-Host '下一步：重开一个终端，运行   git-cn doctor'
Write-Host '想卸载：把上面那行 PATH 项删掉，再删除该目录即可（PATH-backup.txt 里有原值）'
