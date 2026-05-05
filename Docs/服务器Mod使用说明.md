# 烨夜服 Mod 使用说明

适用版本：Minecraft 1.21.11 / Fabric  
整合包版本：2026.05.02.2  
服务器地址：luckcclove.xyz:57733

这份文档给普通玩家使用，主要说明：服务器官方 Mod 怎么同步、常用 Mod 怎么打开、玩家自己想加客户端 Mod 应该放在哪里，以及出问题时怎么排查。

## 1. 官方整合包怎么使用

1. 打开 `烨夜服 Launcher`。
2. 输入你的游戏昵称。
3. 服务器选择保持 `官方服务器（推荐）`。
4. 点击 `开始`。
5. 启动器会自动检查 Java、Minecraft、Fabric 和服务器必需 Mod。
6. 自动同步完成后，游戏会直接启动并连接烨夜服。

官方服务器模式会自动保持服务器需要的 Mod 一致。玩家一般不需要手动下载官方 Mod。

## 2. 如何打开 mods 文件夹

方法一：从启动器打开

1. 打开启动器。
2. 点击 `更多选项`。
3. 点击 `打开 mods 文件夹`。

方法二：直接打开本地目录

```text
D:\ArclightLauncher\.minecraft\mods
```

玩家自己添加的客户端 Mod 也放在这个文件夹里。放入新的 `.jar` 文件后，需要重启游戏才会生效。

## 3. 玩家可以自己添加什么 Mod

可以添加的类型：

- 小地图、世界地图、路径点类
- HUD、背包显示、状态栏显示类
- 截图、缩放、光影、画质优化类
- 按键优化、鼠标操作优化类
- 音效、粒子、披风、皮肤显示类
- 只影响自己客户端显示或操作体验的 Fabric Mod

不建议添加的类型：

- 新方块、新物品、新生物、新维度
- 地形生成、结构生成、世界机制修改
- 需要服务器也安装的内容型 Mod
- Forge / NeoForge Mod
- 与 Minecraft 1.21.11 或 Fabric 不匹配的 Mod

如果添加后游戏崩溃，先删除你最近放进去的 `.jar` 文件，再重新启动。

## 4. 常用 Mod 打开方式

如果下面的默认按键没有反应，请进入游戏后打开：

```text
选项 -> 控制 -> 按键绑定
```

然后搜索 Mod 名称，例如 `Xaero`、`Litematica`、`MiniHUD`、`Tweakeroo`、`Zoomify`。

| 功能 | Mod | 打开方式 / 使用方式 |
| --- | --- | --- |
| 小地图 | Xaero's Minimap | 常见入口：按键绑定里搜索 `Xaero`；可打开小地图设置、路径点、新建路径点 |
| 世界地图 | Xaero's World Map | 常见默认键：`M` 打开大地图；如冲突请在按键绑定里搜索 `World Map` |
| 方块 / 实体信息 | Jade | 对准方块或生物会自动显示信息；设置入口可在 `Mod Menu` 或按键绑定中查找 |
| 饥饿值 / 饱和度 | AppleSkin | 自动显示食物恢复量和饱和度，不需要手动打开 |
| 动态光源 | LambDynamicLights | 手持火把等发光物品会自动发光；设置可在 `Mod Menu` 中查找 |
| 背包 HUD | Inventory HUD+ | 可显示背包、药水、装备状态；入口在 `Mod Menu` 或按键绑定搜索 `Inventory HUD` |
| 背包整理 | Inventory Profiles Next | 背包界面会出现整理按钮；详细设置在 `Mod Menu` 中 |
| 潜影盒预览 | ShulkerBoxTooltip | 鼠标悬停潜影盒，通常按住 `Shift` 可查看内容 |
| 物品信息 | Held Item Info | 自动显示手持物品信息，不需要手动打开 |
| 药水效果条 | Status Effect Bars | 自动增强状态效果显示，不需要手动打开 |
| 更好的 F3 | BetterF3 | 按 `F3` 查看更清晰的调试信息；设置在 `Mod Menu` 中 |
| 更好的进度页 | Better Advancements | 打开原版进度界面时自动生效 |
| 更好的统计页 | Better Statistics Screen | 打开统计界面时自动生效 |
| 聊天头像 | Chat Heads | 聊天栏自动显示玩家头像 |
| 服务器延迟显示 | Better Ping Display | 多人服务器列表或相关界面自动显示更清晰的延迟信息 |
| 缩放 | Zoomify | 常见默认键是 `C`；如冲突请在按键绑定里搜索 `Zoomify` |
| 高清截图 | Fabrishot | 在按键绑定中搜索 `Fabrishot` 设置截图快捷键和倍率 |
| 无边框窗口 | Cubes Without Borders | 自动优化窗口显示；设置可在 `Mod Menu` 中查找 |
| 输入法冲突修复 | IMBlocker | 自动修复游戏内中文输入法焦点问题 |
| 自动汉化更新 | I18nUpdateMod | 自动更新部分 Mod 汉化，不需要手动打开 |
| 万用皮肤补丁 | CustomSkinLoader | 自动加载皮肤；配置文件在 `.minecraft\CustomSkinLoader` |
| 3D 皮肤层 | 3D Skin Layers | 自动让皮肤外层更立体 |
| 披风显示 | WaveyCapes | 自动让披风更自然；设置在 `Mod Menu` 中 |

## 5. 建筑和辅助工具

这些 Mod 功能较强，第一次用建议先在单人世界测试。

| 功能 | Mod | 打开方式 / 使用方式 |
| --- | --- | --- |
| 投影 | Litematica | 常见入口：按键绑定搜索 `Litematica`；常见默认键为 `M` 打开菜单 |
| 投影打印机 | Litematica Printer | 配合 Litematica 使用；设置入口在 `Mod Menu` 或按键绑定中 |
| 投影依赖库 | MaLiLib | Litematica / MiniHUD / Tweakeroo 的基础库；一般不需要单独打开 |
| 迷你 HUD | MiniHUD | 常见组合键：`H + C` 打开配置；也可在按键绑定搜索 `MiniHUD` |
| 操作增强 | Tweakeroo | 常见组合键：`X + C` 打开配置；也可在按键绑定搜索 `Tweakeroo` |
| 地毯 | Carpet | 偏技术向功能；普通玩家不需要手动打开 |
| 鼠标优化 | Mouse Tweaks | 背包拖拽和物品移动自动增强 |

Litematica 投影文件通常放在：

```text
D:\ArclightLauncher\.minecraft\schematics
```

如果没有 `schematics` 文件夹，可以自己创建。

## 6. 画质和性能相关

| 功能 | Mod | 打开方式 / 使用方式 |
| --- | --- | --- |
| 渲染优化 | Sodium | 打开 `选项 -> 视频设置`，会看到优化后的视频设置界面 |
| 更多视频设置 | Sodium Extra | 在视频设置中增加更多细节选项 |
| 视频设置界面优化 | Reese's Sodium Options | 让 Sodium 设置界面更清晰 |
| 光影加载 | Iris | 打开 `选项 -> 视频设置 -> 光影包` |
| 云层优化 | Better Clouds | 设置入口在 `Mod Menu` 或视频设置相关页面 |
| 连接纹理 | Continuity | 通常需要启用对应资源包；在资源包界面查看 |
| 实体剔除 | EntityCulling | 自动优化，看不到的实体不渲染 |
| 方块剔除 | MoreCulling | 自动优化，看不到的面尽量不渲染 |
| 快速渲染 | ImmediatelyFast | 自动优化 GUI 和渲染性能 |
| 物理优化 | Lithium | 自动优化游戏逻辑性能 |
| 动态帧率 | Dynamic FPS | 游戏在后台时自动降低资源占用 |
| 快速退出 | FastQuit | 退出世界时减少等待 |
| 快速加载 | RRLS | 加快资源重载过程 |
| 漏洞修复 | Debugify | 自动修复一些客户端问题 |

## 7. 氛围和视觉增强

这些 Mod 主要改善观感，通常不需要手动打开：

- Sound Physics Remastered：更真实的声音传播效果
- Visuality：粒子效果增强
- Wakes：水面尾迹效果
- Particle Rain：雨雪粒子增强
- Explosive Enhancement：爆炸视觉效果增强
- Better Clouds：云层效果增强

如果觉得掉帧，可以先在 `Mod Menu` 或视频设置中降低这些视觉效果。

## 8. Mod Menu 怎么用

整合包内置 `Mod Menu`。

进入游戏主菜单后，通常可以看到 `Mods` 或 `模组` 按钮。点击后可以：

- 查看已加载 Mod
- 打开部分 Mod 的配置界面
- 检查 Mod 版本
- 快速找到某个 Mod 的设置入口

如果某个 Mod 没有配置按钮，说明它可能是自动生效或依赖库。

## 9. 常见问题

### 游戏崩溃了怎么办

1. 如果刚添加过自己的 Mod，先删除最近添加的 `.jar`。
2. 确认 Mod 是 `Fabric` 版本，不是 Forge / NeoForge。
3. 确认 Mod 支持 Minecraft `1.21.11`。
4. 不要一次添加很多 Mod，建议一次只加 1 到 3 个。
5. 如果仍然崩溃，把下面目录里的最新崩溃报告发给管理员：

```text
D:\ArclightLauncher\.minecraft\crash-reports
```

### 服务器进不去怎么办

1. 使用启动器的 `官方服务器（推荐）` 启动。
2. 等待启动器自动同步完成。
3. 不要删除官方整合包里的必需 Mod。
4. 如果提示 Mod 不匹配，先移除自己额外添加的 Mod 再试。

### 自己添加的 Mod 不生效

1. 确认 `.jar` 放在 `mods` 文件夹，不是压缩包里面。
2. 确认添加后已经重启游戏。
3. 确认 Mod 是客户端 Fabric Mod。
4. 部分 Mod 需要依赖库，缺依赖时游戏会提示。

## 10. 管理员说明

玩家自己添加的客户端 Mod 会保留在 `mods` 文件夹中。  
官方整合包仍由启动器自动同步，官方 Mod 更新以 GitHub manifest 为准。  
如果某个玩家自加 Mod 导致崩溃，让玩家先删除最近添加的 jar，再重新启动。

