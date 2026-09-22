# gcviewer 统计结果 BUG 检查报告

检查时间：2026-06-22
检查范围：`Services/GcLogParser.cs`、`Services/GcReportCalculator.cs`、`Models/GcLogModels.cs`，以及示例日志 `servergc.log`（ZGC）、`servergc2.log`（ZGC）、`servergc_g1.log`（G1）

---

## 一、已修复的 BUG（验证通过）

### BUG 2：G1 元空间 ReservedMb 错填为 CommittedMb — 已修复

**位置**：`Services/GcLogParser.cs:540-545`、`Models/GcLogModels.cs:111`

**原问题**：G1 日志的 Metaspace 行格式为 `used(committed)->used(committed)`，不含 reserved 字段，但原代码把 committed 当成 reserved 写入，导致 G1 日志的"元空间保留"曲线与"已提交"完全相同，与 ZGC 语义不一致。

**修复方式**：
- `GcMetaspaceSample.ReservedMb` 类型由 `double` 改为 `double?`
- G1 分支 `ReservedMb = null`
- `GcReportCalculator.cs:474-475` 增加 `Where(gc => gc.Metaspace?.ReservedMb.HasValue == true)` 过滤

**验证**：G1 日志不再绘制错误的"元空间保留"曲线，ZGC 保留正确语义。

### BUG 7：G1 的 TypeStatistics 按原始 name 分组，类型未翻译 — 已修复

**位置**：`Services/GcLogParser.cs:315`

**原问题**：G1 分支构造 `GcCompletionRecord` 时 `TypeRaw = name`（如 `"Pause Young (Prepare Mixed) (G1 Evacuation Pause)"`），而 `TranslateType` 只识别 `Minor`/`Major`/`Full`，导致 G1 的"按类型统计"中每个 Pause Young 变体成为独立行，无法聚合，类型饼图同样受影响。

**修复方式**：`TypeRaw = TranslateG1Type(name)` —— G1 完成行的 TypeRaw 现在是翻译后的"年轻代GC"/"混合GC"/"Remark暂停"等。

**验证**：TypeStatistics 和类型饼图能正确聚合 G1 事件。

---

## 二、未修复的 BUG

### BUG 1：堆峰值（peakHeapMb）取错列 — 未修复

**位置**：`Services/GcLogParser.cs:418-422`、`Services/GcReportCalculator.cs:75-80`

**问题**：ZGC 的 `Heap Statistics` Used 行有 6 列：

```
Used:  15514M(95%)  15784M(96%)  11382M(69%)  4110M(25%)  15784M(96%)  4104M(25%)
       Mark Start    Mark End     Relocate Start Relocate End  High         Low
```

`ParseHeapLine` 只取 `tokens[0]`（Mark Start）和 `tokens[3]`（Relocate End），**High 列（tokens[4]）从未被解析**。`GcReportCalculator.peakHeapMb` 仍从 `HeapBefore/After` 取 max。

**影响**：
- 当前 `peakHeapMb` = max(15502, 4198) = **15502M**
- 真实堆峰值 High = **15784M**，偏差 ~1.8%

**修复方向**：在 `ParseHeapLine` 解析 Used 行时额外保存 `tokens[4]`（High）作为 `HeapHighMb`，`peakHeapMb` 改用它。

### BUG 3：G1 Concurrent Mark Cycle 的 HeapBefore/HeapAfter 被子 Pause 覆盖 — 未修复

**位置**：`Services/GcLogParser.cs:283, 301-302`

**问题**：G1 的 `Concurrent Mark Cycle` 事件（如 `GC(110264)`）日志里会插入多个独立 Pause 行：

```
GC(110264) Pause Remark      3644M->3644M(6144M) 4.375ms
GC(110264) Pause Cleanup     4094M->4094M(6144M) 0.341ms
GC(110264) Concurrent Mark Cycle 2720.815ms      ← 无堆变化
```

`ParseCollectionLine` 对每个 G1 行都是**直接赋值**（不是 `??=`）：

```csharp
gcEvent.DurationMs = durationMs;       // 第 283 行，每次都覆盖
...
gcEvent.HeapBeforeMb = before;          // 第 301 行
gcEvent.HeapAfterMb = after;            // 第 302 行
```

最终 `gcEvent` 的 HeapBefore/HeapAfter 被 Pause Cleanup 的 `4094->4094` 覆盖，DurationMs 被 Cycle 的 2720.815ms 覆盖，导致 `ReclaimedMb = 0`。

**影响**（cycle 维度统计）：
- `totalReclaimedMb`（`GcReportCalculator.cs:72`）：G1 Cycle 事件贡献 0，偏低
- `peakHeapMb`（第 75-80 行）：取 Cleanup 的 4094，而非真实峰值
- `latestAfterHeapMb`（第 81 行）：若最后是 Cycle，取 4094
- `HeapBeforeSeries`/`HeapAfterSeries`/`HeapReclaimedSeries`（第 378-388 行）：Cycle 数据点显示 4094/4094/0
- `CalculateAfterGcTrend`（第 599-616 行）：HeapAfterMb 序列受 Cycle 干扰

**修复方向**：G1 collection 行对 `gcEvent.HeapBeforeMb/HeapAfterMb` 改用 `??=`；`DurationMs` 只在更大时覆盖或也用 `??=`。

### BUG 4：AllocationStalls 用 `\d+` 贪婪累加所有数字 — 未修复

**位置**：`Services/GcLogParser.cs:555`

**问题**：

```csharp
gcEvent.AllocationStalls += IntegerRegex.Matches(message)
    .Select(match => int.Parse(match.Value, CultureInfo.InvariantCulture)).Sum();
```

`IntegerRegex = \d+` 会匹配行内**所有**数字。ZGC 一个 GC id 会出现 `y:` 和 `O:` 两行 Allocation Stalls（如 `servergc.log:74` 和 `:130`），会被**累加两次**；且每行 4 个阶段值（Mark Start/Mark End/Relocate Start/Relocate End）被 Sum，语义不明（实际应取 Max 或最后值）。

当前 ZGC 日志：

```
Allocation Stalls:          0                0                0                0
```

正好是 4 个阶段值，累加尚算合理。但一旦日志里出现任何额外数字（如版本号、行号），都会被一并计入。

**修复方向**：用精确正则 `Allocation Stalls:\s+(\d+)` 只取第一个值，或明确 Young/Old 是否要累加。

### 问题 5：G1 region size 用堆后值反推 — 未修复

**位置**：`Services/GcLogParser.cs:505`

**问题**：

```csharp
var regionSizeMb = gcEvent.HeapAfterMb.Value / totalAfterRegions;
```

G1 的 region size 是固定值（1/2/4/8/16/32MB），不应反推。这会导致 `Young/Old` 的 `UsedBeforeMb/UsedAfterMb/ReclaimedMb` 全部带误差，并传染到"年轻代/老年代样本"统计、`totalPromoted`、`GenerationStatistics`。

**修复方向**：直接从 G1 日志的 region size 行解析，或至少把估算值标记为"约"。

### 问题 6：G1 的 "Concurrent Mark" 阶段被整体丢弃 — 未修复

**位置**：`Services/GcLogParser.cs:340-343`

**问题**：

```csharp
if (isG1Marking && rawName.Equals("Concurrent Mark", StringComparison.OrdinalIgnoreCase))
{
    return;
}
```

虽然能避免与 `Concurrent Mark From Roots` 重复，但 G1 日志里 `Concurrent Mark 1895.437ms` 是**整个标记阶段的总时长**，直接丢弃会让 `ConcurrentDurationMs` / "并发GC总耗时" 偏小（示例中少了约 1895ms）。

**修复方向**：保留 phase 但在统计时去重，而不是在解析时丢弃。

---

## 三、修复优先级建议

| 优先级 | BUG | 修复位置 | 修复方向 |
|---|---|---|---|
| 高 | BUG 3 | `GcLogParser.cs:283, 301-302` | G1 collection 行对 `gcEvent.HeapBeforeMb/HeapAfterMb` 改用 `??=`；`DurationMs` 只在更大时覆盖或也用 `??=` |
| 高 | BUG 4 | `GcLogParser.cs:555` | 用精确正则 `Allocation Stalls:\s+(\d+)` 只取首个值，明确 y:/O: 是否累加 |
| 中 | BUG 1 | `GcLogParser.cs:420-421` + `GcReportCalculator.cs:75-80` | 解析 `tokens[4]` 存为 `HeapHighMb`，`peakHeapMb` 改用它 |
| 低 | 问题 5 | `GcLogParser.cs:505` | 从日志的 region size 行直接解析 |
| 低 | 问题 6 | `GcLogParser.cs:340-343` | 保留 phase 但统计时去重 |

---

## 四、总结

本次检查共发现 7 个统计相关 BUG，其中：
- **已修复**：2 个（BUG 2、BUG 7）
- **未修复**：5 个（BUG 1、BUG 3、BUG 4、问题 5、问题 6）

建议优先修复 **BUG 3** 和 **BUG 4**，这两个对 G1 和 ZGC 日志的统计数字影响最直接。
