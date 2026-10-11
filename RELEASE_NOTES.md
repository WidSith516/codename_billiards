# REWIND 一杆清台 · Demo 发布说明

> 版本：**demo-0.4**｜构建提交：`70d722b`｜门禁：**471/471 GATE PASS**

---

## 一、发布包

| 项 | 值 |
|---|---|
| 文件 | `REWIND_demo_win64.zip` |
| 大小 | **108 MB** |
| SHA-256 | `8FBC5547C19AFA4AB2C22C07FAE6316A8650DAAE5BF2F73AFB2927CFC28F54F2` |
| 平台 | Windows x86_64 |
| 运行要求 | 无需安装 .NET（运行时已内置），需支持 D3D12 的显卡 |

> **为什么是 108 MB**：Godot 的 C# 导出会把自包含的 .NET 8 运行时（187 个 dll）
> 一起打包。这是 **C# 版的代价**；若改用 GDScript 可降到约 60 MB，但整个内核要重写。

### 校验下载完整性

```powershell
Get-FileHash .\REWIND_demo_win64.zip -Algorithm SHA256
# 应输出 8FBC5547C19AFA4AB2C22C07FAE6316A8650DAAE5BF2F73AFB2927CFC28F54F2
```

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
| 中文化 | ✅ 界面全中文，内置可自由分发的字体 |
| 白洞机制 | ✅ 有冷却，防止反复刷能量（实测最小间隔 0.617s ≥ 冷却 0.5s） |
| 每球类型物理 | ✅ 质量/摩擦/弹性/库边增益/库边加速次数，全部关卡 JSON 可调 |
| **关卡数量** | ❌ **只有 1 关**（另外 4 关已通过容差验证，但没有关卡选择入口） |
| **首页 / 关卡选择 / 设置** | ❌ **未做**（你已指定，是下一步重点） |
| **美术换装** | ❌ **未做**（目前是扁平线稿风） |
| **音频 / 特效** | ❌ **未做** |
| 人工试玩 | ⚠️ **只有你玩过**，尚无第二人反馈 |

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
