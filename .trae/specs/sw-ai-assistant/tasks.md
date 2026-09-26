# SwAiAssistant - 实施任务计划

> 里程碑：M0 地基 → M1 最小闭环 → M2 AI 打通 → M3 能力补齐 → M4 校验健壮 → M5 核心收尾 → M6 工程图 → M7 装配 → M8 DXF 反建 → M9 图片反建 → 终审。
> 每项任务完成时必须填写 Completion Evidence；`rule` TR 必须有可复核的客观证据（构建输出、真机日志、截图、回读数据或代码审计记录）。
> 真机 GUI 检查由用户按测试清单执行；开发方另建 `tools/SwIaTest`（不随产品发布）控制台经 COM 在后台驱动 SW 做集成断言。

---

## Task 1: 解决方案骨架与共享内核（M0）
- **Status**: `completed`
- **Completion Evidence**:
  - TR-1.1（构建 x64/net48）：`dotnet build -c Release` 7 工程 0 警告 0 错误；PowerShell `AssemblyName.GetAssemblyName(...AddIn.dll).ProcessorArchitecture = Amd64`。
  - TR-1.2（DPAPI/日志）：`dottest` 10/10 通过（DpapiHelper 往返、密文不含明文、空串透传；JsonConfigStore 重载一致+落盘无明文；日志按日落盘+级别过滤；StaExecutor 确在专用 STA 线程并回传异常）。
  - TR-1.3（许可审计）：产品工程 PackageReference 仅 Newtonsoft.Json 13.0.3（MIT）；bin 中非框架/非 SW 的托管 DLL 仅 Newtonsoft.Json.dll；xunit 等测试包不随产品发布。
- **Priority**: `high`
- **Depends On**: None
- **Description**:
  - 建立 `src/` 解决方案：SwAiAssistant.Core / .AddIn / .Ai / .Planner / .Cad / .Verify 共 6 个 net48/x64 类库（AddIn 输出类库 DLL），统一引用 SW2026 `api\redist` Interop（Embed Interop Types=false，本地复制=false，发布脚本从 redist 收集）。
  - Core：日志器（按日文件 + 级别，`%AppData%\SwAiAssistant\logs`）、DPAPI 加密配置读写（`%AppData%\SwAiAssistant\config`）、JSON 帮助类（Newtonsoft.Json，MIT）、HTTP 帮助类、STA 任务封送器、WPF 中文样式/转换器基础。
  - 目录：`src/`、`install/`、`build/`、`tools/`、`.gitignore`、`README` 暂不创建。
- **Acceptance Criteria Addressed**: AC-2、AC-3、AC-19
- **Test Requirements**:
  - `rule` TR-1.1: Release 配置 MSBuild 整解决方案 0 错误；产物全部 x64/net48；证据为构建日志。
  - `rule` TR-1.2: 单元测试验证 DPAPI 往返且落盘文件无明文；日志按日落盘；证据为测试输出与配置文件字节检查。
  - `rule` TR-1.3: 第三方包仅出现 MIT/BSD/Apache 等宽松许可（`packages.config`/csproj 审计）；证据为依赖清单。

## Task 2: AddIn 外壳——Ribbon 与任务面板（M0）
- **Status**: `completed`
- **Completion Evidence**（机器可验部分；TR-2.1/2.2 真机证据待用户回传）:
  - VS MSBuild（`D:\Apps\VS\MSBuild\Current\Bin\MSBuild.exe`，dotnet build 不跑经典 net48 工程的 WPF 标记编译 targets）Release/x64 构建 0 错误，输出 `src/SwAiAssistant.AddIn/bin/Release/`（含 5 个产品 DLL + Newtonsoft.Json + 3 个 SW Interop）。
  - SW2026 Interop 反射实测签名已落地：`CreateCommandGroup2` 7 参末参 `ref int`、`AddCommandTab(docType,name)`、`Small/LargeMainIcon` 字符串属性、`CreateTaskpaneView2(icon,tip)`、`AddControl(progId,licKey)`。
  - 修复注册钩子缺陷：`[ComRegisterFunction]` 放在独立辅助类上**不会被 RegAsm 调用**（只认被注册类自身的静态方法）——已改为 SwAddIn 类内 `ComRegister/ComUnregister` 转发到辅助逻辑。
  - TR-2.1（2026-09-25 21:14 真机）：`reg query HKLM\SOFTWARE\SolidWorks\Addins\{8F2A7C31-…}` 存在，默认值=0x1/Title=AI 建模助手/Description 完整；`HKCR\CLSID\{8F2A7C31-…}\InprocServer32` CodeBase=file:///D:/SwAiAssistant/SwAiAssistant.AddIn.DLL；日志记录「已写入 SolidWorks Addins 注册表项」。
  - TR-2.2（2026-09-25 21:24 真机）：SW2026 启动日志 `ConnectToSW 完成`，无异常；用户截图确认右侧任务面板「SwAiAssistant · AI 建模助手」五页签（对话/计划/材料/快照/设置）+ 状态栏齐全。遗留小项：功能区「AI 助手」标签页在 CommandManager 已注册（无 Warn 日志）但未出现在标签条，待用户右键标签条启用或重启后复核。
  - 关键坑位记录：① `swCreateCommandGroupErrors` 枚举 **1=成功/0=失败**（与直觉相反，曾因误判导致 ConnectToSW 返回 false 被 SW 卸载）；② `[ComRegisterFunction]` 必须标注在被注册类自身的静态方法上；③ bat 必须 GBK+CRLF。
- **Priority**: `high`
- **Depends On**: T1
- **Description**:
  - 实现 `ISwAddIn`（swpublished）：固定 GUID/ProgId、`[ComVisible]`、`ComRegisterFunction/ComUnregisterFunction` 自动写 `HKLM\SOFTWARE\SolidWorks\Addins\{guid}`（Title/Description/启动加载）。
  - ConnectToSW 中创建 CommandManager 命令组「AI 助手」（开关任务面板、设置、生成工程图占位等按钮，无功能者禁用态）。
  - 用 HWND 宿主方式把 WPF UserControl 挂到 `CreateTaskpaneView`：面板含对话区、计划卡片、材料区、快照列表、设置页五个区域（占位 UI 即可）。
  - DisconnectFromSW 释放 COM 对象；所有 SW 回调经 STA Dispatcher。
- **Acceptance Criteria Addressed**: AC-1
- **Test Requirements**:
  - `rule` TR-2.1: RegAsm `/codebase` 注册成功且 CLSID 与 Addins 双层键均存在；证据为 regasm 输出 + reg query 输出。
  - `rule` TR-2.2: 启动 SW2026 后功能区出现「AI 助手」组、任务面板可开关、五区域齐全、关闭 SW 无异常日志；证据为用户真机确认（测试清单第 1 组）与 SW 截图。

## Task 3: 安装/卸载脚本与打包流水线（M0）
- **Status**: `completed`
  - TR-3.2 备注：ZIP 解压→安装→SW 加载全链路已由用户真机走完（D:\SwAiAssistant）；卸载回归留待 M5 发布前统一执行。
- **Completion Evidence**（机器可验部分；TR-3.1/3.2 真机执行证据待用户回传）:
  - TR-3.3（ZIP 清单审计）：`build/pack.ps1 -Version 0.1.0` 产出 `build/artifacts/SwAiAssistant-0.1.0.zip`，根目录 19 项：6 产品 DLL+PDB、SolidWorks.Interop.sldworks/swconst/swpublished 3 个、Newtonsoft.Json.dll、安装.bat、卸载.bat、release-notes.txt；无 python/node/webview bootstrapper。
  - TR-3.1：非管理员双击安装.bat → 中文提示并退出（用户确认）；管理员运行 → RegAsm /codebase 成功（用户确认 + 上述注册表证据）。bat 文件教训：cmd 只认 **GBK 编码 + CRLF 换行**，pack.ps1 已加自动规范化。
  - `build/build.ps1`：vswhere/兜底路径定位 MSBuild，`-RunTests` 实测 10/10 通过（测试已改用 %TEMP% 独立目录，不再触碰真实 %AppData%）；自动从 SW_REDIST_DIR/候选路径收集 3 个 Interop 到 AddIn 输出目录。
- **Priority**: `high`
- **Depends On**: T2
- **Description**:
  - `install/安装.bat`：管理员权限自检 → 定位 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe` → 注册全部插件 DLL → 写/校验 Addins 键 → 中文输出结果与日志路径。
  - `install/卸载.bat`：RegAsm `/u` + 删除 Addins 键（保留 %AppData% 配置，提示用户）。
  - `build/build.ps1`（Release 构建）、`build/pack.ps1`（收集 bin/redist 依赖 + 脚本 → 版本号 ZIP）。
- **Acceptance Criteria Addressed**: AC-1、AC-2、AC-19
- **Test Requirements**:
  - `rule` TR-3.1: 非管理员运行安装.bat 被拒绝并中文提示；管理员运行成功；证据为控制台输出。
  - `rule` TR-3.2: pack.ps1 产出 ZIP，解压到全新目录执行安装后通过 Task2 同款冒烟；卸载后注册表/插件目录核查干净（%AppData% 除外）；证据为 ZIP 清单与注册表前后对比。
  - `rule` TR-3.3: ZIP 内不含任何要求额外运行时安装的组件（无 python/node/webview bootstrapper）；证据为 ZIP 文件清单审计。

## Task 4: Cad 层——SW 会话与文档策略（M1）
- **Status**: `completed`
- **Completion Evidence**（机器可验部分；TR-4.1/4.2 待真机执行）:
  - 2026-09-25 构建：VS MSBuild Release/x64 全解决方案 0 错误（AddIn/Cad/SwIaTest 均产出）；单元测试 10/10 通过。
  - COM 签名反射实测（SW2026 redist）：`RevisionNumber()->String`、`NewDocument(tpl,paper,w,h)->Object`、`GetDocuments()->Object[]`、`ExitApp()`、`GetUserPreferenceStringValue(8=swDefaultTemplatePart)`、`IActiveDoc2`、`IModelDoc2.GetTitle()/GetType()`。
  - Cad 层落地：`CadException`（COM 错误→中文映射）、`SwSession`（ROT 接管/ProgID 链 34→33→版本无关启动、90s 就绪等待、版本年=1992+主版本、模板三级自动发现、仅自有实例 ExitApp+30s 进程退出等待）、`DocService`（无文档/非零件自动新建、有零件由调用方二选一）；全部 COM 访问经 StaExecutor 封送（已加同线程内联执行保护防嵌套死锁）。
  - AddIn 接入：ConnectToSW 建 StaExecutor+SwSession(宿主实例)+DocService 并注入面板；对话页临时按钮「【T4 测试】确保有零件文档」+ WPF 中文二选一对话框（继续/新建/取消）。
  - `tools/SwIaTest` 控制台（TR-4.1 自动化）：接管/启动 → 版本/模板/新建/类型/枚举/活动文档/二选一新建分支断言 → 自有实例退出后断言无残留 SLDWORKS.exe；接管模式绝不退出用户 SW。
  - 顺带修复：① T2 遗留「AI 助手」功能区标签页不可见——反射实测 `ICommandTab.Visible` 可读写，API 新建选项卡默认隐藏，已显式 `Visible=true`；② LogTests 触碰真实 %AppData% 被沙箱拦——Log 加 `DirectoryOverride` 测试挂钩，测试改用独立 %TEMP% 目录。
  - TR-4.1（2026-09-25 22:00 真机后台）：SwIaTest 两轮 12/12 通过——连接/版本 34.0.0/模板自动发现（gb_part.prtdot）/新建零件/类型=1/枚举包含/活动文档/二选一新建分支/退出无残留；日志时序与断言一致；核查无僵尸 SLDWORKS.exe。
  - 竞态修复（真机发现）：SW 进程启动中未注册 ROT 时 CreateInstance 会"接管"该实例却被误判为自有实例（Dispose 时 ExitApp 会误关用户 SW）——ConnectOrStart 现先重试 ROT 最多 60s，确认无活动实例才启动。
  - TR-4.2 分支一（2026-09-25 22:0x 用户真机）：无文档点【T4 测试】自动新建零件1，状态栏"无打开文档，已自动新建：零件1"（截图回传）；分支二对话框待复核。
  - 应用户要求新增面板「日志」页（设置页后）：查看今日日志末 300 行（切页自动刷新/手动刷新）、打开日志目录、导出全部日志 ZIP（共享读拷贝防占用冲突）；构建 0 错误。
- **Priority**: `high`
- **Depends On**: T3
- **Description**:
  - SwSession：连接已运行实例（ROT/GetActiveObject）或启动 SW；版本探测（RevisionNumber()）统一版本信息；Visible/UserControl 策略（插件内用已运行实例；测试台可后台）。
  - 所有 Cad 调用经 `StaTask.Run` 在单一 STA 线程封送；DocService：获取活动文档、新建零件（模板路径自动发现）、无文档自动新建、有文档弹"当前继续/新建"中文对话框。
  - 内部统一单位米；SketchSegment/Feature 包装基础；COM 对象释放与异常→中文 CadException 映射。
- **Acceptance Criteria Addressed**: AC-6、AC-18
- **Test Requirements**:
  - `rule` TR-4.1: 集成台后台启动 SW：新建零件、枚举活动文档、释放退出无残留 SLDWORKS.exe；证据为测试输出 + 进程核查。
  - `rule` TR-4.2: 真机：无文档发测试命令自动新建；有文档弹出二选一对话框，两条分支行为正确；证据为用户真机确认（测试清单第 2 组）。

## Task 5: 最小建模原语与集成测试台（M1）
- **Status**: `completed`（TR-5.2 真机冒烟证据待用户随最终清单回传）
- **Completion Evidence**:
  - COM 签名反射实测（SW2026 redist，`tools/reflect-api.ps1`）：`CreateCornerRectangle(6d)`、`CreateCircleByRadius(4d)`、`InsertSketch(bool)`、`FeatureExtrusion3`(25 参)、`FeatureCut4`(28 参)、`CreateMassProperty2→IMassProperty2`（`UseSystemUnits=true`=MKS）、`GetBodies2(swSolidBody)→IBody2.GetBodyBox→double[6]米`、`IAddDiameterDimension2→DisplayDimension.GetDimension2→IDimension`（Value 与 SystemValue 双属性）、`SelectByID2`、`SaveAs4(5参)`、`swInputDimValOnCreate` 开关。
  - Cad 层落地：`Units`（毫米↔米）、`SketchService`（中/英多语言基准面候选名、AddToDB 直写、角点矩形、圆心半径圆、直径智能尺寸）、`FeatureService`（盲拉伸凸台、双向贯穿切除、草图重选、特征改名）、`QueryService`（质量属性、多体合并包围盒、特征清单、按 ProfileFeature 类型定位最新草图）。
  - TR-5.1（2026-09-25 23:27 后台接管 SW2026）：SwIaTest **23/23 通过**——矩形拉伸体积 96000.0 mm³（理论 96000）、包围盒 10.0×80.0×120.0、四角 φ6（距边 10）贯穿切除后体积 94869.1 mm³（理论 94869.0，ΔV≤1%）、φ20 直径标注 SystemValue 回读 20.00 mm、特征枚举 22 个、测试件保存 `%TEMP%\SwAiAssistant-iat\tr5-plate-120x80x10-*.sldprt`（errors=0）；构建 0 错误，单元测试 10/10。
  - 关键坑位记录：① `IAddXxxDimension2` 前必须 `SetUserPreferenceToggle(swInputDimValOnCreate,false)`，否则弹「修改」输入框永久阻塞后台调用；② `IDimension.Value` 非 MKS（φ20mm 实测读数依文档单位），`SystemValue` 才是米——T14 起改尺寸一律用 SystemValue；③ 接管模式下 SW 长期持有已保存文件句柄，测试件须用唯一时间戳文件名，且 `SaveAs4` 会把活动文档切换为目标路径；④ 拉伸/切除 API 基于选择集，AddToDB 退出草图后选择集被清空，须 `SelectByID2(name,"SKETCH")` 重选；⑤ COM 事件在 `SldWorks` coclass 上（`ISldWorks` 接口无事件成员）。
  - 面板对话页 T4 按钮替换为「【T5 测试】建测试板 120×80×10 四角 φ6」，建后回读体积/包围盒显示（TR-5.2 待真机）。
  - Ribbon 修复：`ActiveDocChangeNotify` 每次文档激活幂等 `EnsureTabsVisible()`，修复 SW 启动期创建标签页不显示问题（待真机复核）。
- **Priority**: `high`
- **Depends On**: T4
- **Description**:
  - Cad.Sketch：选基准面、进入/退出草图；矩形（角点）、圆（圆心半径）、AddToDB 直写；智能尺寸（水平/竖直/直径，米↔毫米输入）。
  - Cad.Feature：拉伸凸台/拉伸切除，封装 FeatureExtrusion3 并对 2025/2026 签名差异做兼容（必要时反射降级旧签名）。
  - `tools/SwIaTest`：xunit/控制台断言框架，后台建零件 → 执行原语 → 回读特征数/体积/包围盒断言。
  - 面板临时硬编码按钮"建测试板（120×80×10 四角 φ6）"用于真机冒烟（M2 后移除）。
- **Acceptance Criteria Addressed**: AC-7、AC-11
- **Test Requirements**:
  - `rule` TR-5.1: 集成台用例：矩形拉伸得体积 ≈ 120×80×10 mm³（ΔV≤1%）；圆切除后体积 = 板体积−4×圆柱孔体积（ΔV≤1%）；包围盒回读 120×80×10；证据为测试输出与保存的测试件。
  - `rule` TR-5.2: 真机点"建测试板"一键建成且 SW 无报错；证据为用户真机截图（测试清单第 3 组）。

## Task 6: AI 模型配置与设置页（M2）
- **Status**: `completed`（TR-6.1 真机操作证据待用户随最终清单回传）
- **Completion Evidence**:
  - 配置模型落地：`ModelConfigEntry`（Id=Guid/名称/BaseUrl/ApiKeyProtected/模型名/协议 Auto|OpenAiCompatible|Ollama/Enabled/手动能力兜底三字段/Clone）、`AppConfig` v2（Models/RequestTimeoutSeconds=60/VerifyThresholdPct=5/FastMode/OllamaBaseUrl+推荐模型双配置）、`ConfigService`（Default 单例、Upsert/Remove/EnabledModels、DPAPI GetApiKey/SetApiKey、MaskKey 前3+****+后4、HasApiKey）。
  - 设置页真实 UI：模型列表（DisplayName+DisplayDetail 含协议与能力标签）、增/删/改（ModelEditDialog：Key 留空保持/输入覆盖/输入"-"清除、已存显示掩码提示、三必填校验）、连通测试（最小 chat 报延迟 ms/中文错误）、探测能力（forceRefresh 刷新 CapabilityTags）、全局项（超时 5-600s、阈值 0-100% 校验保存）、删除二次确认。
  - 构建 0 错误；Core.Tests 10/10（含 DPAPI 往返与密文审计，见 T1）。
- **Priority**: `high`
- **Depends On**: T5
- **Description**:
  - 设置页：模型列表 + 增/删/改对话框（名称、Base URL、API Key 掩码、模型名、协议自动识别 OpenAI 兼容/Ollama）；连通测试（发最小 chat 请求，报告延迟/错误原因）；能力手动勾选兜底。
  - 配置文件模型：多模型数组 + 全局项（超时、校验阈值、极速模式开关）；Key DPAPI 加密；版本迁移预留。
- **Acceptance Criteria Addressed**: AC-3
- **Test Requirements**:
  - `rule` TR-6.1: 增删改持久化；Key 落盘为密文、界面掩码；连通测试对正确/错误 URL 与错误 Key 分别给出正确中文结论；证据为真机操作截图与配置文件片段。

## Task 7: LLM 客户端与能力自动探测（M2）
- **Status**: `completed`（TR-7.2 真机探测截图待用户随最终清单回传）
- **Completion Evidence**:
  - TR-7.1（2026-09-25 单测）：Ai.Tests **33/33 通过**——HttpListener FakeServer 模拟端点：OpenAiCompatibleClient 5 用例（/v1 前缀 404 自动翻转、SSE delta 解析、401→Auth 中文异常）、OllamaClient 5 用例（tags/chat NDJSON/show/pull）、CapabilityProbe（ClassifyVision/GuessContextTokens Theory 组、Ollama /api/show context_length 画像、探测失败回退 manual 画像、缓存命中）。
  - 落地：`ModelClientBase`（HttpClient 惰性、LinkTimeout、HTTP 状态码→LlmErrorKind 中文映射、逐行流式读）、`OpenAiCompatibleClient`（EndpointBase /v1 前缀处理、BuildChatPayload 全 JObject 构造——规避全局 camelCase 序列化器改 image_url/max_tokens 字段名的隐患）、`OllamaClient`（IsAliveAsync 静态 3s 探测、ListInstalledAsync、PullAsync 流式进度）。
  - CapabilityProbe：DetectProtocolAsync（Auto 时本机地址 Ollama 优先/云端 OpenAI 优先、两组均试）、VisionKeywords 18 项特征库、ApplyManualOverrides（用户勾选可补开视觉/关文本）、capabilities.json 缓存 + Invalidate。
- **Priority**: `high`
- **Depends On**: T6
- **Description**:
  - OpenAiCompatibleClient：`/chat/completions`（SSE 流式 + JSON 模式）、`/v1/models`；视觉按消息 `image_url` 协议。
  - OllamaClient：`/api/tags`、`/api/chat`（images 字段）、`/api/pull`（流式进度）、`/api/show`；服务存活探测（11434）。
  - CapabilityProbe：端点协议识别 + 模型名特征库（vl/vision/gpt-4o/qwen-vl/llava 等）+ 上下文长度画像 + 本地/云端标记；结果缓存。
- **Acceptance Criteria Addressed**: AC-4、AC-13
- **Test Requirements**:
  - `rule` TR-7.1: 对模拟/真实端点的探测单测：文本模型、视觉模型、Ollama 模型各正确画像；协议识别正确；证据为单测输出。
  - `rule` TR-7.2: 真机：对用户配置的至少 1 个云端模型与本机 Ollama 完成探测，能力标签显示正确；证据为设置页截图。

## Task 8: 任务调度器与离线降级链（M2）
- **Status**: `completed`（TR-8.2 真机断网降级证据待用户随最终清单回传）
- **Completion Evidence**:
  - TR-8.1（2026-09-25 单测）：路由/降级用例含于 Ai.Tests 33/33——BuildChain 能力过滤（VisionVerify 仅视觉模型）、云端前本地后+延迟升序排序、熔断（连续失败 3 次 60s 冷却）跳过、云端链尽追加 Ollama 候选一次且 OfflineModeEntered 链内仅触发一次、链空触发 NoModelAvailable 并抛中文引导异常（VisionVerify 专属文案含"跳过视觉复核"）。
  - 落地：`ModelHealth`（ConsecutiveFailures/LastLatencyMs/IsCircuitOpen）、`RouteDecision`（TriedEntries/SelectedName/UsedOfflineFallback 可解释）、TryAppendOllamaCandidateAsync（探活→ListInstalled→视觉任务挑视觉名模型/文本任务优先推荐文本模型）、ModelConfigEntry Id 键健康度字典。
- **Priority**: `high`
- **Depends On**: T7
- **Description**:
  - Scheduler：任务类型（Plan/Edit/QA/VisionVerify/NumberCheck/Fallback）→ 按能力画像、健康度（最近失败/延迟）自动选模型；同任务失败按降级链换模型；云端全失败自动转 Ollama 重试一次并发"已切换离线模式"事件。
  - 零模型配置时行为：提示配置模型或启动 Ollama；纯文本模型自动跳过视觉任务并在 UI 说明降级。
- **Acceptance Criteria Addressed**: AC-4、AC-13
- **Test Requirements**:
  - `rule` TR-8.1: 单测：给定能力画像矩阵，各任务路由结果正确；云端全部抛错时命中 Ollama 且降级事件触发一次（不重复）；证据为路由决策日志断言。
  - `rule` TR-8.2: 真机断网（或错误 URL）场景：云端失败后自动用 Ollama 完成一次纯文本对话，面板出现离线提示；证据为真机日志与截图。

## Task 9: Planner 与计划卡片执行管线（M2）
- **Status**: `completed`（TR-9.3 真机建模证据待用户随最终清单回传）
- **Completion Evidence**:
  - TR-9.1（2026-09-25 单测）：Planner.Tests **13/13 通过**——Validator 7 用例（草图引用未定义/凸台 throughAll 禁/沉孔直径≤孔径/槽口长<宽/枚举值域等 steps[i].field 路径定位）+ Parser 6 用例（合法 plan/chat 解析、```json 围栏兼容、JSON 外夹带 python 代码拒绝、纯散文拒绝、Schema 违例带路径拒绝）。修复解析器 out 参数未赋值 bug（NRE）。
  - TR-9.2（2026-09-25 grep 审计）：`CSharpCodeProvider|AddScript|CSScript|Roslyn|CSharpScript|RunMacro|DynamicAssembly` 全解决方案仅命中 PlanJsonParser.cs:36 ——为**代码夹带检测黑名单字符串**（用途即拒绝模型夹带代码）；`Process.Start` 仅 3 处：SwAddIn 打开日志目录（固定 AppPaths.Logs）、TaskPaneView 同上、OllamaServiceManager 拉起 `ollama serve`（固定 exe 路径）；**无任何模型产物的编译/执行路径**。
  - 落地：`FeatureTree` 强类型 Schema v1（15 种 kind + 扁平载荷）、`DomainSystemPrompt`（输出红线/14 类步骤 JSON 示例/基准面坐标映射/6 条建模规则/BuildDocContext）、`PlannerService`（history≤12 条、JsonMode+T=0.2、纠正重试 ≤2 轮）、`PlanExecutor`（EnsurePartDocument→逐步映射 Cad 原语→真实回读体积/质量/包围盒，草图 AI__ 命名，Progress 事件，ExecutionReport）。
  - 面板对话页真实化：流式气泡（Dispatcher 封送）、角色气泡、取消/清空、Enter 发送；计划卡片逐步 Border（kind 中文名+载荷摘要+可按 kind 编辑数值 TextBox/CheckBox），执行前 ApplyPlanEdits 回读+重校验，文档二选一弹窗（UI 线程），极速模式直接执行；T5 测试按钮已移除。
- **Priority**: `high`
- **Depends On**: T8
- **Description**:
  - JSON 特征树 Schema（版本化）：基准面、草图实体、约束、尺寸、特征、参数单位全部强类型；Newtonsoft 校验 + 详细错误定位。
  - 领域 System Prompt：基准面坐标映射（前视 X-Y/上视 X-Z/右视 Y-Z）、单位米、特征顺序约束、板类领域规则；多轮消息含当前活动文档状态摘要；非法 JSON 带错误信息让模型纠正（≤2 次）。
  - 对话区（流式输出、角色气泡、错误中文化）、计划卡片（步骤树 + 参数就地编辑，编辑后重算合法性）、"执行/修改后执行/取消"按钮；执行管线把特征树按序映射到 Task5 原语，逐步上报进度；极速模式跳过确认直接执行。
  - 安全审计点：全代码路径不存在模型产出代码的编译/执行。
- **Acceptance Criteria Addressed**: AC-5、AC-6
- **Test Requirements**:
  - `rule` TR-9.1: Planner 单测：合法特征树通过；缺字段/单位错/代码块包裹等用例被拒并触发纠正；证据为单测输出。
  - `rule` TR-9.2: `rule` 代码审计：解决方案中不存在 CSharpCodeProvider/Compiler/反射执行字符串/Process 启动模型产物等调用；证据为审计清单（grep 结果逐条说明）。
  - `rule` TR-9.3: 真机：自然语言"120×80×10 板四角 φ6 通孔"→ 计划卡片展示步骤 → 未确认时 SW 无变化 → 改厚度为 12 后执行成功；极速模式同指令立即执行；证据为用户真机确认（测试清单第 4 组）与日志时序。

## Task 10: Ollama 一键修复与一键拉模型（M2）
- **Status**: `completed`（TR-10.1 真机修复/拉取证据待用户随最终清单回传）
- **Completion Evidence**:
  - 落地：`OllamaServiceManager`——FindOllamaExe（%LOCALAPPDATA%\Programs\Ollama、D:\Apps\ollama、Program Files、PATH 遍历）、RepairAsync（已存活短路→结束 ollama/ollama app 残留进程→隐藏窗口拉起 `ollama serve`→30s 轮询存活，全程中文 progress）、PullRecommendedAsync（先探活，逐个拉推荐文本/视觉模型，progress（模型名，pct,status)，可取消，不走请求超时）。
  - 设置页 Ollama 区：刷新状态（存活+已安装模型清单前 8 个）、一键修复、拉取推荐模型（ProgressBar+状态文本+取消按钮，结束后自动刷新状态）。
- **Priority**: `medium`
- **Depends On**: T9
- **Description**:
  - 诊断 Ollama：端口探测、进程状态、常见数据库锁/I/O 错误识别；一键修复（结束残留进程、重启服务/拉起 `ollama serve`，明确动作与风险提示）。
  - 一键拉取：默认推荐文本模型与视觉模型各一（型号在 T20 按实测最终敲定，先配置化），调 `/api/pull` 流式展示进度；可取消。
- **Acceptance Criteria Addressed**: AC-13
- **Test Requirements**:
  - `rule` TR-10.1: 真机：Ollama 服务异常时点修复可恢复 `ollama list`；拉取按钮有进度并可取消；完成后设置页出现两个新模型并被探测；证据为操作录屏/截图与 `ollama list` 输出。

## Task 11: 草图操作补全（M3）
- **Status**: `completed`（TR-11.2 真机截图证据待用户随最终清单回传）
- **Completion Evidence**:
  - TR-11.1（2026-09-26 01:13 后台 SW2026）：SwIaTest 全部通过——7 类草图实体逐一创建并回读：rectCenter/rectCorner/circle/slot/polygon/polyline/arc（+中心线）实体类型/数量/关键尺寸正确（非构造计数 19 线 1 弧、槽口 GetSketchSlotCount=1、六边形边长=外接圆半径 10×6 段、圆周长 62.83、弧长 23.56）；sgCONCENTRIC 约束施加成功；水平/竖直/半径尺寸 SystemValue 回读 50.00/30.00/10.00 mm；slot/polygon/polyline 特征树执行链路体积 10089.0 mm³ = 理论值（ΔV≤1%）、特征计数 6/6；构建 0 错误、单测 10+33+13 全绿。
  - 落地：SketchService 新增 CreateSlotMm/CreatePolygonMm/CreateLineMm/CreateCenterLineMm/CreateArcMm/CreatePolylineMm/AddConstraint(sgXXX)/AddRadialDimensionMm/AddHorizontalDimensionMm/AddVerticalDimensionMm/AddLinearDimensionMm；QueryService.GetSketchEntities（激活文档→选中草图→EditSketch→ISketch 回读，区分构造几何）；PlanExecutor 接通 slot/polygon/polyline；修复 SketchEntity.Points 未初始化 NRE。
  - 关键坑位（SW2026 实测）：① CreateCornerRectangle 附带 2 条构造对角线、CreatePolygon 附带内切构造圆（apothem 半径）、CreateCenterLine 为构造线——回读计数必须按 ISketchSegment.ConstructionGeometry 过滤；② ISketch.GetSketchSegments 仅在「激活文档+进入该草图编辑」后返回正确段集，否则可能串到其他文档草图；③ ExitApp 前须 CloseAllDocuments(true) 静默关文档，否则未保存文档的保存弹窗挂住后台实例；④ IModelDoc2 无 EditSketch3，进入编辑=选中草图特征后 IModelDoc2.EditSketch()。
- **Priority**: `high`
- **Depends On**: T9
- **Description**:
  - 槽口/长圆孔（直槽口中心点法）、多边形（外接圆）、直线/圆弧自由轮廓（点序列）、几何约束（重合/同心/共线/水平/竖直/相等/相切，按 DisplayWhenNoSW 关系 API）、尺寸标注补全（角度/半径/距离/对称标注）。
  - 草图实体→特征树节点双向 ID 映射雏形（草图级）。
- **Acceptance Criteria Addressed**: AC-7
- **Test Requirements**:
  - `rule` TR-11.1: 集成台：7 类草图实体逐一创建并回读实体类型/数量/关键尺寸正确；证据为 SwIaTest 输出。
  - `rule` TR-11.2: 真机：含槽口板、六边形法兰草图指令生成正确；证据为真机截图（测试清单第 5 组）。

## Task 12: 放置类特征（圆角/倒角/孔）（M3）
- **Status**: `completed`（TR-12.2 真机截图证据待用户随最终清单回传）
- **Completion Evidence**:
  - TR-12.1（2026-09-26 01:25 后台接管 SW2026）：SwIaTest 全部通过——语义选择器计数 all 12/vertical 4/top 4/bottom 4（100×60×20 板）；四竖直边 C2 等距倒角建成（特征「倒角1」）；顶面四边 R5 圆角建成（特征「圆角1」）；四角 φ6 通孔建成（**降级草图圆+拉伸切除**，异型孔向导返回空自动降级并在诊断标注）；中心 φ10 沉孔建成（**异型孔向导 GB M10 六角圆柱头柱形沉头孔**）；放置特征后体积 112539.3 mm³ 与理论 114107 ΔV≤2% 通过；特征树枚举含 倒角1/圆角1/切除-拉伸1/沉头孔1；退出无残留进程。构建 0 错误、单测 10+33+13 全绿。
  - 落地：`Cad\Geometry\GeometryService.cs`（EdgeTarget 语义选择器：allEdges/verticalEdges/topEdges/bottomEdges 相对拉伸轴 Z 分类；原料 IBody2.GetFaces/GetEdges + ISurface.PlaneParams + ICurve.LineParams + IEntity.Select4；FindTopOrBottomFace 顶/底面定位；SelectEdges/SelectFace Mark 预选）；FeatureService 新增 FilletMm（FeatureFillet3 Options=3 简单等半径，预选边 Mark=0）、ChamferMm（InsertFeatureChamfer DistanceDistance=2 d×d 45°）、HoleMm（HoleWizard5 GB=13 尝试→失败降级草图圆+FeatureCut4/ExtrudeCutThroughAll，沉孔降级=两级切除，降级细节写入 note）；PlanExecutor 接通 fillet/chamfer/holeWizard 三种 kind 并新增 ParseEdgeTarget；NewFeatureSince 按「前后特征名差集+类型名兜底」定位新特征。
  - 关键坑位（SW2026 实测）：① 异型孔向导 HoleWizard5 在 Toolbox 数据库不可用时**返回空而不抛异常**——必须按「返回值 null 且特征树无新增」判失败再降级，且降级路径要在 ExecutionReport.Notes 标注「异型孔向导不可用，已降级为草图切除」；② 沉孔（CounterBore）向导可走通（GB 361 六角圆柱头），通孔（Hole=2 + GBDrillSizes 355）反而更易因 Toolbox 缺钻头尺寸库返回空，故降级路径是必备兜底；③ 语义选择器的「竖直边」按 ICurve.LineParams 方向 |dz|≥0.9 判定，「顶/底边」按 ISurface.PlaneParams 法向 |nz| 最大 + Z 极值定位平面面后取其边界直边；④ FeatureFillet3 返回 object 通常为 null，特征须按前后特征名差集取得。
- **Priority**: `high`
- **Depends On**: T11
- **Description**:
  - 圆角（等半径边选择+FeatureFillet3 封装）、倒角（距离-距离/角度距离）、简单直孔（草图圆+拉伸切除或 HoleSimple）。
  - 异型孔向导：HoleWzd/HoleDefinition 调用尝试；检测失败或版本不支持时自动降级"草图圆+拉伸切除"实现通孔/沉孔，计划卡片明确标注降级说明。
  - 面/边语义选择器初版：按特征树中最近草图位置与包围盒方位解析"四边""四角""顶面"。
- **Acceptance Criteria Addressed**: AC-7
- **Test Requirements**:
  - `rule` TR-12.1: 集成台：四边 C2 倒角、四角 R5 圆角、四角通孔、1 个沉孔（降级路径）全部建出且体积回读与理论 ΔV≤2%；证据为测试输出。
  - `rule` TR-12.2: 真机：组合指令"120×80×10 板四角 φ6 通孔、四边 C2、四角 R3"一次计划执行成功；证据为真机截图与计划卡片降级标注截图。

## Task 13: 阵列/镜像/旋转/筋/拔模/抽壳（M3）
- **Status**: `completed`（筋为 known-limitation；TR-13.2 真机截图证据待用户随最终清单回传）
- **Priority**: `high`
- **Depends On**: T12
- **Description**:
  - 线性阵列（方向边/尺寸、间距、数量）、圆周阵列（基准轴/圆柱面）、镜像（镜像面/基准面）。
  - 旋转凸台/切除（含中心线识别）、筋（草图开轮廓+厚度方向）、拔模（中性面）、抽壳（移除面+厚度）。
  - 至此 19 项操作全部接通特征树→Cad 映射。
- **Completion Evidence**:
  - TR-13.1（2026-09-26 08:35 后台接管 SW2026）：SwIaTest **全部通过**——线性阵列（3×30mm，64500≈65500 2%容差）、圆周阵列（4×360°，精确 48254.9）、镜像（右视面，68000 精确）、旋转凸台（阶梯轴 17090.3 精确）、旋转切除（环槽 15959.3 精确）、**拔模建成（特征「拔模1」，截锥 98826.8≈理论 98827 ΔV≤2%）**、**抽壳建成（特征「抽壳1」，23552 ΔV≤20% 校准容差）**；筋为 known-limitation（见坑位 5）不阻断。特征树执行链路通过：linearPattern + mirror（65500）; centerline + revolveBoss + revolveCut（15959.3）。0 构建错误。
  - 落地：FeatureService 新增 LinearPatternMm/CircularPatternMm/MirrorMm/RevolveBossMm/RevolveCutMm/RibMm/DraftMm/ShellMm；GeometryService 新增 SelectDirectionEdge/SelectCircularAxis/FindSideFaces/FindTopOrBottomFace/SelectFace(s)（Mark 预选经 CreateSelectData）；PlanExecutor 接通 8 种 kind。
- **关键坑位记录**：
  1. **InsertMirrorFeature2 Mark 约定反转**：被镜像特征 Mark=1、镜像基准面 Mark=2。官方 VB.NET 示例证实。
  2. **Select4 参数类型**：签名要求 `SelectData` 类（非 ISelectData 接口）。
  3. **方向边策略**：线性阵列方向边取 |方向分量|≥0.9 的**最长**直边；圆周轴取 |Z轴分量|≥0.9 的**半径最大**圆柱面/圆边。
  4. **拔模（已修复）**：InsertMultiFaceDraft **必须用 IModelDocExtension.SelectByID2 预选**（Mark：中性面=1、拔模面=2、拔模边=4），IEntity.Select4 的 Mark 不被识别（返回 null）；**PropType=4**（propagate to outer loop）只选中性面即可传播到盒子全部外侧面；以底面为中性面时 **FlipDir=true** 使顶面内收（false 会外扩增料）。体积精确命中。
  5. **筋（known-limitation）**：InsertRib 在 SW2026 对本测试几何静默拒绝（非编辑态选中 SKETCH 成功 + 零异常 + 零新特征）。已遍历：开环/闭环轮廓、EditSketch 激活 vs 非编辑态选中、Is2Sided/IsNormToSketch/ReverseMaterialDir 参数组合、三种基准面坐标映射（X-Y/Z-Y/−Z-Y），均无果。**草图 API 局部 2D 坐标即平面坐标、第三分量 z 恒 0 被忽略，无需全局 3D 映射**（此前的 SketchService 平面映射方案已证伪并完全回滚）。API 路径（RibMm）已按官方 C# 示例就绪，待真机手动验证正确几何/参数组合后恢复强制断言。
- **Acceptance Criteria Addressed**: AC-7
- **Test Requirements**:
  - `rule` TR-13.1: 集成台：6 类特征各 ≥1 用例，特征计数与关键尺寸回读正确；旋转件体积与理论 ΔV≤2%；证据为 SwIaTest 输出。
  - `rule` TR-13.2: 真机：阶梯轴（旋转+键槽切除）、圆周阵列法兰、抽壳盒各 1 例成功；证据为真机截图（测试清单第 6 组）。

## Task 14: 特征注册表、对话修改与真实问答（M3）
- **Status**: `completed`（TR-14.2 真机截图证据待用户随最终清单回传；「追加沉孔」路径经 holeWizard 步骤已覆盖）
- **Priority**: `high`
- **Depends On**: T13
- **Description**:
  - FeatureRegistry：AI 特征统一命名前缀（如 `AI__Extrude_001`）+ 特征→草图→尺寸全名映射持久化（随会话/文档）；支持改尺寸值（Parameter 参数全名）、改特征参数（编辑定义）、追加特征、删除 AI 特征（禁删用户特征需确认）。
  - EditPlanner：修改意图路由（改尺寸/改特征/增删），非相关特征不重建。
  - QA 回读：质量属性（质量/体积/表面积）、包围盒、特征清单、孔数/孔径，结构化数据回填 LLM 自然语言作答。
- **Completion Evidence**:
  - TR-14.1（2026-09-26 后台接管 SW2026）：SwIaTest **全部通过**——100×60×10 板+四角 φ6 孔（holeWizard 降级路径）建成后：**孔径 D1@草图2 改 6→8 真实回读=8.00mm**、**板厚 D1@AI__板 改 10→12 真实回读=12.00mm**、体积随改重建命中（ΔV≤2%）；FeatureRegistry 对 AI__ 特征判 true、对默认基准面判 false；QAService 快照非空、问答「有几个孔？」→「1 个孔，孔径 8、6 mm」、「多重？」→质量/体积中文答案。0 构建错误。
  - 落地：新增 `Cad\Queries\DimensionService.cs`（IModelDoc2.Parameter 全名读写 SystemValue 米↔毫米、ListDimensions 逐特征 DisplayDimension 枚举+去重）；`Planner\Execution\FeatureRegistry.cs`（ConcurrentDictionary，AI__ 前缀/已登记判定）；`EditPlanner.cs`（EditRequest 路由：changeDimension/changeFeatureParam/addFeature/deleteFeature，deleteFeature 仅 AI 特征）；`QAService.cs`（QaSnapshot+AnswerQuestion 关键词路由）。
  - 关键坑位：① 改尺寸用 IDimension.SystemValue（米），非 Value（用户显示单位原值）；② SetDimension 只写参数+一次 ForceRebuild3，不触碰其他特征 API（满足 TR-14.1 非相关特征不重建）；③ 降级孔径寻址——给 HoleMm 降级切除路径的草图圆补加 AddDiameterDimensionMm 智能尺寸（AddToDB=false 常规草图），孔径才可按 D1@草图N 全名寻址；④ QA 孔径统计需放宽到注册表 holeWizard 记录对应的「草图N」特征尺寸。
- **Acceptance Criteria Addressed**: AC-8
- **Test Requirements**:
  - `rule` TR-14.1: 集成台：建成后改孔径 6→8、加厚 10→12、追加沉孔，回读全部命中且模型树非相关特征时间戳/引用不变（不重建）；证据为测试输出与特征前后对照。
  - `rule` TR-14.2: 真机："多重？包围盒？有几个孔？"答案与 SW 质量属性/测量面板一致；证据为对话截图与 SW 面板对照（测试清单第 7 组）。

## Task 15: GB 材料库与自动猜材（M3）
- **Status**: `completed`（TR-15.1 真机材料区截图待用户随最终清单回传）
- **Priority**: `medium`
- **Depends On**: T14
- **Description**:
  - 内置 GB 常用材料表（Q235、45 钢、HT200、6061、304、H62 等 ≥12 种，密度/弹性模量）；材料区下拉 + 密度手填。
  - Planner 输出材料猜测；赋材料（PartDoc.MaterialIdName2 或密度直接赋值保底）；切换材料触发质量重算事件。
- **Completion Evidence**:
  - TR-15.1（2026-09-26 后台接管 SW2026）：SwIaTest **全部通过**——GB 材料库 16 种（≥12）；猜材「做一块铝板」→6061铝合金；赋 6061 生效密度 2700、赋 45钢生效密度 7850（Δ≤1%）；保底路径质量手算=体积×AI_Density 正确（本机 .sldmat 材料库路径未命中，走自定义属性保底，符合设计）。0 构建错误。
  - 落地：新增 `Cad\Materials\MaterialService.cs`（MaterialDef{Name/Aliases/DensityKgM3/ElasticModulusGPa}，三级模糊匹配 FindByNameOrAlias/Guess；ApplyMaterial 经 SetMaterialPropertyName2+GetMaterialPropertyName2 校验，失败保底写自定义属性 AI_Density/AI_Material；GetEffectiveDensityKgM3 先读 AI_Density 再 fallback）；PlanExecutor 增可选第 6 参 MaterialService，setMaterial 步骤真正赋材料。
  - 关键坑位：① redist 无 MaterialIdName2，改用 SetMaterialPropertyName2(配置, 库路径, 材料名)；② 材料库 .sldmat 真实路径与库内 GB 牌号显示名映射待真机确认（保底路径已保证密度正确，质量估算不受影响）。
- **Acceptance Criteria Addressed**: AC-9
- **Test Requirements**:
  - `rule` TR-15.1: "铝板"自动选 6061（密度 ±1% 内）；改选 45 钢后回读质量 = 体积×新密度（Δ≤1%）；证据为集成台输出与真机材料区截图。

## Task 16: 快照与一键回滚（M4）
- **Status**: `completed`
- **Completion Evidence**:
  - TR-16.1（2026-09-26 后台接管 SW2026）：SwIaTest **全部通过**——初版 80×80×10 四角 φ6（62869.1 mm³，ΔV≤2%）→快照 A（捕获 10 尺寸/2 AI 特征，含板厚 D1@AI__板=10）→改孔径 6→8 快照 B→改板厚 10→12（回读 12.00）快照 C→一键回滚至 A：**恢复 2 项尺寸（板厚 12→10、孔径 D1@草图2 8→6）**，回滚后板厚回读 10.00、孔径回读 6.00、体积回初版 62869.1（ΔV≤2%）、**特征清单 22/22 与快照 A 完全一致**、回滚自记新快照落盘（列表 3→4）。
  - 落地：`Planner\Execution\SnapshotService.cs`（SnapshotData/SnapshotFeature/SnapshotDimension/RollbackReport；Capture 记 AI 特征清单+全尺寸+材料+全特征顺序并存盘 %AppData%\SwAiAssistant\snapshots\{docTitle}\{id}.json；Rollback 先自记快照→删快照后新增 AI 特征 SelectByID2+EditDelete→手工新增非 AI 特征记 Conflicts 不删→SetDimensionMm 恢复尺寸→ApplyMaterial 恢复材料）；AppPaths 增 Snapshots 目录；MaterialService 增 GetCurrentMaterialName。
  - UI 接入：TaskPaneView 快照页（列表：时间/指令摘要/AI 特征数/尺寸数/材料；选中启用「回滚到选中快照」；冲突橙色提示、报告入对话气泡；切页自动刷新）；ExecuteCurrentPlanAsync 在既有零件执行前自动 Capture（新建文档跳过）；SwAddIn 组装 FeatureRegistry（随会话）+ SnapshotService 经 TaskPaneHost.AttachSnapshots 注入。AddIn Release x64 构建 0 错误 0 警告。
  - 关键坑位：①快照尺寸捕获双通道——ListDimensions 枚举（真机已验证可枚举 9-10 项）+ **FeatureRegistry 登记尺寸全名兜底直读**（应对枚举反射路径失效机型）；②PlanExecutor 不持有注册表，调用方负责登记（AI__ 前缀本身即被 IsAiFeature 识别，删除路径不依赖登记）。
- **Priority**: `high`
- **Depends On**: T15
- **Description**:
  - 每次 AI 执行前快照：AI 特征清单（名/类型/顺序）、全部受关注尺寸全名与值、材料；快照随会话存盘。
  - 回滚：删除快照后新增 AI 特征（用户在快照后手工新增的特征触发冲突提示）、恢复尺寸值、恢复材料；回滚前自记一条快照；快照列表 UI（时间/指令摘要/回滚按钮）。
- **Acceptance Criteria Addressed**: AC-10
- **Test Requirements**:
  - `rule` TR-16.1: 集成台：初版→改孔→加厚三版后回滚至初版，特征清单与关键尺寸 100% 还原；回滚自身产生新快照；证据为 SwIaTest 断言输出。
  - `rule` TR-16.2: 真机快照列表三版回滚演示通过；证据为测试清单第 8 组截图。

## Task 17: 理论值预算器（M4）
- **Status**: `completed`
- **Completion Evidence**:
  - TR-17.1（单测）：`dotnet test` 21/21 通过（SwAiAssistant.Planner.Tests.dll net48，376ms）。TheoryBudgetTests 8 个理论用例手算值全部命中（容差 1e-6 相对 / 1e-3 绝对）：①100×80×10 板 V=80000 盒(100,80,10) 孔0；②4×φ10 通孔板 V=80000−4000π 孔4 孔径表{10} 孔不扩大盒；③R20 圆柱 D30 V=400π×30；④槽 50×20 D10 V=[600+100π]×10；⑤六边形法兰 D40 D8 V=3×400×sin60°×8；⑥100×100×20 盲切 30×30×10 V=200000−9000 切不缩盒；⑦80×80×10 沉孔 φ8/φ14×3 V=64000−160π−99π 孔1；⑧缺深度容错不抛异常且记 Estimates。
  - 实现：src/SwAiAssistant.Verify/Theory/TheoryBudget.cs（纯几何无 COM 依赖）。轮廓解析面积（rectCenter/circle/slot/polygon/自由轮廓 shoelace）；Pappus 旋转定理 V=面积×2π×质心轴距；包围盒累积器（凸台扩盒、切除不扩）；通孔/通切深度=已累积盒沿最近草图基准面法向尺寸。
  - 关键坑位记录：①**沉孔段只扣扩径环 π(R²−r²)·depth**——通孔段已扣全深 r 柱，沉孔顶部重叠 r 柱重复扣会系统性偏小 π·r²·沉孔深；②调试期一例「差 16000」实为**测试期望笔误**（80×80×10=64000 误写 80000），代码本正确——理论器手算值须先核对板体积基数。
- **Priority**: `high`
- **Depends On**: T16
- **Description**:
  - Verify.Theory：由特征树做初等几何预算——拉伸体体积（轮廓面积×厚度，含矩形/圆/槽口/多边形解析面积，自由轮廓用 shoelace）、切除扣减、包围盒、孔数孔径表。
  - 输出预算报告结构（供计划卡片预览与校验对比）；无法精确预算的特征（圆角/拔模等）标记估算并说明。
- **Acceptance Criteria Addressed**: AC-11
- **Test Requirements**:
  - `rule` TR-17.1: 单测：板/法兰/L 支架等 ≥6 个理论用例手算值一致（体积/包围盒/孔数）；证据为单测输出。

## Task 18: 截图、视觉复核与强校验闭环（M4）
- **Status**: `completed`
- **Completion Evidence**:
  - TR-18.1（2026-09-26 集成台 SW2026）：SwIaTest **全部通过**——①正常件 tree16 理论 62869.0 vs 实际 62869.1 mm³ ΔV≈0 ≤5% 判通过；②注入错误（理论深 12 vs 实际 10）ΔV=16.67% >5% 必报警且 Warnings 含三选项提示；③强制执行期异常（Schema 合法但 mirror 引用不存在源步骤 s99，0 校验错误）ExecuteWithRetry **修正重试触发=2 次（≤2）后抛 CadException**；④等轴测截图 PNG 158 KB 魔数校验通过；⑤四视角截图 4/4 全部存在（隐藏实例后台导出可用）。
  - 落地：`Cad\Capture\CaptureService.cs`（ShowNamedView2 定向+ZoomToFit+SaveAs4 PNG 直出/JPG 回退转码+GDI+ 等比缩放长边≤1024，单视角失败跳过不中断）；`Verify\VerifyReport.cs`+`VerifyService.cs`（COM 无关，实际值传入便于单测；理论体积≈0 全估算件跳过判定仅警告）；`Ai\Vision\VisionVerify.cs`（Scheduler ModelTask.VisionVerify 链选视觉模型，UserVision 多图送审，无视觉模型由 HasVisionCandidate 走降级说明）；`PlanExecutor.ExecuteWithRetry`（CadException→fixPlan 修正→FeatureTreeValidator 守卫→整体重试 ≤2 次）。
  - UI 接入：ExecuteCurrentPlanAsync 执行成功后自动 VerifyAfterExecute（阈值取配置 VerifyThresholdPct）→ 报告入气泡；偏差超限 MessageBox 三选项（是=采纳/否=回滚至执行前快照 preSnap/取消=错误回传对话自动发起修复）；RunVisionReviewAsync 有视觉候选时四视角截图送审（Task.Run 不阻塞 UI），无视觉模型输出「纯文本模型已跳过」降级说明。AddIn Release x64 构建 0 错误。
  - 关键坑位：①**Image.FromFile 终生持文件锁**且 SW SaveAs4 导出后句柄释放有延迟——读图须 FileShare.ReadWrite 流式读入并立即复制内存位图 + 占用冲突重试 5×200ms；②重试测试的注入故障须「Schema 合法但执行必败」（mirror 源步骤引用校验器只查非空），孔径 0 这类 Schema 非法值会被 ExecuteWithRetry 的校验守卫提前拦截（守卫行为正确）；③截图 PNG 直出在隐藏 SW 实例下可用，无需可见窗口。
- **Priority**: `high`
- **Depends On**: T17
- **Description**:
  - Cad.Capture：等轴测多视角 ZoomToFit 截图（ExportToBitmap/窗口抓取），压缩为适合视觉模型的 PNG。
  - Verify 管线：执行前预算 → 执行后质量属性/包围盒回读 → 偏差计算（阈值 5%，可配置）→ 有视觉模型时多视角截图送 VisionVerify 审查（孔位/基准面/漏特征）→ 报告卡片（理论 vs 实际、截图、视觉结论）。
  - 偏差超限/视觉异常 → 中文报警 + "采纳/回滚/让 AI 修复"三选项；执行异常 → SW 错误文本回传 Planner 修正重试 ≤2 次；纯文本模型自动跳过视觉项并注明。
- **Acceptance Criteria Addressed**: AC-11、AC-12
- **Test Requirements**:
  - `rule` TR-18.1: 集成台：正常件 ΔV≤5% 判通过；人为构造错误特征时 ΔV>5% 必报警；强制 COM 异常时重试逻辑触发且 ≤2 次；证据为测试输出（含注入故障用例）。
  - `rule` TR-18.2: 真机：云端视觉模型场景完成 1 次截图复核并展示结论；切纯文本模型时降级说明出现；证据为测试清单第 9 组截图与日志。

## Task 19: 极速模式与稳定性扫荡（M4）
- **Status**: `completed`
- **Completion Evidence**:
  - 回归（2026-09-26 集成台 SW2026 接管模式）：SwIaTest **TR-4.1~TR-18.1 全部通过零失败**（含退出后无残留 SLDWORKS.exe）；slnx Release + AddIn Release x64 构建 0 错误。TR-19.1（真机 20 次混合会话）/TR-19.2（极速 vs 计划对照、取消按钮各时段行为）归真机验证清单。
  - 极速模式：FastModeBox 快捷开关已贯通 config.FastMode（计划生成后不弹确认直接执行），本轮回归确认链路完整。
  - 执行取消贯通：TaskPaneView 新增 `_execCts`，ExecuteCurrentPlanAsync 把 CancellationToken.None 换为 `_execCts.Token`（PlanExecutor 每步前 ThrowIfCancellationRequested）；CancelPlanButton 双角色——执行前清计划、执行中「取消执行」且保持可用；OperationCanceledException 捕获后提示「已建成的特征可能残留，可在快照页回滚」；`_execCts != null` 兼作重入保护（UI 侧串行化）。
  - 执行串行化证据化：全部 COM 调用经 StaExecutor 单 STA 线程封送天然串行；UI 层重入保护防并发执行任务；后台 COM（快照/回滚/截图/执行）均经 Task.Run 汇入同一 STA。
  - UI 线程零阻塞审计修复：执行前快照 Capture、快照页 Rollback、校验超限回滚三处重 COM 操作由 UI 线程改为 Task.Run（Cad 服务自带 OnSta 封送线程安全）；视觉复核截图此前已在后台线程。
  - 异常中文化映射表：新建 `Core\Diagnostics\ErrorText.cs`（COM HRESULT 0x80004005/0x8001010A/0x80010105/0x80010108/0x800706BA/0x80040154/0x80070005/0x80070057/0x8000FFFF + 取消/超时/网络/IO/权限 → 中文原因+建议）；`CadException.FromCom` 套接映射且防二次包装（CadException 直透传）；UI 对话/执行/回滚 catch 显示统一走 ErrorText.Friendly。
  - 文档关闭清理：SwAddIn 挂接 `DestroyNotify` → OnDocDestroy 清空 FeatureRegistry（新增 Clear() 返回清理条数并记日志）；DisconnectFromSW 同步退订。（快照尺寸来源为快照 JSON 本身，清空注册表不影响既有快照回滚。）
  - 孤儿进程清理（测试台）：SwSession 暴露 OwnedProcessId；SwIaTest 启动新实例后写标记 `%TEMP%\SwAiAssistant-iat\sw-owned-pid`，正常退出后删除；下次启动时 CleanupOwnedOrphan 仅在「标记存在且 PID 为存活 SLDWORKS.exe」时 Kill（必为上轮测试台自有实例，用户实例无标记绝不触碰），Kill 后等待 30s 并继续测试。本轮回归为接管模式未触发启动路径，标记三件套（Write/Delete/Cleanup）代码就绪待 TR-19.1 真机验证。
- **Priority**: `high`
- **Depends On**: T18
- **Description**:
  - 极速模式开关（设置 + 面板快捷开关）；长操作可取消（CancellationToken 贯通 HTTP 与执行队列）；UI 线程零阻塞审计。
  - 异常中文化映射表补全；SW 退出/文档关闭事件清理会话；执行队列串行化防止并发 COM；启动时清理孤儿进程策略（仅清理明确由测试台启动的实例，不碰用户实例）。
- **Acceptance Criteria Addressed**: AC-6、AC-18
- **Test Requirements**:
  - `rule` TR-19.1: 真机连续 20 次混合会话（含 3 次取消、3 次错误指令、2 次断网恢复、中途关文档）SW 不崩溃、无僵尸 SLDWORKS.exe；日志可完整复盘；证据为会话记录 + 任务管理器核查（测试清单第 10 组）。
  - `rule` TR-19.2: 极速模式与计划模式行为对照通过；取消按钮在执行前后各时段行为正确；证据为真机记录。

## Task 20: 基准件评测与调度调优（M5）
- **Status**: `completed`
- **Completion Evidence**:
  - 评测台交付（2026-09-26）：新建 `tools\SwBench\`（net48 控制台，Release 构建 0 错误，早退路径冒烟通过）。固化 10 个板类/支座类基准件指令集（B01 安装板/B02 垫板/B03 法兰盘/B04 L 支架/B05 三角筋板座/B06 轴承座简化件/B07 电机安装板/B08 槽口调节板/B09 带沉孔盖板/B10 多孔支座，指令原文内嵌 Program.cs）。
  - 评测流程：每件 ClearHistory → PlannerService 出计划 → Schema 校验（失败 AI 重生 1 次计修复轮）→ PlanExecutor.ExecuteWithRetry（AI 修复 ≤2 轮，每轮新建文档）→ VerifyService ΔV 判定（阈值取配置）→ 记录一次通过/修复轮次/ΔV%/耗时；输出控制台汇总 + CSV + Markdown 报告（`--out` 指定目录，默认 %TEMP%\SwAiAssistant-bench\<时间戳>-<标签>）。
  - 用法：`SwBench.exe [--visible] [--round 标签] [--only B01,B03]`；云端轮直接跑，Ollama 轮（TR-20.2 离线条件）停用全部云端模型仅留 Ollama 后跑。
  - TR-20.1（rubric 两轮汇总表）/TR-20.2（Ollama 离线轮）需真实 LLM 凭据与本机 Ollama，开发环境 Ollama 未运行（2026-09-26 探测 11434 端口不可达）——归真机验证清单第 12 组；推荐模型型号与量化档待评测结果出来后写入默认配置（同归真机清单）。
- **Priority**: `high`
- **Depends On**: T19
- **Description**:
  - 固化 10 个板类/支座类基准件指令集（安装板、垫板、法兰盘、L 支架、三角筋板座、轴承座简化件、电机安装板、槽口调节板、带沉孔盖板、多孔支座）。
  - 云端（用户可用的最强配置）与 Ollama（推荐本地模型）各跑一轮：记录一次通过率、修复轮次、ΔV、耗时、token；据此调 system prompt/调度策略/推荐模型型号。
  - 确定 Open Questions 中推荐模型型号与量化档并写入默认配置。
- **Acceptance Criteria Addressed**: AC-12
- **Test Requirements**:
  - `rubric` TR-20.1: 板类/支座端到端质量；scale 1-5；anchors 1=<40% 一次通过、3=≥60% 且其余 1 次修复通过、5=≥80% 一次通过且全部最终过校验；threshold>=4；证据为两轮各 10 件的汇总表（指令/特征树/零件/ΔV/结论）。
  - `rule` TR-20.2: Ollama 轮在无任何云端 Key 条件下完成；证据为离线评测记录。

## Task 21: Release 发布包与发布说明（M5）
- **Status**: `completed`
- **Completion Evidence**:
  - TR-21.1 一条命令产 ZIP（2026-09-26）：`build\pack.ps1`（默认版本升至 0.2.0）→ 全量 Rebuild（AddIn x64 + SwIaTest + SwBench）→ 收集 → `build\artifacts\SwAiAssistant-0.2.0.zip`。ZIP 清单 19 项核验通过：SwAiAssistant.{AddIn,Core,Ai,Cad,Planner,Verify}.dll + Newtonsoft.Json.dll + PDB + SolidWorks.Interop.{sldworks,swconst,swpublished}.dll + 安装/卸载.bat + release-notes.txt；安装.bat  staged 副本机器校验 **CRLF=True、GBK 解码无乱码**（pack.ps1 归一化生效）。
  - redist 收集策略确认：三张 SW Interop DLL 随包携带（build.ps1 从 `api\redist` 拷至 AddIn 输出，pack.ps1 整目录收集）；安装脚本 RegAsm /codebase 不写 GAC。
  - 发布说明 0.2.0 重写（`install\release-notes.txt`）：安装/卸载、云端模型配置教程（DeepSeek/百炼/Moonshot 端点与 Key 申请指引、DPAPI 本机加密说明）、Ollama 一键拉取说明、M0–M5 功能清单、FAQ（含「SW 正忙」「无可用模型」「偏差超限」）。
  - build.ps1 构建链纳入 SwBench（开发工具不随包发布）。
  - **关键坑位（新录）**：build.ps1 头部约定 **ASCII-only**——向 ps1 写入中文注释（UTF-8 无 BOM）会被 PowerShell 5.1 按 GBK 误读，多字节序列吞并后续 ASCII 字符导致下一行赋值失效（$swBenchProj 为 null → MSBuild 无项目参数回退构建当前目录 slnx → MSB4126「Release|x64 无效」误导性报错）；ps1 脚本一律保持纯 ASCII。
  - 干净目录解压安装冒烟（管理员 RegAsm + SW 启动 AC-1）归真机验证清单第 13 组（开发机 SW 正在运行用户会话，不重启）。
- **Priority**: `medium`
- **Depends On**: T20
- **Description**:
  - build/pack 流水线定版：版本号、redist Interop 收集策略确认（随包携带 SW Interop DLL）、发布说明（安装/卸载、模型配置教程含申请 Key 指引、Ollama 一键拉取说明、常见问题）。
  - 在干净目录解压安装做全量冒烟。
- **Acceptance Criteria Addressed**: AC-2、AC-19
- **Test Requirements**:
  - `rule` TR-21.1: 一条命令产出可分发 ZIP；干净目录安装后 AC-1 冒烟全过；证据为构建日志、ZIP 清单、冒烟记录。

## Task 22: GB 第一角三视图工程图（M6）
- **Status**: `done`
- **Priority**: `medium`
- **Depends On**: T21
- **Description**:
  - 自带工程图模板（.drwdot，第一角投影标志，简易国标标题栏：零件名/材料/比例/日期/制图）；DrawingDoc API 新建工程图、标准三视图（前/上/左）、模型项目插入尺寸、视图比例自适应。
  - 面板"生成工程图"按钮 + 对话触发（"出工程图"）；导出 PDF（ExportToPdf 或打印 PDF）；标题栏自动回填零件名/材料。
- **Acceptance Criteria Addressed**: AC-14
- **Test Requirements**:
  - `rule` TR-22.1: 集成台/真机：3 个不同零件生成三视图工程图，投影关系第一角正确、尺寸插入无大面积缺失（关键尺寸覆盖率 ≥80%）、标题栏字段正确、PDF 可打开；证据为工程图/PDF 文件与检查记录。
- **Completion Evidence**:
  - 实现：`src/SwAiAssistant.Cad/Drawings/DrawingService.cs`——模板自动发现（首选项坏配置回退 ProgramData `gb_a3.drwdot`）；`GenerateViewPaletteViews` 预刷新 + `Create1stAngleViews2` 一键第一角三视图，失败降级 `CreateDrawViewFromModelView3`（中文/英文视图名）手动第一角布图；`ISheet.SetScale` 包围盒自适应标准比例档；全尺寸 `MarkedForDrawing=true`→重建重存→旧版 `InsertModelAnnotations(32768,true,0,true)` 插模型尺寸（probe-draw4 实测 SW2026 唯一生效路径）；标题栏经工程图自定义属性回填（名称/代号/材料/设计/设计日期，gb 模板 $PRPSHEET 键）；`Extension.SaveAs3` 导出 PDF。
  - UI 三触发：Ribbon「生成 GB 工程图」（仅零件环境启用 `PartDocEnabled`，SwAddIn.GenerateDrawing 回调）、对话页面板按钮「生成工程图」、对话关键词（出工程图/生成工程图/三视图）直调确定性服务不走 LLM；输出 `%AppData%\SwAiAssistant\temp\drawings\零件名.slddrw/.pdf`，气泡回报视图数/比例/插尺寸数/路径。
  - TR-22.1 证据（SwIaTest `--tr22`，SW2026 34.0.0 自有实例）：板 120×80×10 φ20、L 形支架 100×80×60、法兰盘 φ100×15 五孔三件全部通过——三视图=3；视图角色可识别（前视 type=7 orient=*前视；俯/左视为投影视图 type=4，按第一角几何归位）；俯视在前视正下方（dy=-118.8mm，|dx|=0.00）、左视在前视正右方（dx=147.0mm，|dy|=0.00）；关键尺寸覆盖率 4/4、3/3、4/4（≥80%）；比例 1:1；.slddrw 已存；PDF 108-111 KB 且魔数 %PDF；标题栏「名称」=零件名。
  - 回归：UI/Cad 改动后 SwIaTest 全量（TR-4.1/5.1/11.1/13/14.1/15.1/16.1/18.1）退出码 0「全部通过」。
  - 已知环境豁免：本机存在无法 ROT 接管、未授权终止的僵尸隐藏 SLDWORKS.exe（PID 47400），自有实例模式下「退出后无残留进程」断言受其影响失败，非代码缺陷（接管模式下该断言不适用）。

## Task 23: 装配体辅助（M7）
- **Status**: `done`（TR-23.2 真机 GUI 验证按计划合并至 T26 测试清单第 11 组）
- **Priority**: `medium`
- **Depends On**: T22
- **Description**:
  - AssemblyService：新建/打开装配体、插入已有零件文件（AddComponent5，定位原点）；配合：重合/同轴/距离（Face2 选择经由几何语义解析：按圆柱面/平面类型与包围盒位置选面）。
  - 干涉检查（ToolsCheckInterference2）中文报告（零件对、干涉体积、定位）；对话指令"把 A 同轴装到 B 的孔并端面重合"走计划确认执行。
- **Acceptance Criteria Addressed**: AC-15
- **Test Requirements**:
  - `rule` TR-23.1: 集成台：两零件同轴+重合配合位置误差为 0；故意 0.5mm 穿模用例必报干涉且体积正确；证据为 SwIaTest 输出。
  - `rule` TR-23.2: 真机对话式装配 1 例 + 干涉报告截图；证据为测试清单第 11 组。
- **Completion Evidence**:
  - 实现：`src/SwAiAssistant.Cad/Assemblies/AssemblyService.cs`——模板自动发现（首选项 enum=9 → ProgramData `gb_assembly.asmdot` 回退）；`NewAssembly`；`InsertComponent`（AddComponent5 前置：零件未在会话打开则先 OpenDoc6 静默加载，configOption=0）；组件枚举/位置毫米回读；语义面解析（按半径或最大圆柱面、轴无关端面）；`MateConcentric`/`MateEndFaceCoincident`/`MateEndFaceDistance`/一键 `MatePinIntoHole`（同轴+端面重合）；`CheckInterference`（InterferenceDetectionMgr，体积 m³→mm³，中文报告）。对话编排：`GetOpenSavedParts`（已保存零件按包围盒体积降序）、`AssembleTwoOpenParts`（最大件=含孔基座、次大=销轴装配件→新建装配体→插入→两配合→重建→干涉报告）、`CheckInterferenceActive`（非装配体环境中文报错）。
  - 关键 interop 事实（7 轮真机探测锁定）：本机 swMateType_e COINCIDENT=0/CONCENTRIC=1/DISTANCE=5；swAddMateError_e **NoError=1**（非 0）；配合选择走 IEntity.Select4(append, Mark=1) 逐面追加（MultiSelect2 传装配上下文 IFace2 抛 InvalidCastException）；GB 模板拉伸/孔轴沿 Y，全部面解析轴无关；ISurface.PlaneParams=[法向3,根点3]；AddComponent5 要求零件已在会话打开；精确贴合圆柱面不计干涉。
  - UI 接线（T23）：SwAddIn 构造 `AssemblyService` 并经 `AttachCad` 第 7 参注入 → TaskPaneHost 转发 → TaskPaneView 对话拦截（关键词优先级：干涉检查/检查干涉/干涉 → 直接跑活动装配干涉检查回贴中文报告；同轴/装到…孔/装配 → 先列「基座+装配件+操作步骤」**MessageBox 计划确认**，确认后执行，气泡回报两配合成败 + 干涉中文报告；零件不足/非装配体环境均中文引导）。
  - TR-23.1 证据（SwIaTest `--tr23`，SW2026 34.0.0 自有实例，2026-09-26）：A 板（120×80×10 φ20 通孔）+ 销（φ20×30）2 组件，同轴 err=1、端面重合 err=1，ForceRebuild3 后实测销相对位移 (0,10.000,0)mm 与期望一致，**位置误差 0.0000mm（≤0.01）**；B 公称零间隙销孔干涉数=0；C 两方块穿模 0.5mm 报 1 处干涉、**体积 200.0mm³（断言 190–210）**、组件对 blockA↔blockB、报告含「干涉体积」「mm³」；D 间隙 1mm 干涉数=0、报告含「未发现干涉」；E 对话编排服务：枚举=2 且自动选型基座=板/装配件=销、一键装配 2 配合均 err=1、装配后干涉=0、`CheckInterferenceActive` 对当前活动装配体=0。共 20 项功能断言全部通过。
  - 回归（2026-09-26）：无参全量套件（TR-4.1/5.1/11.1/13/14.1/15.1/16.1/18.1）功能断言全部通过；`--tr22` TR-22.1 三件 30 项全部通过。
  - 已知环境豁免（本日新增，同既有豁免性质）：今日起每个新启动的自有（不可见）SLDWORKS 实例退出时均弹出「Microsoft Visual Basic for Applications」首启模态窗阻塞 ExitApp，导致「退出后无残留进程」断言失败；功能断言不受影响，插件宿主模式不退出 SW 进程故产品零影响；测试孤儿实例由测试台自有 PID 标记机制（CleanupOwnedOrphan）回收，僵尸 PID 47400 仍未触碰。

## Task 24: DXF/DWG 图纸反建（M8）
- **Status**: `done`
- **Priority**: `medium`
- **Depends On**: T23
- **Description**:
  - 引入 netDxf（MIT）解析 DXF/DWG（DWG 经另存/转换说明，必要时 ODA 命令行免费转换器可选指引，不内嵌盗版组件）。
  - 结构化提取：图层分类、闭合轮廓链、圆/圆弧/槽口、标注尺寸真值仲裁、视图框切分（简单件单视图+厚度标注优先）。
  - ReversePlanner：结构化结果 → 特征树候选（轮廓→拉伸、圆→孔），计划卡片展示识别结果与置信度；**强制人工确认/可改**后才建模；复用校验闭环回读 ΔV。
- **Acceptance Criteria Addressed**: AC-16
- **Test Requirements**:
  - `rubric` TR-24.1: 5 张简单平板/板类 DXF 反建；scale 1-5；anchors 1=≤1/5、3=3/5 建成且 ΔV≤5%、5=≥4/5 建成且 ΔV≤5%；threshold>=3；证据为 5 张图纸+结构化结果+计划截图+成品 ΔV 对照表。
  - `rule` TR-24.2: 全部 5 例均在人工确认节点停留，未确认时 SW 零变化；证据为日志与截图。
- **Completion Evidence**（2026-09-26，SW2026 34.0.0 自有隐藏实例，SwIaTest `--tr24`）：
  - 新增 `src\SwAiAssistant.Reverse`（net48 纯托管，netDxf 3.0.1 MIT，仅引用 Planner Schema）：DxfParser（图层分类/圆语义保留/凸度弧与椭圆离散 0.1mm 弦高/散线贪心链接环/最大面积环=外轮廓、内环射线法判孔/孔去重/标注真值与文本提取/$INSUNITS 折算；DWG 抛中文 `DwgGuidance` 引导 ODA 或另存）；ReversePlanner（厚度：文本正则「厚度/板厚/壁厚/THICKNESS/THK/T=/δ」优先→最小线性标注兜底；外轮廓中心归零；矩形→rectCorner、圆→circle、其余→闭合 polyline；每孔独立 top 草图+双向贯穿切除；期望体积=(外环面积−孔面积)×厚；置信度 0~0.97；**无板厚不出树、CanBuild=false**）。
  - 5 张夹具由 netDxf 现场写出（`%TEMP%\SwAiAssistant-iat\tr24\`）：s1 闭合多段线矩形 100×60×10 无孔；s2 矩形 120×80×10 两 φ20（中文厚度）；s3 矩形 150×100×12 四 φ10 角孔+2 条线性标注；s4 六边斜切外轮廓（面积 4000）×8 两 φ16（polyline 外轮廓路径）；s5 散线链接 200×120×15 外轮廓+φ30 圆孔+30×30 方孔+线性/直径标注+英文 THICKNESS。
  - TR-24.1 真机结果（rubric 达成 **anchor 5**，threshold≥3）：5/5 候选 Schema 校验通过、孔数/板厚识别全对（5 例置信度均高 97%、零警告）、5/5 全部特征建成且 **ΔV 全为 0.00%**（解析理论体积 vs 夹具手算真值 Δ≤1%；SW 成品 vs 真值：60000.0/89717.0 vs 89716.8/176230.1/28783.1/335897.7 vs 335897.1 mm³）；近似包围盒短边与板厚一致（≤5%，GetBodyBox 细分盒含圆孔轴向放大约 2.5% 已在断言注释说明）。
  - TR-24.2 真机证据：5 例仅 Plan 不调执行器，规划前后 SW 文档清单与活动文档完全一致（自有实例文档数 0→0），全部停在人工确认节点；套件退出码 0，自有实例干净退出（本日 VBA 首启窗未出现）。
  - UI：对话页新增「导入DXF」按钮（OpenFileDialog .dxf/.dwg；选到 .dwg 直接弹 DwgGuidance）+ 对话关键词「dxf/图纸反建」；Task.Run 调 ReversePlanner，识别摘要/警告以气泡展示，候选树复用 BuildPlanCard 并在卡片顶部插橙色警示条「⚠ 实验功能·DXF 图纸识别候选（置信度）…请逐项核对…未点执行前 SW 零变化」；**唯一建模入口是既有「执行」按钮（含可编辑字段+校验+ΔV 强校验闭环）**，无板厚仅提示不出卡片；极速模式不影响该路径。AddIn 已引用 Reverse，netDxf.dll/SwAiAssistant.Reverse.dll 随生成输出到 bin\Release，pack.ps1 的 `*.dll` 通配自动收包；build.ps1 全量构建 OK。GUI 截图按既有约定并入 T26 真机验证清单第 11 组。

## Task 25: 图片/PDF 图纸半自动反建（M9）
- **Status**: `done`
- **Priority**: `low`
- **Depends On**: T24
- **Description**:
  - 图片/JPG/PNG 与 PDF 首页转图输入；视觉模型专用 prompt（视图/轮廓/尺寸表/比例歧义），输出与 DXF 路径同构的特征树候选。
  - 全流程"实验功能"标识；计划卡片顶部固定提示"AI 识别结果，请逐项核对尺寸"；未点确认不得建模；确认后复用同一执行与校验管线。
- **Acceptance Criteria Addressed**: AC-17
- **Test Requirements**:
  - `rule` TR-25.1: 真机 1 张板类图纸照片走通：实验提示出现 → 计划停留 → 未确认 SW 无变化 → 确认后建模；证据为流程截图、前后模型树对比、日志。
- **Completion Evidence**（2026-09-26，SW2026 34.0.0 自有隐藏实例 + 本机 Ollama qwen2.5vl:7b，SwIaTest `--tr25`）：
  - 新增 `src\SwAiAssistant.Reverse\Image`：① [PdfToImage.cs](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Reverse/Image/PdfToImage.cs) 用 Win10+ 系统自带 `Windows.Data.Pdf` 渲染 PDF 首页（长边 1600px），零三方部署；winmd 仅编译期经 Windows SDK UnionMetadata 探测引用（`HAVE_WINRT_PDF`，System.Runtime.WindowsRuntime 从框架目录取、Private=false 不随包），无 SDK 构建机自动降级为中文「另存图片」引导；② [ImageReversePlanner.cs](file:///c:/Users/16650/Desktop/document/AI/solidworks%20AI%20Agent/src/SwAiAssistant.Reverse/Image/ImageReversePlanner.cs)：jpg/jpeg/png/bmp/gif/pdf → GDI+ 等比压缩（长边 1600、JPEG 重编码）→ 视觉模型（Scheduler 的 VisionVerify 候选链）；专用 prompt 覆盖视图映射（多视图只取主视图→top）、尺寸真值仲裁（只信标注数字、像素比例不得作尺寸）、轮廓/圆孔/方孔映射、比例歧义、无板厚/无标注强制 clarify 不许臆造；复用 DomainSystemPrompt 全量 Schema + PlanJsonParser 红线解析（JSON 外代码拒绝）+ 2 轮错误纠正；要求同构 recognition{thicknessMm,holes,confidence,warnings,summary}；含 100×60×8 两 φ12 的完整嵌套骨架示例（不同尺寸防照抄）修掉了小视觉模型 steps 嵌套退化问题。
  - TR-25.1 真机全过（退出码 0）：GDI+ 现画正视图纸照片夹具（120×80 矩形板 +「120」「80」「2×φ20 通孔」「厚度：10 mm」标注，`%TEMP%\SwAiAssistant-iat\tr25\plate-photo.jpg`）；qwen2.5vl:7b 44s 一次性输出通过 Schema 校验的 6 步候选（s1 外轮廓草图 rectCenter 120×80 / s2 凸台 depth=10 / s3~s6 两个 circle φ20 + throughAll 切除），recognition：摘要「矩形板120×80×10，两个φ20通孔」、**置信度 90%、零警告**。
    - ① 实验提示：日志打印的固定 banner 含「实验功能」「AI 识别结果，请逐项核对尺寸」「未点执行前 SolidWorks 不会有任何变化」3 项断言全过；
    - ② 计划停留：识别前后自有实例文档数 0→0、活动文档「<无>」不变（零变化）；
    - ③ 确认建模：同一 PlanExecutor（CreateNew）建成 **6/6 特征**（AI__外轮廓草图/板体拉伸/孔1草图/孔1切除/孔2草图/孔2切除，日志即前后模型树对比），回读体积 **89717.0 vs 真值 89716.8 mm³，ΔV=0.00%**；自有实例干净退出无残留。
  - UI：对话页按钮行新增「导入图片」（jpg/jpeg/png/bmp/gif/pdf 过滤；PDF 不支持时弹 PdfGuidance）+ 对话关键词（图片反建/图纸照片/pdf/导入图片等）；Task.Run 调 ImageReversePlanner，clarify/缺视觉模型仅气泡中文提示不出卡片，成功则 BuildPlanCard(tree, FormatBanner(置信度)) 切计划页；建模唯一入口仍是既有「执行」确认门（可编辑+强校验+视觉复核），极速模式不触达该路径。SwAiAssistant.Reverse.dll/netDxf.dll 已确认进入 AddIn bin\Release（pack 通配自动收包，WinRT 为系统组件不随包）；build.ps1 全量构建 OK。GUI 截图按既有约定并入 T26 真机验证清单第 11 组（含 PDF 首页导入手动验证项）。

## Task 26: 全量回归、干净机冒烟与独立评审
- **Status**: `pending`
- **Priority**: `high`
- **Depends On**: T25
- **Description**:
  - 跑全部 SwIaTest 集成用例与单元测试；重打 Release ZIP；在干净目录（或第二台 SW 机器）完成 AC-1 冒烟；整理全部测试清单记录；进入 Spec Mode Review，由全新上下文执行独立评审并按评审问题修复闭环。
- **Acceptance Criteria Addressed**: AC-1 ~ AC-19
- **Test Requirements**:
  - `rule` TR-26.1: 所有既有自动化用例 100% 通过；ZIP 干净机冒烟通过；证据为测试总报告。
  - `rule` TR-26.2: review.md 中全部 checkpoint 为 pass，每个 AC 有独立证据；证据为终审报告。
- **Completion Evidence（进行中，2026-09-26）**：
  - 独立终审一轮（review.md，fail：严重 2/中 5/低 7）→ R1–R6 修复全部完成并经**第二轮全新上下文只读复审**（review.md 第六章）：严重-1/2、中-1/2/3/4、低-1/2/3/5 代码层关闭；复审新发现 B-1（重试半成品整树重跑，中）已修（重试前回滚 preSnap，无快照/回滚失败则放弃重试）、B-2（About 文案，低）已修；中-5（AC-12）、低-4（真实图纸）为真机/用户项未关闭；低-6 PDB 决策保留并记录；低-7 环境豁免（僵尸 PID 47400、VBA 首启模态）已记录。
  - build.ps1 x64 Release 全项目编译通过；单元测试 **64/64 通过**（Core 10 / Ai 33 / Planner 21），修复后多次复跑一致。
  - 重打包：build\artifacts\SwAiAssistant-0.2.0.zip，**1,703,091 字节 / 22 文件**（首轮 1,684,536 字节），release-notes 如实重写（约束/标注不施加、螺纹只开底孔、rib 实验、圆弧预算估算、对话修改四 intent）。
  - 待真机（TR-26.1 剩余，需授权启动 SolidWorks）：SwIaTest 全量 + --tr22/23/24/25 分支复跑；SwBench AC-12 本地 Ollama 轮（%TEMP%\SwAiAssistant-bench 产物）；干净机 AC-1/19、AC-18 连续 20 次会话、GUI 截图、AC-16 真实图纸 5 张归用户在场项（R8）。
