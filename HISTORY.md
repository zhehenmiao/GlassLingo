# 历史版本归档

旧版程序与已有源码包保存在 [GitHub Releases 历史归档](https://github.com/zhehenmiao/GlassLingo/releases/tag/historical-versions-2026-11-12)。当前源码仍以 main 和 v0.17.0 为准。

归档包含 **24 个旧版本、33 个文件集合**：27 个旧程序目录、5 个原始 ZIP 的成员文件，以及一组历史校验和记录。原文件约 15.85 GiB；相同内容按 SHA-256 只保存一次，归档约 328 MiB。

可恢复的程序版本：

| 版本 | 本机保留的原始源码 |
|---|---|
| 0.5.0 | 无，仅程序 |
| 0.5.1 | 无，仅程序 |
| 0.6.0 | 有 |
| 0.6.1 | 有 |
| 0.6.2 | 有 |
| 0.7.0 | 无，仅程序 |
| 0.7.1 | 无，仅程序 |
| 0.7.2 | 无，仅程序 |
| 0.7.3 | 无，仅程序 |
| 0.8.0 | 无，仅程序 |
| 0.9.0 | 无，仅程序 |
| 0.10.0 | 无，仅程序 |
| 0.11.0 | 无，仅程序 |
| 0.12.0 | 无，仅程序 |
| 0.13.0 | 无，仅程序 |
| 0.13.1 | 无，仅程序 |
| 0.14.0 | 无，仅程序 |
| 0.14.1 | 无，仅程序 |
| 0.15.0 | 无，仅程序 |
| 0.15.1 | 无，仅程序 |
| 0.15.2 | 无，仅程序 |
| 0.15.3 | 无，仅程序 |
| 0.15.4 | 无，仅程序 |
| 0.16.0 | 无，仅程序 |

## 下载和恢复

从历史归档的 Assets 下载 `GlassLingo-historical-versions.zip`、`Restore-HistoricalVersion.ps1` 和 `SHA256SUMS.txt`。这个归档使用共享内容存储，需通过脚本重建版本目录，不是直接解压运行的普通程序包。

使用 PowerShell 7，先将 `Get-FileHash` 输出与 `SHA256SUMS.txt` 比对，再列出版本或恢复：

```powershell
Get-FileHash ./GlassLingo-historical-versions.zip -Algorithm SHA256
pwsh -File ./Restore-HistoricalVersion.ps1 -Archive ./GlassLingo-historical-versions.zip -List
pwsh -File ./Restore-HistoricalVersion.ps1 -Archive ./GlassLingo-historical-versions.zip -Version 0.16.0 -Destination ./restored-history
```

最后一条命令创建 `restored-history/GlassLingo-0.16.0`，逐文件核对 SHA-256。已存在的目标目录不会被覆盖。恢复后请保留整个程序目录；手动运行前先退出当前版本，脚本不会自动启动程序。

选择特定集合可恢复原始源码或另一个发布副本：

```powershell
pwsh -File ./Restore-HistoricalVersion.ps1 -Archive ./GlassLingo-historical-versions.zip -Collection GlassLingo-0.6.0-source-zip -Destination ./restored-history
```

`-List` 可查看精确集合名；`-VerifyOnly` 可完整校验归档内容。脚本也保存在仓库的 [scripts/Restore-HistoricalVersion.ps1](scripts/Restore-HistoricalVersion.ps1)。

## 保存范围与验证

程序、运行库、模型、随附文档和许可文件按内容保存。测试截图、生成的诊断记录、日志、个人配置及设计工具状态已排除。原始 ZIP 保留成员文件和修改时间，未保留原容器压缩字节；附带的旧校验和文本仅作历史记录，不能用于校验新归档。

归档中的 778 份唯一内容文件已全部通过 SHA-256 校验；实际恢复检查覆盖 0.5.0、0.16.0 和 0.6.0 源码，并确认脚本拒绝覆盖已有目录。此项检查验证归档与还原完整性，没有重新验收旧版功能。

历史归档标签只标记归档说明所在的提交，不代表各旧版原始源码提交。GitHub 自动生成的 “Source code” 下载对应归档标签的仓库快照；旧程序请下载上述具名 ZIP，早期源码请通过对应集合恢复。
