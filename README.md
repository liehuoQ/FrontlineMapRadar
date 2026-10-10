# Frontline Map Radar（个人自用）

国服 XIVLauncherCN / Dalamud 插件：进入**纷争前线**并打开大地图后，在地图上用色点标注周围已加载玩家；并记录本场玩家名册，支持场地标记跟随。

- 红：黑涡团
- 蓝：恒辉队
- 黄：双蛇党

**名册**（`/flroster`）：自动扫描本场见过的玩家，显示名字 / 服务器 / 阵营 / 职业 / 职能 / **当前携带的职能技能**，并可分配 A–D、1–4 标记跟随目标。

职能技能为每职能三选一（灰机 Wiki）。自己可从 DutyAction / PvP Profile 直接读取；其他玩家无法读配置，需在其咏唱或施放该技能后才会记入名册。

**远程雷达**：设置里开启「局域网网页同步」后，同一 WiFi 下的平板用浏览器打开显示的地址（默认 `http://电脑IP:17890/`），即可实时看敌我点位。


## 环境

- XIVLauncherCN（Soil）+ Dalamud
- .NET SDK 10
- 本机已有：`%AppData%\XIVLauncherCN\addon\Hooks\dev\`

## 编译

```powershell
cd "d:\my demo\FrontlineMapRadar"
dotnet build FrontlineMapRadar.sln -c Release
```

输出目录：`FrontlineMapRadar\bin\x64\Release\`

## 安装（开发中插件）

产物已复制到：

`%AppData%\XIVLauncherCN\devPlugins\FrontlineMapRadar\`

在游戏内完成绑定：

1. **Dalamud 设置 → 常规**：打开 **启用插件开发者模式**（DevMode）
2. **Dalamud 设置 → 实验性功能 / 开发中插件**：添加目录  
   `%AppData%\XIVLauncherCN\devPlugins\FrontlineMapRadar`
3. 打开插件安装器，在 **开发中插件** 页启用 **Frontline Map Radar**
4. 命令：
   - `/flradar` 打开设置
   - `/flroster` 或 `/flradar roster` 打开本场玩家名册
   - `/flradar remote` 开关远程雷达并打开设置

重新编译后覆盖同目录 `dll/json` 即可热更（或重载插件）。

## 使用

1. 进入纷争前线战场。
2. 按 `M` 打开大地图。
3. 地图上应出现周围玩家色点。

### 平板第二屏

1. `/flradar` → 勾选 **启用局域网网页同步**。
2. 复制设置里显示的 `http://x.x.x.x:17890/`。
3. 平板连同一 WiFi，浏览器打开该地址。
4. 若无法访问：在 Windows 防火墙入站规则中放行 TCP `17890`（或你改的端口）。

荣誉野 / 尘封秘岩 / 昂萨哈凯尔 / 沃戈尔·奇尔特 使用 `Assets/maps` 羊皮纸底图；进入对应前线会自动切换。默认校准已按装饰边框内侧对齐，可在网页「校准」或设置里微调后保存。

## 说明

- 地图坐标换算参考开源 [KirisameVanilla/Radar](https://github.com/KirisameVanilla/Radar) 对 `AreaMap` 的处理。
- 第三方插件有封号风险，仅建议个人评估后使用。
