# AGENTS.md

本文件给后续在本仓库工作的代理使用。请优先遵守这里的项目约定，再结合用户的具体请求行动。

## 项目概览

这是一个 .NET 8 WPF 桌面应用，用于解析 JDK Unified GC 日志并生成中文报表。项目没有第三方 NuGet 依赖，核心实现集中在解析、统计、ViewModel 和自绘图表四块。

主要文件：

- `gcviewer.sln`：解决方案入口。
- `gcviewer.csproj`：WPF 项目，目标框架 `net8.0-windows`。
- `MainWindow.xaml`：主界面布局、Tab、表格和图表绑定。
- `MainWindow.xaml.cs`：窗口初始化并设置 `MainViewModel`。
- `ViewModels/MainViewModel.cs`：文件选择、后台解析、进度、时间范围切换、报表绑定。
- `Services/GcLogParser.cs`：GC 日志解析器，按 GC id 聚合事件并生成完成记录。
- `Services/GcReportCalculator.cs`：从解析结果生成 KPI、统计表、明细行和图表序列。
- `Models/GcLogModels.cs`：解析结果、报表 DTO、图表模型和格式化工具。
- `Controls/SimpleChart.cs`：自定义 WPF 图表控件，支持折线、面积、柱状和环形图。
- `BUG检查报告.md`：历史问题检查记录，可作背景参考，但不要假设它一定与当前源码完全同步。

## 构建与验证

推荐验证命令：

```powershell
dotnet build gcviewer.sln
```

运行应用：

```powershell
dotnet run --project gcviewer.csproj
```

注意：这是 WPF 桌面程序，运行需要 Windows 桌面环境。当前仓库没有正式测试项目；如果修改解析或统计逻辑，应至少用 `servergc.log`、`servergc2.log`、`servergc_g1.log` 做本地验证，必要时补充小型验证程序或单元测试。

## 工作区约定

- 不要手动编辑 `bin/`、`obj/`、`release/`、`.vs/` 下的构建产物。
- 不要提交 `*_wpftmp.csproj` 或 WPF 临时项目文件。
- `servergc*.log` 是体积较大的样本日志，当前 `.gitignore` 已忽略这类文件；分析时可以读取，但不要把新的大日志纳入版本控制。
- 保持中文 UI 文案一致；如果新增面向用户的文本，优先使用简洁中文。
- 解析和统计逻辑对业务含义敏感。修改 `GcLogParser` 或 `GcReportCalculator` 时，应明确说明影响的是“GC id 聚合维度”还是“完成行维度”。

## 实现注意事项

- `GcLogParser` 使用正则解析 JDK Unified GC 日志。新增格式时，优先添加窄范围正则和示例验证，避免放宽现有匹配导致误识别。
- ZGC 与 G1 的日志结构不同：ZGC 多数指标来自 `Minor/Major/Full Collection` 和 `gc,heap` 明细；G1 还包含 `Pause Young`、`Pause Remark`、`Pause Cleanup`、`Concurrent Mark Cycle` 以及 region 行。
- `GcParseResult.Events` 是按 GC id 聚合后的事件；`CompletionRecords` 是每条带耗时 `[gc]` 完成行。报表里两者都会用到，不要混淆统计口径。
- `SimpleChart` 是自绘控件，不依赖图表库。改图表渲染时要注意大数据量性能、tooltip 命中区域、图例可见性切换和坐标轴显示。
- UI 使用基础 MVVM，没有引入框架。新增命令时复用 `RelayCommand` 和 `INotifyPropertyChanged` 模式即可。

## 代码风格

- 遵循当前 C# 风格：file-scoped namespace、`sealed` 类型、`required` init-only DTO、显式小方法。
- 保持 nullable 语义清晰；缺失日志字段优先用 `double?` / `null` 表达，不要用 `0` 伪装未上报。
- 只添加必要注释。复杂 GC 统计口径可以加短注释说明原因。
- 修改完成后运行 `dotnet build gcviewer.sln`，并在最终回复中说明验证结果。
