# SwAiAssistant Spec Mode 终审报告（独立技术评审）

- 评审对象：SolidWorks AI 插件 SwAiAssistant（0.2.0，T1–T26；T26 Status = pending）
- 评审方式：全新上下文、**只读**评审。全部结论以源码与实物（ZIP / 日志 / 快照 JSON / TEMP 产物）为第一证据，tasks.md 自述仅作线索；未运行构建、未启动 SolidWorks、未做任何 git/代码修改。
- 评审日期：2026-09-26
- 关键说明：SwIaTest 控制台"退出码 0 / 全部通过"类结果（TR-11.1、TR-14.1、TR-22.1、TR-23.1、TR-24.x、TR-25.1 等）本次**未复跑**，属开发者自报证据；凡产品层无法触达的能力，即使测试台通过，也不计为 AC 通过。

---

## 一、结论总览

**T26 放行结论：fail（当前不满足发布放行条件）。**

红线与底座扎实，但终审中发现两项**核心 FR 在产品工程中根本未接线**（服务代码存在且测试台通过，但 AddIn 产品路径不可达），直接导致 AC-8、AC-9 失败；另有 AC-12 零数据、AC-1/18/19 的真机验收项未执行。这些不是设计缺陷，而是"集成 + 证据"缺口，修复路径清晰，无需架构返工。

- 红线（无模型代码执行）、DPAPI、人工确认闸、绿色部署：**全部扎实通过**。
- 失败 AC：**AC-8、AC-9**（产品中不可用）。
- 证据缺失 blocked：**AC-1、AC-12、AC-18、AC-19**（其中 AC-1/19 为同一项干净机冒烟）。
- 部分达成：AC-4、AC-6、AC-7、AC-11、AC-13、AC-16。
- 通过（以自动化/静态证据为主，GUI 截图类证据普遍待补）：AC-2、AC-3、AC-5、AC-10、AC-14、AC-15、AC-17。
- 问题计数：**严重 2 / 中 5 / 低 7**。

最重大的两个新发现（tasks.md 未如实反映为"未完成"，T14/T15 均标记 completed）：

1. **对话式修改与数据问答（FR-14）在产品里不存在**：`EditPlanner`、`QAService` 全仓只在 SwIaTest 测试台被 new 过（[Program.cs:673-674](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/tools/SwIaTest/Program.cs#L673-L674)、[Program.cs:856](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/tools/SwIaTest/Program.cs#L856)），AddIn 工程零引用；产品对话发给 LLM 的文档上下文只有"文档标题 + 特征名"（[TaskPaneView.xaml.cs:292-318](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.AddIn/UI/TaskPaneView.xaml.cs#L292-L318)），问答无真实回读、修改走整树重跑（会在原零件上叠加重复几何）而非改驱动尺寸。
2. **材料（FR-15）在产品里完全不生效**：材料页仍是占位文字"将在 M3 里程碑接入"（[TaskPaneView.xaml:96-101](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.AddIn/UI/TaskPaneView.xaml#L96-L101)）；产品装配 PlanExecutor 时**未注入 MaterialService**（[SwAddIn.cs:130](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.AddIn/SwAddIn.cs#L130)），`setMaterial` 步骤只写一条"材料服务未注入，材料未赋值"的备注（[PlanExecutor.cs:290-298](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Planner/Execution/PlanExecutor.cs#L290-L298)）。连带后果：UI 显示的质量按 SW 默认密度计算（错误）、工程图标题栏材料字段无值。
3. 发布说明 [release-notes.txt:50-66](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/install/release-notes.txt#L50-L66) 已把上述不可用功能（对话修改问答、材料页、约束/标注、筋板、包围盒校验）作为正式能力对外宣称，构成用户可见的过度承诺。

---

## 二、Checkpoint 终审（a–h）

| 项 | 结论 | 依据摘要 |
|---|---|---|
| **a 红线（无模型产物执行）** | **pass** | 全 src grep 无 `CSharpCodeProvider/CodeDom/AddScript/CSScript/Roslyn/CSharpScript/RunMacro/DynamicAssembly`；唯一命中是 [PlanJsonParser.cs:36](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Planner/PlanJsonParser.cs#L36) 的代码夹带**拒绝黑名单**。`Process.Start` 仅 3 处合法用途（两处打开固定日志目录、一处固定路径拉起 `ollama.exe serve`），无模型产物执行。COM 启动反射仅 [SwSession.cs:109-111](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Cad/Session/SwSession.cs#L109-L111) 的 `Type.GetTypeFromProgID/Activator.CreateInstance`。响应先抽取首个平衡花括号块、剥离 ```json 围栏，JSON 外文本经黑名单拒绝（[PlanJsonParser.cs:34-114](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Planner/PlanJsonParser.cs#L34-L114)），随后 `JObject.Parse` + [FeatureTreeValidator.cs:33-34](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Planner/Schema/FeatureTreeValidator.cs#L33-L34) 强校验。残留弱点见低-3。 |
| **b 架构/分层/STA** | **partial** | Ai 层 csproj 仅引用 Core/Newtonsoft/System.NetHttp，无 Interop；Reverse 层无 SolidWorks.Interop/`Process.Start`。但 **Planner 层条件引用 sldworks/swconst interop** 且 4 个文件直接 `using SolidWorks.Interop.sldworks`：[SwAiAssistant.Planner.csproj:20-31](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Planner/SwAiAssistant.Planner.csproj#L20-L31)、[PlanExecutor.cs:132](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Planner/Execution/PlanExecutor.cs#L132)（`doc.GetTitle()` 直调）、[SnapshotService.cs:312](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Planner/Execution/SnapshotService.cs#L312)（`ForceRebuild3` 直调），EditPlanner/QAService 直接收 `IModelDoc2`。实际 COM 调用绝大多数经 [StaExecutor](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Core/Threading/StaExecutor.cs)（单后台 STA + BlockingCollection，同线程防自死锁，实现正确；[SwAddIn.cs:106-107](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.AddIn/SwAddIn.cs#L106-L107) 注入宿主实例），**STA 安全性成立**；工程依赖方向违反 FR-4 字面要求，见中-3（编号见问题清单，实际为中-3 分层条目）。 |
| **c 绿色部署** | **pass** | 0.2.0.zip 只读枚举：产品 7 DLL（AddIn/Core/Ai/Cad/Planner/Verify/Reverse）+ PDB、3 个 SW Interop、Newtonsoft.Json.dll 13.0.3、netDxf.dll 3.0.1（均 MIT）、安装/卸载.bat、release-notes.txt；**无 exe（SwIaTest/SwBench 均未入包）、无 Python/WebView2/Node 痕迹**。安装.bat 管理员自检 + 定位 Framework64 RegAsm + `/codebase`（不入 GAC）；双层注册经 `[ComRegisterFunction]`→AddInRegistration 写 HKLM\SOFTWARE\SolidWorks\Addins{8F2A7C31-9B4E-4A6D-B1F2-0E3A5C7D8890}。0.1.0.zip 无 Reverse/netDxf，版本演进自洽。 |
| **d DPAPI 密钥安全** | **pass** | [DpapiHelper.cs](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Core/Security/DpapiHelper.cs) 用 `ProtectedData.Protect/Unprotect` + CurrentUser + 固定 Entropy；ConfigService 所有 Key 必经 DPAPI，落盘字段 `ApiKeyProtected`，UI 前3+****+后4 掩码；grep 无硬编码 sk-/Bearer。 |
| **e 人工确认闸（DXF/图片反建）** | **pass（代码层）** | DXF 导入 [TaskPaneView.xaml.cs:612-682](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.AddIn/UI/TaskPaneView.xaml.cs#L612-L682)、图片导入 :705-788 两条路径只生成候选计划并切计划页，**均不调 PlanExecutor**；唯一建模入口 `ExecuteCurrentPlanAsync`（:1034 起，执行按钮触发）。极速模式自动执行只在普通 LLM 分支（:236-240），DXF/图片路径提前 return（:193-204）。无板厚/无视觉模型只气泡提示不出卡片，Banner 明示实验性质。SwIaTest TR-24.2（:1483-1518）、TR-25.1（:1785-1792）零变化断言代码真实存在（本次未复跑，属开发者自报）。 |
| **f 单位/几何/ΔV 闭环** | **partial** | 内部米制正确（Units MmToM/MToMm；QueryService `UseSystemUnits=true` MKS；GetBodyBox 米→mm；尺寸写 SystemValue）。GB 模板发现与中英文基准面候选、轴向映射（[DomainSystemPrompt.cs:50-52](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Planner/Prompts/DomainSystemPrompt.cs#L50-L52)）齐备。ΔV 闭环存在但**只判体积不判包围盒**（[VerifyService.cs:37-57](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Verify/VerifyService.cs#L37-L57)），与 FR-17"体积/包围盒任一偏差>5%"不符；且因材料未接线（严重-2），产品中质量回读按默认密度，数值不可信。 |
| **g 构建可复现** | **partial** | build.ps1 vswhere 定位 MSBuild，x64 Release `/t:Rebuild` 顺序构建 AddIn→SwIaTest→SwBench；pack.ps1 调 build→stage→GBK/CRLF 归一化→Compress-Archive，产物实物已核验。偏离：build.ps1 无 BOM 且第 59 行含 63 字节中文注释，与其自述 ASCII-only 及 T21 约定不符（PS5.1 两种解码静态解析均侥幸通过，当前不致命，见低-2）。tools/Tests 下 3 个测试工程（Core/Ai/Planner）真实存在。 |
| **h 已知缺口（如实记录）** | **blocked** | ①干净机管理员注册+卸载回归（TR-21.1 后半、TR-26.1）未执行，现有真机证据仅开发机 D:\SwAiAssistant；②AC-12 两轮基准**零数据**（`%TEMP%\SwAiAssistant-bench` 不存在，SwBench 从未跑过）；③AC-18 连续 20 次会话+僵尸进程核查未做；④全部 GUI 截图类（TR-2.2/5.2/…/23.2 及第 11 组真机清单）归用户手动未回传。两个环境豁免须跟踪：隐藏僵尸 SLDWORKS.exe PID 47400、VBA 首启模态窗致"无残留"断言间歇失败。 |

---

## 三、AC-1～AC-19 逐条核对

| AC | 结论 | 独立证据与说明 |
|---|---|---|
| **AC-1 注册加载与五区域界面** | **blocked** | 真机日志 [app-20260925.log](file:///c:/Users/16650/AppData/Roaming/SwAiAssistant/logs/app-20260925.log)（%AppData%\SwAiAssistant\logs\）：21:14 Addins 键写入、21:24/22:03/22:30/22:35 `ConnectToSW Revision=34.0.0` 完成，独立佐证开发机注册加载成功。但：①Pass Condition 要求**干净机+卸载前后注册表对比**，未执行；②"五个区域齐全"中**材料区为占位页**（[TaskPaneView.xaml:96-101](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.AddIn/UI/TaskPaneView.xaml#L96-L101)），不满足"材料区域"功能语义。 |
| **AC-2 绿色部署** | **pass（静态）** | ZIP 清单、RegAsm/注册表代码、第三方许可审计均通过（见 checkpoint c）。子项"干净机全过程无下载提示"未真机验证，随 AC-1 一并 blocked。 |
| **AC-3 模型配置与密钥安全** | **pass（静态+代码）** | DPAPI/掩码/持久化/连通测试代码齐备（checkpoint d）；设置页模型增删改/连通测试/能力探测为真实控件（[TaskPaneView.xaml:152-172](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.AddIn/UI/TaskPaneView.xaml#L152-L172)）。TR-6.1 截图待回传，不影响代码层结论。 |
| **AC-4 能力探测与自动调度** | **partial** | Scheduler 能力路由、熔断、Ollama 追加一次重试、`OfflineModeEntered`→UI 气泡（TaskPaneView:112-115 附近）代码完整并有单测；真机"断网降级建模成功"（TR-8.2）与能力标签截图未回传，Pass Condition 的三任务路由日志未独立核验。 |
| **AC-5 JSON 特征树红线** | **pass** | 代码审计无任何执行面（checkpoint a）；诱导用例的拒绝路径代码真实（PlanJsonParser 黑名单 + Validator 拒绝）。字符串值内夹带代码不检测（低-3），但下游为强类型确定性执行器，无可利用面。 |
| **AC-6 文档策略与双模式** | **partial** | 无文档自动新建零件、已有文档二选一、计划卡可编辑字段、极速模式代码均真实；9-25 日志独立佐证自动新建与二选一弹框。"未点执行前 SW 零变化"经代码路径核实（DXF/图片路尤甚，见 checkpoint e），但 4 子场景的真机 GUI 时序截图未回传；极速模式在普通聊天路自动执行（:236-240）逻辑存在。 |
| **AC-7 19 项操作真实生效** | **partial（实为 11+/12 特征、4/7 草图可经产品触达）** | ①**筋板真机零成功**：[FeatureService.cs:575-611](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Cad/Features/FeatureService.cs#L575-L611) `RibMm` 代码就绪（两次尝试+silent），T13 自认 SW2026 InsertRib 静默拒绝。②草图 Schema 仅 7 类实体且**无 arc**（[FeatureTreeValidator.cs:33-34](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Planner/Schema/FeatureTreeValidator.cs#L33-L34)、[PlanExecutor.cs:342-382](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Planner/Execution/PlanExecutor.cs#L342-L382) default 抛 NotSupported），**几何约束、尺寸驱动标注**无 Schema 实体类型——Cad 层 AddConstraint/AddRadial/AddHorizontal 等原语与 TR-11.1 回读为真，但产品特征树入口不可达，FR-13 草图 7 项中 3 项（圆弧、约束、标注）产品不可用。③**螺纹孔缺失**：HoleSpec 无螺纹字段，异型孔仅通孔/沉孔（FR-13 括号明示"螺纹孔"）。④异型孔降级真实有效（[FeatureService.cs:279-348](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Cad/Features/FeatureService.cs#L279-L348) HoleWizard5 失败→草图圆+两级切除+note）。 |
| **AC-8 对话式修改与数据问答** | **fail** | 见严重-1。`EditPlanner`/`QAService` 在 AddIn 工程零引用（全仓仅 [Program.cs:673-674](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/tools/SwIaTest/Program.cs#L673-L674)、[:856](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/tools/SwIaTest/Program.cs#L856) 测试台实例化）；产品对话上下文仅标题+特征名（[TaskPaneView.xaml.cs:292-318](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.AddIn/UI/TaskPaneView.xaml.cs#L292-L318)）；PlannerService 响应类型仅 plan/chat/clarify 整树，无编辑意图通道。"孔改 φ8/加厚/追加沉孔/多重/包围盒"在产品中均不按 AC 方式工作；TR-14.1 全过仅证明 Cad 原语，不证明产品行为。 |
| **AC-9 材料库与自动猜材** | **fail** | 见严重-2。材料下拉不存在（占位 XAML）；产品 PlanExecutor 构造仅 5 参无 MaterialService（[SwAddIn.cs:130](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.AddIn/SwAddIn.cs#L130)），setMaterial 仅加备注（[PlanExecutor.cs:293-298](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Planner/Execution/PlanExecutor.cs#L293-L298)）。MaterialService 16 种 GB 材料/Guess/保底密度代码与 TR-15.1 测试台证据为真，但产品三个 Then 子句（自动赋密度/下拉可改/改后质量重算）无一成立。 |
| **AC-10 快照与一键回滚** | **pass（自动化证据强）** | 产品真实接线（[SwAddIn.cs:119-123](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.AddIn/SwAddIn.cs#L119-L123)，快照页真实控件，执行前自动 Capture）；回滚只删 AI 特征（`IsAiFeature` 按 AI__ 前缀即可判定，[FeatureRegistry.cs:66-71](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Planner/Execution/FeatureRegistry.cs#L66-L71)，产品中虽无人 Register 但不影响前缀路径；冲突用户特征不删，[SnapshotService.cs:235-236](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Planner/Execution/SnapshotService.cs#L235-L236)）、恢复尺寸/材料、回滚前再快照。TR-16.1 回读详尽且 %AppData%\snapshots 有真实快照 JSON 实物（9-25 当日多次更新）。GUI 回滚演示截图待补。 |
| **AC-11 强校验闭环与自动重试** | **partial** | 体积 ΔV 闭环、超阈三选项（采纳/回滚 preSnap/取消回传错误）、视觉模型缺失自动降级为真。缺口：①**UI 执行调 `ExecuteAsync` 而非 `ExecuteWithRetry`**（[TaskPaneView.xaml.cs:1098](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.AddIn/UI/TaskPaneView.xaml.cs#L1098)），FR-17"SW 报错自动修正重试≤2 次"（[PlanExecutor.cs:84-125](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Planner/Execution/PlanExecutor.cs#L84-L125)）在产品中不生效，仅测试台/SwBench 可触达；②包围盒/孔数未参与 Passed 判定（[VerifyService.cs:46-47](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Verify/VerifyService.cs#L46-L47) 只比体积）；③校验自身异常按 `Passed=true` 放行（[TaskPaneView.xaml.cs:1276-1285](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.AddIn/UI/TaskPaneView.xaml.cs#L1276-L1285)）。 |
| **AC-12 端到端建模质量（rubric）** | **blocked（零数据）** | `%TEMP%\SwAiAssistant-bench` 不存在；TR-20.1 云端两轮、TR-20.2 Ollama 轮均未跑，10 测试件一次通过率无任何结果。SwBench 工具已建但从未产出报告。rubric 无证据即不得评分；此为 T26 放行硬前置。 |
| **AC-13 Ollama 零 Key 与一键拉取** | **partial** | OllamaServiceManager 修复/拉取/进度/取消代码与设置页控件齐（[TaskPaneView.xaml:175-188](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.AddIn/UI/TaskPaneView.xaml#L175-L188)）；零 Key 纯文本降级（HasVisionCandidate→跳过视觉并说明）代码齐；TR-25.1 旁证开发机 Ollama qwen2.5vl:7b 实际跑通过一次视觉调用。但"清空云端→一键修复/拉取→纯离线建成 1 件并过数值校验"的 AC 主路径（TR-10.1/TR-20.2）无证据。 |
| **AC-14 GB 三视图工程图** | **pass（自动化证据）** | [DrawingService.cs:195-224](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Cad/Drawings/DrawingService.cs#L195-L224)：Create1stAngleViews2 失败降级手动第一角布图（前视左上/上视在下/左视在右）、MarkedForDrawing+InsertModelAnnotations、:251 SaveAs3 导 PDF；TR-22.1 三件 30 项控制台断言（PDF 魔数/投影位移/覆盖率）。注意连带影响：标题栏材料字段在产品中因 AC-9 未接线将为空；PDF 实物与 GUI 未独立复核。 |
| **AC-15 装配体辅助** | **pass（自动化证据）** | AssemblyService AddComponent5/同轴/重合/距离配合/干涉中文报告代码真实，TR-23.1 20 项断言（位置误差 0、故意干涉体积 200mm³）；TEMP 下有真实 .SLDPRT 装配夹具产物（probe-asm，9-26 13:21–13:58 多轮）。TR-23.2 GUI/对话式"装到顶面"截图未回传。 |
| **AC-16 DXF 反建（rubric）** | **partial** | TR-24.1 报 5/5 建成、ΔV=0、anchor 5 数值满足（阈值≥3）；但 5 张 DXF 系测试台用 netDxf **现场合成夹具**而非 AC 规定的代表性真实图纸（矩形板带孔/法兰盘/L 板），代表性保留；人工确认闸代码为真（checkpoint e，TR-24.2 零变化断言存在）。真实图纸集 + 计划卡截图补齐前不判满。 |
| **AC-17 图片/PDF 反建安全闸** | **pass（代码+自动化断言）** | 实验 Banner（ImageReversePlanner.cs:54-55）、唯一执行门、极速路排除均核实（checkpoint e）；TR-25.1 qwen2.5vl 确认前零变化、确认后 ΔV=0 断言存在（开发者自报未复跑）。流程截图/PDF 手动项缺。 |
| **AC-18 稳定性与可观测性** | **blocked** | 日志/中文异常/取消/串行化/孤儿清理代码齐，9-25 真机日志链路完整可复盘（模型调用/特征树/COM/回读均有）；但 Pass Condition 的**连续 20 次混合会话+僵尸进程核查未执行**（TR-19.1/19.2 归真机清单）。另记录两个环境豁免（僵尸 PID 47400、VBA 首启模态）。 |
| **AC-19 一键构建发布** | **blocked（构建侧 pass，验收侧 blocked）** | build.ps1/pack.ps1 可产 ZIP 且 0.2.0.zip 实物清单已核验（checkpoint c/g）；但 Pass Condition 明文要求"另一台机器/干净目录解压安装并通过 AC-1 冒烟"，未执行，与 AC-1 同阻。build.ps1 非 ASCII 注释为低风险项。 |

---

## 四、分级问题清单

### 严重（2）

**严重-1｜FR-14 对话式修改与数据问答在产品中完全未接线（违背 AC-8、FR-14）**
- 证据：`EditPlanner`、`QAService` 全仓仅测试台实例化（[Program.cs:673-674](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/tools/SwIaTest/Program.cs#L673-L674)、[:856](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/tools/SwIaTest/Program.cs#L856)）；AddIn 工程 grep 零命中；产品文档上下文仅标题+特征名（[TaskPaneView.xaml.cs:292-318](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.AddIn/UI/TaskPaneView.xaml.cs#L292-L318)）；PlannerService 无编辑意图响应类型。
- 后果："孔改 φ8/加厚/追加沉孔"在产品中只会生成整树计划并在活动零件上叠加重复几何；"多重/包围盒/孔数"无真实回读，LLM 凭标题+特征名臆答。tasks.md T14 标 completed 与实际不符。
- 修复建议：①在 [SwAddIn.cs:125-132](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.AddIn/SwAddIn.cs#L125-L132) 装配 EditPlanner/QAService（DimensionService 已建，:121）并注入 TaskPaneView；②PlannerService 增加 edit/qa 意图分类（或在 chat 分支用 QAService.AnswerQuestion 的真实回读填充答案）；③修改类指令生成 EditRequest 走 EditPlanner.Apply，并把 _registry.Register 接入 PlanExecutor 成功回调以填充尺寸全名映射；④按 AC-8 四句指令补真机回读证据。

**严重-2｜FR-15 材料功能产品中不生效，且发布说明对外宣称可用（违背 AC-9、FR-15，连带 FR-18 标题栏）**
- 证据：材料页占位（[TaskPaneView.xaml:96-101](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.AddIn/UI/TaskPaneView.xaml#L96-L101)）；PlanExecutor 产品构造无 MaterialService（[SwAddIn.cs:130](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.AddIn/SwAddIn.cs#L130)）；setMaterial 空转（[PlanExecutor.cs:293-298](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Planner/Execution/PlanExecutor.cs#L293-L298)）；[release-notes.txt:58](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/install/release-notes.txt#L58) 宣称"自动猜材+材料页手动赋值"。
- 后果：自动猜材、手动改材质均不可用；执行报告质量（:1102）按 SW 默认密度，数值错误；工程图标题栏材料字段无值。
- 修复建议：SwAddIn 构造 PlanExecutor 时补第 6 参 `_materials`（MaterialService 已在会话中创建）；材料页改为真实 ComboBox（Q235/45/6061/304 等 16 项）+ 应用按钮调 ApplyMaterial，应用后刷新质量回读；或在正式修复前删改 release-notes 对应条目避免误导。

### 中（5）

**中-1｜产品 UI 未接自动修正重试（违背 AC-11、FR-17"重试≤2 次"）**
- 证据：[TaskPaneView.xaml.cs:1098](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.AddIn/UI/TaskPaneView.xaml.cs#L1098) 用 ExecuteAsync；ExecuteWithRetry（[PlanExecutor.cs:84-125](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Planner/Execution/PlanExecutor.cs#L84-L125)）仅测试台/SwBench 调用。
- 修复：ExecuteCurrentPlanAsync 改调 ExecuteWithRetry，fixPlan 回调复用 PlannerService 携带 SW 错误文本重新规划并再过 FeatureTreeValidator。

**中-2｜强校验只判体积，包围盒/孔数不参与判定（违背 AC-11、FR-17"体积/包围盒任一 >5%"）**
- 证据：[VerifyService.cs:46-47](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Verify/VerifyService.cs#L46-L47)；ActualBoundingBox 仅展示不比较；release-notes:59 已宣称包围盒校验。
- 修复：Verify 增加三边（或最长边）相对偏差与孔数偏差并入 Passed/Warnings，阈值复用 VerifyThresholdPct。

**中-3｜Planner 层直接引用并调用 SolidWorks Interop（违背 FR-4 字面分层）**
- 证据：[SwAiAssistant.Planner.csproj:20-31](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Planner/SwAiAssistant.Planner.csproj#L20-L31)；PlanExecutor.cs:132 GetTitle、[SnapshotService.cs:312](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Planner/Execution/SnapshotService.cs#L312) ForceRebuild3 等直接 COM 调用（其余均已 OnSta 封送，STA 安全成立，风险在依赖方向与纪律）。
- 修复：Cad 层补 GetActiveTitle/ForceRebuild 包装方法，Planner 改调 Cad 服务接口并移除 interop 条件引用；或正式修订 FR-4 措辞为"COM 调用必须经 Cad 封送原语"。

**中-4｜AC-7 19 项能力不齐：筋零成功、Schema 缺圆弧/约束/标注、螺纹孔缺失（违背 AC-7、FR-13）**
- 证据：[FeatureService.cs:575-611](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Cad/Features/FeatureService.cs#L575-L611) 筋 T13 自认静默失败；[FeatureTreeValidator.cs:33-34](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Planner/Schema/FeatureTreeValidator.cs#L33-L34) 实体白名单无 arc/约束/标注；[PlanExecutor.cs:379-381](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Planner/Execution/PlanExecutor.cs#L379-L382) default NotSupported；HoleSpec 无螺纹字段。
- 修复：SketchEntity 增 arc/constraints/dimensions 模型 + Validator 规则 + CreateEntity 接 Cad 已有的 CreateArcMm/AddConstraint/Add*Dimension；螺纹孔补 HoleWizard 类型参数或显式在计划与发布说明中标注不支持；筋要么换草图策略攻关，要么在 release-notes 显式降级标注。

**中-5｜AC-12 两轮基准评测零数据（rubric 无任何评分依据）**
- 证据：%TEMP%\SwAiAssistant-bench 不存在；SwBench 已建未跑（TR-20.1/20.2 均挂真机清单，Ollama 当时不可用）。
- 修复：云端两模型 + Ollama 各跑完 10 件，产出含一次通过率/修复轮次/ΔV 的汇总表，按 anchor 评分（Pass≥4）。

### 低（7）

1. **校验异常静默放行**：[TaskPaneView.xaml.cs:1276-1285](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.AddIn/UI/TaskPaneView.xaml.cs#L1276-L1285) catch 后 `Passed=true`，校验器自身故障被当通过。建议改为"校验不可用"告警态并默认建议回滚/重试。
2. **build.ps1 非 ASCII 隐患**：第 59 行中文注释 63 字节、文件无 BOM，违反其头部 ASCII-only 自述与 T21 约定（PS5.1 当前侥幸解析通过）。改为英文注释即可。
3. **代码夹带检测盲区**：[PlanJsonParser.cs:91-97](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Planner/PlanJsonParser.cs#L91-L97) 只检 JSON 外文本，字符串值内夹带不拦。下游无执行面，风险低；可加值内危险关键字启发式告警。
4. **AC-16 样本代表性**：5 张 DXF 为 netDxf 合成夹具，须补真实图纸（含法兰盘/L 板）重跑。
5. **发布说明过度宣称与版本路标陈旧**：[release-notes.txt:54-66](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/install/release-notes.txt#L54-L66) 宣称约束/标注、筋板、对话修改问答、材料页、包围盒校验（分别对应中-4、严重-1/2、中-2）；:82 行将 M6–M9（工程图/装配/DXF/图片，实际已在 0.2.0 包内）列为"后续里程碑"。须按真实能力重写；设置页 About 文案"0.2.0（M2 AI 打通）"（[TaskPaneView.xaml:193](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.AddIn/UI/TaskPaneView.xaml#L193)）同步更新。
6. **ZIP 携带全部 PDB**：绿色部署无禁止，但增大体积且暴露少量符号信息；正式发布可考虑仅保留必要 PDB 或剥离。
7. **环境豁免未跟踪**：僵尸 SLDWORKS.exe PID 47400、VBA 首启模态窗致残留断言间歇失败，应在 release-notes FAQ 或后续 issue 记录排查结论。

---

## 五、T26 放行结论与前置条件

**结论：fail。** 安全红线、绿色部署、DPAPI、人工确认闸与确定性执行架构可以信赖；但 AC-8、AC-9 两项核心 FR 的服务在产品中不可达（且被发布说明作为正式能力宣称），AC-12 零评测数据，AC-1/18/19 的真机验收未执行。当前发布 ZIP（0.2.0）不具备对外交付条件。

**收口 T26 的最小前置条件（建议顺序）：**

1. 接线并真机验证 **FR-14**（严重-1）：EditPlanner/QAService 注入 AddIn，修改/问答走真实回读，按 AC-8 四句指令留存对话与尺寸/质量属性对照证据。
2. 接线并真机验证 **FR-15**（严重-2）：PlanExecutor 注入 MaterialService、材料页落地，确认执行报告质量与工程图标题栏材料正确。
3. 修复中-1（重试接线）、中-2（包围盒/孔数判定）、低-1（异常放行）。
4. 决策中-4：圆弧/约束/标注/螺纹/筋是补齐实现还是在 release-notes 与计划提示中**显式降级标注**（同步处理低-5 发布说明重写、低-2 build 注释）。
5. 补跑 **AC-12**（中-5，云端+Ollama 两轮 10 件 rubric 报告）。
6. 干净机执行 **AC-1/AC-19** 安装→五区域→卸载回归（注册表前后对比）。
7. 执行 **AC-18** 连续 20 次混合会话+僵尸进程核查；回传 GUI 真机清单（五页签、DXF/图片/装配截图、AC-16 真实图纸 5 张、AC-13 离线建板）。

完成 1–4（代码收口）+ 5–7（证据补齐）并经复审后，本项目具备 pass 条件；架构层面无需要求返工的事项。

---

## 六、第二轮复审记录（R1–R6 修复闭环，2026-09-26）

复审方式：全新上下文只读 agent 对当前源码逐条取证（未构建、未启动 SW），随后对新发现问题修复并再次编译+64 单测。

### 6.1 首轮问题关闭情况

| 编号 | 结论 | 关键证据 |
|---|---|---|
| 严重-1 对话修改/问答未接线 | **已关闭（代码层）** | SwAddIn.cs 实例化并注入 EditPlanner(5 参)/QAService(4 参)；PlanJsonParser 支持 type=edit 四 intent（白名单+内嵌 plan 过 Validator）；改尺寸走 DimensionService 单尺寸写入+重建（非整树叠加），删除有 registry.IsAiFeature 守卫；TaskPaneView 接线 IsDataQuestion/AnswerDataQuestionAsync/ApplyEditResponseAsync/BuildDocContextSafe（含尺寸全名清单）。真机行为待 TR-14.1 复跑。 |
| 严重-2 材料不生效 | **已关闭（代码层）** | PlanExecutor 产品构造实传 _materials,_registry；材料页为真实 ComboBox/应用/刷新/当前材料控件；setMaterial 注入时真赋材质。SW2026 中文材料库命中率待真机。 |
| 中-1 重试未接产品 | **已关闭（代码层）** | 产品执行路径改 ExecuteWithRetry（≤2），fixPlan 走 PlannerService.FixPlanAsync（独立消息+解析+Validator 0 错误才返回）。 |
| 中-2 校验只判体积 | **已关闭** | VerifyService 体积 AND 包围盒三边排序逐边偏差（<1mm 边跳过），孔数精确比只告警不判负；VerifyReport 加盒偏差/孔数字段。 |
| 中-3 Planner 裸 COM | **已关闭（FR-4 按"句柄类型+Cad 封送"修订口径）** | Planner 内零裸 COM 调用（grep 验证）；DocService.GetTitle/ForceRebuild、FeatureService.DeleteFeature、QueryService.GetFeatureInfos 承接，全部 OnSta；csproj 注释明确 interop 仅为不透明句柄类型。 |
| 中-4 Schema 缺口 | **已关闭（arc 实装 + 其余显式降级口径）** | arc 进 Schema/Validator 并真接 CreateArcMm；约束/标注进 Schema+Validator（白名单/越界）但执行器显式 note 降级不静默丢弃；HoleSpec threaded/threadSpec 校验+光孔降级 note；TheoryBudget 圆弧近似标注；DomainSystemPrompt 能力边界第 7 条。 |
| 中-5 AC-12 零数据 | **未关闭（非代码项）** | SwBench rubric 就绪；待真机跑 Ollama 轮与云端两轮，产物 %TEMP%\SwAiAssistant-bench。 |
| 低-1 校验异常放行 | **已关闭** | catch 改 Passed=false + 人工核对/回滚提示。 |
| 低-2 build.ps1 非 ASCII | **已关闭** | 字节级扫描全文件 ≤127。 |
| 低-3 值内夹带盲区 | **已关闭** | PlanJsonParser ValueCodeMarkers+ScanValueWarnings（≤3 条/截 40 字/只告警不阻断）；PlannerService.RequestAsync 与 FixPlanAsync 均 Log.Warn 落审计。 |
| 低-4 DXF 真实样本 | **未关闭（用户项 R8）** | 待法兰盘/L 板等真实图纸 5 张。 |
| 低-5 发布说明过度宣称 | **已关闭** | release-notes.txt 按真实能力重写（约束/标注不施加、螺纹只开底孔、rib 实验、圆弧预算估算、对话修改四 intent、问答确定性）；版本路标改为"已知限制与后续计划"；TaskPane About 与 Ribbon About（SwAddIn.ShowAbout）口径一致。 |
| 低-6 ZIP 携带 PDB | **接受（记录决策）** | 0.2.0 保留全部 PDB：便于用户侧导出日志 ZIP 后按符号/行号定位现场问题；ZIP 仅 1.70MB，体积影响可忽略；无敏感源码（仅符号映射）。后续正式商用版再评估剥离。 |
| 低-7 环境豁免未跟踪 | **已记录（本条）** | ①僵尸 SLDWORKS.exe PID 47400（9-26 01:04 残留）：全程不杀进程策略下保留，与测试结果无因果；测试台自有孤儿经 %TEMP%\SwAiAssistant-iat\sw-owned-pid 标记 + CleanupOwnedOrphan 回收。②VBA 7.1 首次启动模态窗（SW 首次自动化宏权限提示）会导致当轮"无残留"断言间歇失败，手工点过一次后稳定，属环境首启现象而非产品缺陷。 |

### 6.2 第二轮复审新发现与处置

- **B-1（中，复审新发现，已修复）**：产品自动重试原在半成品文档上整树重跑（askUser 固定 ContinueCurrent），抽壳/拔模等非幂等步骤可能二次施加产生静默错误几何；SwIaTest/SwBench 重试用例均 CreateNew 未覆盖此路径。修复（TaskPaneView.ExecuteCurrentPlanAsync）：fixPlan 回调内在请求 AI 修正**之前**，ContinueCurrent 路径先 _snapshots.Rollback(preSnap) 再重跑；无快照或回滚失败则放弃重试并气泡提示手动处理；CreateNew 路径每轮新建空白零件（半成品留旧窗口未保存）。
- **B-2（低，已修复）**：Ribbon About（SwAddIn.cs ShowAbout）文案陈旧，已与面板 About/release-notes 统一口径。
- B-3（信息，接受）：fixPlan 回调 .GetAwaiter().GetResult() 在 ThreadPool 线程、内部 ConfigureAwait(false)，无 UI/STA 死锁；慢响应占池线程，观察项。
- B-4（信息，待真机观察）：BuildDocContextSafe/材料按钮在 UI 线程有同步 COM 枚举，复杂零件可能卡顿。
- B-5（信息，接受）：QA 孔数统计依赖中文特征名子串匹配，非中文界面可能漏统；孔数仅告警不判负，影响限于问答文本。

### 6.3 当前验证证据

- 编译：build.ps1 全项目 x64 Release 通过；单元测试 **64/64 通过**（Core 10、Ai 33、Planner 21），R6/B-1/B-2 修复后两次复跑一致。
- 发布包：build\artifacts\SwAiAssistant-0.2.0.zip，**1,703,091 字节 / 22 文件**（首轮包 1,684,536 字节），release-notes 为新文案。
- 待真机（需用户授权/在场）：SwIaTest 全量（TR-4.1/5.1/11.1/13/14.1/15.1/16.1/18.1 及 --tr22/23/24/25 分支，重点 R2 对话改孔/加厚/问答、R4 改后删除与快照路径、B-1 重试回滚路径、删除红线实测）；SwBench AC-12 Ollama 轮（云端轮归 R8）；AC-1/19 干净机安装卸载注册表对比；AC-18 连续 20 次会话；GUI 截图清单；AC-16 真实图纸 5 张。

### 6.4 复审结论（更新）

代码层修复全部落实且经独立只读复审 + 64 单测验证，checkpoint a/c/d/e 维持 pass，b/f/g 由 partial 转 **pass（代码层）**；h 仍 blocked（真机证据）。T26 最终放行结论维持 **有条件 fail → 待 6.3 真机项完成后转 pass**：AC-8/AC-9 代码已通但真机证据未复跑，AC-12 仍零数据，AC-1/18/19 待用户在场执行。
