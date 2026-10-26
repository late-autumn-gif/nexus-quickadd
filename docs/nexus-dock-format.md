# Nexus 停靠栏项目格式（逆向记录）

本文记录 Winstep **Nexus 26.9 build 1287** 上实测得到的停靠栏数据结构。工具的全部写入逻辑都基于这些结论，换版本请先用 `reg export` 备份再验证。

## 存储位置

```
HKCU\Software\WinSTEP2000\NeXuS\Docks
```

| 值名 | 类型 | 含义 |
| --- | --- | --- |
| `DockNoItems1` | REG_SZ | 最高项目下标（0 起）。示例：19 表示 20 个项目 |
| `1Path{i}` | REG_SZ | 启动目标：exe 绝对路径或 `.lnk` 路径 |
| `1Label{i}` | REG_SZ | 显示名（停靠栏标签） |
| `1Type{i}` | REG_SZ | 项目类型，`1` = 程序 |
| `1StartPath{i}` | REG_SZ | 可选，"起始位置" |
| `1Hotkey{i}` | REG_SZ | 可选，项目快捷键（本工具不写） |

其余 `DockXxx1` 是停靠栏外观/行为配置（位置、图标大小、主题、特效…），与项目列表无关。

## 实测行为

1. **启动时读取**：Nexus（VB6，窗口类 `ThunderRT5Form`，主窗口标题 `NxDock`）在进程启动时按 `0..DockNoItems1` 顺序构建图标行；运行中改注册表不会刷新界面。
2. **退出时重写**：Nexus 保存设置会把整个键重写，外部写入必须在它退出之后进行，否则会被覆盖。
3. **无法优雅关闭**：`WM_CLOSE` 发给 `NxDock` 只会销毁窗口，进程变成半死状态（窗口消失、进程无响应）；`SC_CLOSE`、`WM_QUIT` 同样无效。可靠做法是结束进程后重新拉起。
4. **追加项目**：写 `1Path{i}` / `1Label{i}` / `1Type{i}`，把 `DockNoItems1` 提到新下标，重启 Nexus 后图标出现在末尾，停靠栏宽度随之增长（实测 18 项 1524px → 19 项 1574px → 21 项 1674px）。
5. **删除项目**：反向操作——删掉对应 `1*{i}` 值，把 `DockNoItems1` 减 1。
6. **快捷方式承载**：带命令行参数的程序、UWP 应用用 `.lnk` 存进 `1Path{i}`（Nexus 会解析它），与 Nexus 自己拖入商店应用的行为一致。UWP 的 lnk 目标为 `explorer.exe`，参数 `shell:AppsFolder\<AUMID>`。
7. **图标**：Nexus 从目标文件里取图标，exe 内嵌的 icon 会直接显示，无需额外配置。
8. **多停靠栏**：键名按停靠栏序号前缀区分（`1Path*` 属于第 1 个），本工具只处理第 1 个。

## 界面对象

| 对象 | 类名 | 说明 |
| --- | --- | --- |
| 停靠栏窗口 | `ThunderRT5Form` / 标题 `NxDock` | 图标行所在窗口，宽度随项目数变化 |
| 主窗口 | `ThunderRT5Main` / 标题 `NeXuS` | 0×0 隐藏窗口 |
| 其它 | `WsxMMTimer`、`WorkShelf` | 内部组件窗口 |

判断"重启完成"的方法：轮询 `FindWindow("ThunderRT5Form", "NxDock")` 是否返回非零句柄。

## 写入流程（本工具的做法）

1. `reg export HKCU\Software\WinSTEP2000` 备份；
2. 结束所有 `Nexus` 进程并等待退出；
3. 写 `1Path/1Label/1Type/1StartPath` 与 `DockNoItems1`；
4. 重新启动 `Nexus.exe`（清掉 `ELECTRON_RUN_AS_NODE` / `NODE_OPTIONS`，否则从停靠栏启动 Electron 应用会秒退）；
5. 轮询停靠栏窗口就绪，失败则提示"正在重启"。
