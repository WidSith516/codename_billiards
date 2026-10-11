# REWIND 一杆清台 · Demo 发布说明

> 版本：**demo-0.5**｜构建提交：`4d710c8`｜门禁：**503/503 GATE PASS**

---

## 一、发布包

| 项 | 值 |
|---|---|
| 文件 | `REWIND_demo_win64.zip` |
| 大小 | **108 MB** |
| SHA-256 | `F06E043F3D54317A5AE605160C5740ABED7FA61926A6B3D01752B6634F1C913A` |
| 平台 | Windows x86_64 |
| 运行要求 | 无需安装 .NET（运行时已内置） |

### 校验下载完整性

```powershell
Get-FileHash .\REWIND_demo_win64.zip -Algorithm SHA256
# 应输出 F06E043F3D54317A5AE605160C5740ABED7FA61926A6B3D01752B6634F1C913A
```

### ⚠️ 关于「内存写入不能为 written」崩溃

**默认启动已改用 `opengl3`**，与 `project.godot` 里声明的 `"GL Compatibility"` 特性一致
（此前启动脚本强制 `d3d12`，与声明不符，是最可疑的诱因）。

若仍出问题，dist 目录里有两个备用启动器可直接 A/B：

| 启动器 | 后端 | 何时用 |
|---|---|---|
| `启动游戏.bat` | **opengl3（默认）** | 首选，最稳 |
| `启动游戏-d3d12.bat` | d3d12 | 想要更高性能时试 |
| `启动游戏-opengl3.bat` | opengl3 | 与默认相同，显式版本 |

**诚实说明**：我**无法在本机复现**那个弹框，所以这是**针对最可能原因的缓解**，不是确诊修复。
要真正定位，我需要那个弹框的**原话英文文本**（或截图）。

---

## 二、怎么发布（需要你手动做一次）

**我没有 GitHub 凭据，也没有 `gh` CLI，所以上传这一步必须你来。**
（`git push` 已成功，代码已经在远程了 —— 只是 108 MB 的 zip **不适合放进 git 历史**。）

### 方式 A：网页操作（推荐，最快）

1. 打开 https://github.com/WidSith516/codename_billiards/releases/new
2. **Choose a tag**：输入 `demo-0.4` → 点 "Create new tag"
3. **Release title**：`REWIND 一杆清台 · demo-0.4`
4. **Describe this release**：把本文件第三节「给试玩者的话」整段贴进去
5. **Attach binaries**：把 `REWIND_demo_win64.zip` 拖进去
6. 点 **Publish release**

### 方式 B：命令行（若你装了 gh）

```powershell
gh release create demo-0.4 REWIND_demo_win64.zip `
  --title "REWIND 一杆清台 · demo-0.4" `
  --notes-file RELEASE_NOTES.md
```

---

## 三、给试玩者的话（可直接贴进 Release 说明）

> **你只能打一杆。**
>
> 但你可以按住 `R` 让白球沿它走过的完整历史**倒着走回来**——而球桌不会跟着倒回。
> 别的球还在滚、还在撞，所以白球退回旧路时，撞到的东西已经和当初不一样了。
> 在任意时刻松开 `R`，白球就带着当时的逆向速度冲出去，走出一条全新的路。
>
> **一杆、一条路、把球清完。** 这就是全部。
>
> ---
> **怎么玩**
> - `拖动后松开` 瞄准 / 击球（每关**只有一杆**）
> - `按住 R` 回溯白球 → `松开 R` 保留当时的逆向速度
> - `P` 回溯中暂停并微调释放点
> - `F1` **隐藏左侧面板**（想看清球桌时按）
> - `F2` / `F3` 切换残影 / 轨迹
> - `空格` 重置本关
>
> **青色发光圆环是「白洞」**：白球穿过它会被加速。
> 白球是唯一能被回溯的球，所以它没速度了这关就结束了——白洞就是让你**把白球重新救回来**的地方。
>
> ⚠️ 这是**玩法切片**，不是完整游戏：目前**只有 1 关**，没有关卡选择、
> 没有音乐、界面是简版。请重点反馈**玩法本身是否成立**，而不是完成度。

---

## 四、当前这一步做到了什么（诚实版）

| 项 | 状态 |
|---|---|
| 玩法成立 | ✅ 一杆 + 回溯 + 白洞，可通关（导出包实机自检 `won=True`） |
| **首页** | ✅ 开始 / 关卡选择 / 个性化 / 设置 四态菜单 |
| **关卡选择** | ✅ 5 关，含难度点、星级、含白洞标记、锁定原因 |
| **新手引导** | ✅ 现有关卡上的浮动提示气泡（击球 → 回溯 → 白洞），**做到才推进** |
| **音效** | ✅ **程序化合成 11 个音效**，零音频素材、无需下载授权 |
| 中文化 | ✅ 界面全中文，内置可自由分发的字体 |
| 白洞 | ✅ 单个、靠右库边；出洞方向为「从圆心向外推」；带冷却防刷 |
| 球种物理 | ✅ 质量/摩擦/弹性/库边增益/加速上限，全部关卡 JSON 可调 |
| **美术换装** | 🔶 四套配色主题已接入设置页，但**球台与球的质感仍是简版** |
| **3 分钟切片** | ❌ **未录制**（编排已写好在 `docs/DEMO_SLICE_3MIN.md`） |
| 人工试玩 | ⚠️ **只有你玩过**，尚无第二人反馈 |

### 已知问题

1. **「内存写入」崩溃未确诊** —— 见第一节，默认已切 opengl3。
2. 导出包在 opengl3 下的 `--out=` 截图偶发挂起（**只影响自动截图，不影响游玩**）。
3. 4 个 `l*` 关卡各自可解，但**尚未逐关实测过**在当前物理参数下的完整通关。

---

## 五、重建这个包

```powershell
# 0) 导出时 PATH 上必须先是官方 dotnet，否则 dotnet publish 会失败，
#    而 Godot 仍返回 rc=0 并产出一个【没有 C# 程序集】的空壳 exe。
$env:PATH = "C:\Program Files\dotnet;" + $env:PATH
$env:NUGET_PACKAGES = "D:\codename\codename_billiards\godot\.tmp\clihome\.nuget\packages"

# 1) 导出
tools\godot\godot_console.exe --headless --path godot `
  --export-release "Windows Desktop" dist\REWIND.exe

# 2) 确认程序集真的进去了（104 MB 的空壳也有同样大小）
Get-ChildItem dist -Recurse -Filter RewindKernel.dll

# 3) 实机自检
dist\REWIND.exe --rendering-driver d3d12 --headless -- --liveselftest

# 4) 打包
Compress-Archive -Path dist\* -DestinationPath REWIND_demo_win64.zip
```
