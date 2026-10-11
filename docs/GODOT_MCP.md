# REWIND Godot MCP 与 H5 素材复用

连接名：`godot_rewind`。项目配置：`.codex/config.toml`。
端点：`http://127.0.0.1:19085/mcp`，仅监听本机。
工程：`C:/MyProject/1011gamejam/godot`。

运行 `tools/start_godot_mcp.ps1` 启动后台编辑器；已连接当前工程时直接复用。
如需使用可见编辑器，可在 Godot 的 MCP 面板使用 HTTP、端口 19085 并启动服务。
同一端口只运行一个服务。Codex 重新加载项目配置后可使用新连接。

插件从本机 fish-ecosystem 工程复用 Godot MCP Native 1.0.8，MIT 许可证随附于
`godot/addons/godot_mcp/LICENSE`。移除了鱼缸专用工具注册；修正了面板刷新重复设置
相同端口时的误报。导出配置排除了 MCP 插件，运行游戏无需连接服务。

H5 原型没有外部图片、音频，球桌与台球由 Canvas 绘制。复用内容：

- `godot/assets/h5/table.svg`：从 `h5/index.html` 的 `renderTable()` 转写木框渐变、
  绿毡渐变、织物点、开球线和库边刻度；袋口仍按关卡数据绘制。
- `BallStyles.cs`：复用 `renderBall()` 的偏心球面渐变（白色、球色、深绿边缘）与下投影；
  保留 Godot 的球号、类型符号和易辨识配色。
- `Play.cs`：按关卡桌面尺寸映射素材，使用 1:1 设计比例呈现完整桌面与六个袋口。

H5 原文件保持原样。未引入独立美术生成流程。

验证：合入远端首页、教程、音频和白洞更新后，C# 构建 0 警告、0 错误，回归 503/503 通过；MCP 握手、工程读取、
场景打开、运行、运行树读取、截图及停止均成功。运行 stderr 为空。
截图：`godot/evidence/h5_reuse.png`，通过非空图像检查。临时运行探针已移除。
