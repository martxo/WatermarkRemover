# 文件类型识别 + 客户姓名提取 + 输出自动命名 — 实现规划

## Context

去水印后的输出文件目前按「原名_no-watermark」命名，用户需要按内容自动命名：先识别文件类型（营业执照、户口本、身份证…后续持续新增），再提取客户姓名（姓名前的标注词随主体类型不同），按「{客户姓名}{文件类型}」命名（如「张三执照」）。核心诉求是**模式识别式架构**——新增一种文件类型只加一个"画像"数据，不改流程代码。使用场景：对 PDF 去水印之后自动命名（重命名生成文件，不动源文件）。

## 法规调研结论（法定依据）

《市场主体登记管理条例实施细则》第二十三条：营业执照载明「法定代表人（执行事务合伙人、个人独资企业投资人、经营者或者负责人）姓名」。按主体类型：

| 主体类型 | 姓名标注词 |
|---|---|
| 公司 / 非公司企业法人 / 农民专业合作社 | 法定代表人 |
| 个人独资企业 | 投资人 |
| 合伙企业 | 执行事务合伙人（负责人） |
| 个体工商户 | 经营者（组成形式：个人经营/家庭经营） |

真样张实测（扫描全能王.pdf，个体户执照）：微信 OCR 输出含「组成形式个人经营」「者林孝明」——注意 **OCR 把"经营者"标签截断成"者"**，姓名提取必须容忍标签截断与跨行。

## 架构设计（Core 新目录 `Features\Naming\`，纯逻辑、无 UI 依赖）

```
Features\Naming\
  DocumentProfile.cs        // 类型画像（数据描述符）
  DocumentProfileCatalog.cs // 注册表：All = { 执照, 身份证, 户口本 }
  DocumentClassifier.cs     // 锚点评分分类器
  NameExtractor.cs          // 姓名提取（容忍 OCR 标签截断/跨行）
  SurnameTable.cs           // 常见姓氏 + 姓名校验
  SmartNamingService.cs     // 门面：OCR 文本行 → AutoNameResult
  AutoNameResult.cs         // (PersonName, TypeShortName) → CanName
```

**DocumentProfile 字段**：TypeId、TypeShortName（命名用短名，与 QuickSuffixes 标签对齐：执照/户口本/身份证）、Anchors（正则+权重 1~5）、NameLabels（标注词集合）、NameValuePattern（可选覆盖）。

**新增文件类型 = 在 Catalog 注册一个画像对象，零流程改动。**

### 分类评分（DocumentClassifier）

- 各画像 Anchors 正则命中计分求和（同一锚点多次命中只计一次，防关键词堆叠刷分）；
- 判定阈值：最高分 ≥ 6 且比第二名高 ≥ 3 才判型，否则返回 null（宁缺勿错）；
- 营业执照锚点示例：`统一社会信用代码`w5、`营业执照`w5、`组成形式`w4、`注册日期|成立日期`w2、`法定代表人|经营者|投资人`w1；
- 身份证：`公民身份号码`w5、`居民身份证`w5、`住址`w2；户口本：`居民户口簿`w5、`户主`w3、`常住人口登记卡`w4。

### 姓名提取（NameExtractor，容忍 OCR 噪声）

按画像 NameLabels（法定代表人|执行事务合伙人|投资人|经营者|负责人|户主|姓名）逐个匹配：
1. **同行**：标签完整命中，或标签被截断成尾字后缀（{经营者→营者/者}）紧跟 2-4 汉字——`者林孝明` 命中「者」+「林孝明」；
2. **跨行**：标签独占一行时，取下一非空行开头 2-4 汉字作候选；
3. **校验**（SurnameTable）：纯汉字 2-4 字、首字在常见姓氏表中加分、含 OCR 噪声字（□、数字、字母、标点）剔除；多候选取「标签命中最长 + 姓氏命中」最高分。

### 命名规则

- 命中：`{姓名}{类型短名}{扩展名}`，如「林孝明执照.pdf」，冲突走现有 `OutputNameGenerator.FindAvailablePath`（追加 (2) 计数）；
- 未命中（类型识别不出 / 姓名提取不到 / OCR 异常）：**完全保持现状**「原名_no-watermark」，不破坏现有流程。

## 改动文件

| 文件 | 改动 |
|---|---|
| Core\Features\Naming\*（新增 7 个） | 画像/分类/提取/命名核心 |
| [WatermarkProcessor.cs](file:///d:/code/src/WatermarkRemover.Core/Processing/WatermarkProcessor.cs) | 构造签名加 `Func<string, AutoNameResult?>? autoNamer`（可空，默认 null 行为不变），第 51 行命名决策：命中走智能名，否则原 `GetOutputPath` |
| [WatermarkFeature.cs](file:///d:/code/src/WatermarkRemover.Core/Features/WatermarkFeature.cs) | 注入可空 `IPdfPageRenderer` + `IOcrEngine`，Run 内只渲染**第 1 页** → SmartNamingService，OCR 异常一律吞掉返回 null |
| [IOcrEngine.cs](file:///d:/code/src/WatermarkRemover.Core/Features/Ocr/IPdfPageRenderer.cs) | `IPdfPageRenderer` 新增 `RenderFirstPageAsync(path, dpi)`（向后兼容） |
| [AppSettings.cs](file:///d:/code/src/WatermarkRemover.Core/Settings/AppSettings.cs) | 新增 `AutoNameByContent`（默认 true），Clone 同步 |
| [MainViewModel.cs](file:///d:/code/src/WatermarkRemover.App/ViewModels/MainViewModel.cs) | 组合根注入 `new WinRtPdfPageRenderer(), CreateOcrEngine()`（微信优先原生兜底，已有） |
| SettingsWindow XAML/VM | 「按内容自动命名」开关 |
| tests\WatermarkRemover.Tests\Core\Naming\*（新增） | 见验证 |

## 范围与兜底决策（按推荐默认执行，如有异议审批时说明即可）

1. **首批画像**：营业执照 + 身份证 + 户口本（三个一起做正好验证可扩展性；与 QuickSuffixes 标签对齐）。后续新增类型只加画像。
2. **识别出类型但提取不到姓名 / OCR 异常**：回退现有「原名_no-watermark」命名，不产生半截文件名。
3. **智能命名仅作用于去水印输出**；OCR 文本文件命名不在本次范围。

## 测试与验证

1. **画像评分**：真样 OCR 文本（含「者林孝明」截断标签）→ 判「执照」+ 提取「林孝明」；身份证/户口本典型文本各判对；户口本绝不误判为执照；低分文本返回 null。
2. **姓名提取**：标签同行 / 跨行 / 标签截断（者、人、合伙人尾字）三形态；2/3/4 字姓名；噪声剔除；多候选选优。
3. **集成**：namer 命中 → 输出「林孝明执照.pdf」；namer null / OCR 抛异常 → 回退「_no-watermark」；命名冲突 → 「林孝明执照 (2).pdf」。
4. **回归**：现有 122 个基线测试全绿（autoNamer 默认 null 不改行为）。
5. **端到端冒烟**：发布 v1.4.x，拖入 扫描全能王.pdf 去水印 → 生成「林孝明执照.pdf」，日志确认。

实施顺序：Naming 核心 + 单测 → processor/feature 接线 → 组合根/设置开关 → 全量回归 + 真样张冒烟。
