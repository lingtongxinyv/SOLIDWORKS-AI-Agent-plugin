using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SwAiAssistant.Ai;
using SwAiAssistant.Ai.Scheduling;
using SwAiAssistant.Cad;
using SwAiAssistant.Cad.Assemblies;
using SwAiAssistant.Cad.Documents;
using SwAiAssistant.Cad.Drawings;
using SwAiAssistant.Cad.Features;
using SwAiAssistant.Cad.Materials;
using SwAiAssistant.Cad.Queries;
using SwAiAssistant.Cad.Sketching;
using SwAiAssistant.Core.Configuration;
using SwAiAssistant.Core.Logging;
using SwAiAssistant.Planner;
using SwAiAssistant.Planner.Execution;
using SwAiAssistant.Planner.Prompts;
using SwAiAssistant.Planner.Schema;
using SwAiAssistant.Reverse.Dxf;
using SwAiAssistant.Reverse.Drawing;
using SwAiAssistant.Reverse.Image;

namespace SwAiAssistant.AddIn.UI
{
    /// <summary>
    /// 任务面板根视图：对话 / 计划 / 材料 / 快照 / 设置 / 日志。
    /// M2：对话流（流式气泡）、计划卡片（可编辑数值）、设置页（模型 CRUD/连通/探测）、
    /// Ollama 区（状态/修复/拉取）全部真实可用。
    /// </summary>
    public partial class TaskPaneView : UserControl
    {
        // Cad 服务组
        private DocService _docs;
        private SketchService _sketch;
        private FeatureService _features;
        private QueryService _query;
        private SwAiAssistant.Cad.Capture.CaptureService _capture;
        private DrawingService _drawings;
        private bool _drawingBusy;
        private AssemblyService _assemblies;
        private bool _assemblyBusy;
        // 材料（T15/T26 接线）：GB 材料库应用 + 当前材料/质量回读
        private SwAiAssistant.Cad.Materials.MaterialService _materials;
        private bool _materialBusy;

        // AI 服务组
        private ConfigService _config;
        private CapabilityProbe _probe;
        private Scheduler _scheduler;
        private PlannerService _planner;
        private PlanExecutor _executor;
        private OllamaServiceManager _ollamaMgr;
        // 对话修改与真实问答（T14/T26 接线）
        private EditPlanner _editPlanner;
        private QAService _qa;
        private DimensionService _dims;

        private CancellationTokenSource _chatCts;
        private CancellationTokenSource _pullCts;
        // 执行取消（M4-T19）：执行期间非空，CancelPlanButton 此时为「取消执行」
        private CancellationTokenSource _execCts;

        // 快照与一键回滚（M4-T16）
        private SnapshotService _snapshots;
        private readonly List<SnapshotData> _snapshotItems = new List<SnapshotData>();

        private FeatureTree _currentPlan;
        private readonly List<PlanRow> _planRows = new List<PlanRow>();

        public TaskPaneView()
        {
            InitializeComponent();
        }

        /// <summary>Ribbon「AI 设置」按钮入口：显示面板并切到设置页。</summary>
        public void ShowSettings()
        {
            RootTabs.SelectedItem = SettingsTab;
            RefreshModelList();
        }

        /// <summary>由 SwAddIn 在会话建立后注入 Cad 服务组。</summary>
        public void AttachCad(DocService docs, SketchService sketch, FeatureService features,
            QueryService query, SwAiAssistant.Cad.Capture.CaptureService capture = null,
            DrawingService drawings = null,
            AssemblyService assemblies = null,
            SwAiAssistant.Cad.Materials.MaterialService materials = null)
        {
            _docs = docs;
            _sketch = sketch;
            _features = features;
            _query = query;
            _capture = capture;
            _drawings = drawings;
            _assemblies = assemblies;
            _materials = materials;
            PopulateMaterials();
        }

        /// <summary>由 SwAddIn 注入 AI 服务组（配置/探测/调度/规划/执行/Ollama 管理）。</summary>
        public void AttachAi(ConfigService config, CapabilityProbe probe, Scheduler scheduler,
            PlannerService planner, PlanExecutor executor, OllamaServiceManager ollamaMgr,
            EditPlanner editPlanner = null, QAService qa = null, DimensionService dimensions = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            _planner = planner ?? throw new ArgumentNullException(nameof(planner));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _ollamaMgr = ollamaMgr ?? throw new ArgumentNullException(nameof(ollamaMgr));
            _editPlanner = editPlanner;
            _qa = qa;
            _dims = dimensions;

            _executor.Progress += OnPlanProgress;

            TimeoutBox.Text = _config.Current.RequestTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            ThresholdBox.Text = _config.Current.VerifyThresholdPct.ToString(CultureInfo.InvariantCulture);
            FastModeBox.IsChecked = _config.Current.FastMode;

            RefreshModelList();
            _ = RefreshOllamaStatusAsync();
            Log.Info("UI", "AI 服务组已注入任务面板");
        }

        /// <summary>由 SwAddIn 注入快照服务（M4-T16 一键回滚）。</summary>
        public void AttachSnapshots(SnapshotService snapshots)
        {
            _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        }

        // ==================== 材料页（T15 GB 材料库） ====================

        /// <summary>填充 GB 材料下拉（16 种，显示「名称（密度）」）。</summary>
        private void PopulateMaterials()
        {
            if (MaterialCombo == null || MaterialService.GbMaterials.Count == 0) return;
            MaterialCombo.Items.Clear();
            foreach (var m in MaterialService.GbMaterials)
            {
                MaterialCombo.Items.Add(new MaterialComboItem { Def = m });
            }
            // 默认选中 6061 铝合金之外最常用的 Q235
            int q235 = MaterialCombo.Items.OfType<MaterialComboItem>()
                .TakeWhile(x => x.Def.Name != "Q235").Count();
            if (q235 < MaterialCombo.Items.Count) MaterialCombo.SelectedIndex = q235;
        }

        private sealed class MaterialComboItem
        {
            public MaterialDef Def { get; set; }
            public override string ToString() => Def.Name + "（密度 " + Def.DensityKgM3.ToString("0", CultureInfo.InvariantCulture) + " kg/m³）";
        }

        private async void ApplyMaterialClick(object sender, RoutedEventArgs e)
        {
            if (_materials == null || _docs == null)
            {
                MaterialCurrentText.Text = "材料服务尚未就绪，请重启 SolidWorks 后重试。";
                return;
            }
            var item = MaterialCombo?.SelectedItem as MaterialComboItem;
            if (item == null)
            {
                MaterialCurrentText.Text = "请先在下拉中选择一种材料。";
                return;
            }
            if (_materialBusy) return;
            SolidWorks.Interop.sldworks.IModelDoc2 doc = null;
            try { doc = _docs.GetActivePart(); }
            catch (Exception ex) { Log.Warn("UI", "读取活动零件失败：" + ex.Message); }
            if (doc == null)
            {
                MaterialCurrentText.Text = "请先打开（或新建）一个零件，再应用材料。";
                return;
            }

            _materialBusy = true;
            ApplyMaterialButton.IsEnabled = false;
            MaterialCurrentText.Text = "正在赋材料「" + item.Def.Name + "」…";
            try
            {
                var mat = item.Def;
                await Task.Run(() => _materials.ApplyMaterial(doc, mat)).ConfigureAwait(true);
                Log.Info("UI", "材料页手动赋材料：" + mat.Name);
                await RefreshMaterialReadoutAsync(doc).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Error("UI", "应用材料失败", ex);
                MaterialCurrentText.Text = "应用材料失败：" + ex.Message;
            }
            finally
            {
                _materialBusy = false;
                ApplyMaterialButton.IsEnabled = true;
            }
        }

        private async void RefreshMaterialClick(object sender, RoutedEventArgs e)
        {
            if (_materials == null || _docs == null) return;
            SolidWorks.Interop.sldworks.IModelDoc2 doc = null;
            try { doc = _docs.GetActivePart(); }
            catch (Exception ex) { Log.Warn("UI", "读取活动零件失败：" + ex.Message); }
            if (doc == null)
            {
                MaterialCurrentText.Text = "当前没有打开的零件。";
                return;
            }
            await RefreshMaterialReadoutAsync(doc).ConfigureAwait(true);
        }

        /// <summary>回读当前材料名与真实质量/体积并显示（质量按生效密度）。</summary>
        private async Task RefreshMaterialReadoutAsync(SolidWorks.Interop.sldworks.IModelDoc2 doc)
        {
            try
            {
                var result = await Task.Run(() =>
                {
                    string name = _materials.GetCurrentMaterialName(doc);
                    var mp = _query.GetMassProps(doc);
                    return new { Name = name, mp.MassKg, mp.VolumeMm3 };
                }).ConfigureAwait(true);
                MaterialCurrentText.Text = "当前材料：" + (string.IsNullOrWhiteSpace(result.Name) ? "未指定（SW 默认密度）" : result.Name)
                    + "\n质量：" + result.MassKg.ToString("F4", CultureInfo.InvariantCulture) + " kg"
                    + "；体积：" + result.VolumeMm3.ToString("F1", CultureInfo.InvariantCulture) + " mm³";
            }
            catch (Exception ex)
            {
                Log.Warn("UI", "材料/质量回读失败：" + ex.Message);
                MaterialCurrentText.Text = "回读失败：" + ex.Message;
            }
        }

        // ==================== 对话页 ====================

        private async void SendClick(object sender, RoutedEventArgs e) => await SendChatAsync();

        private async void ChatInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                await SendChatAsync();
            }
        }

        private void CancelChatClick(object sender, RoutedEventArgs e)
        {
            _chatCts?.Cancel();
        }

        private void ClearChatClick(object sender, RoutedEventArgs e)
        {
            ChatPanel.Children.Clear();
            _planner?.ClearHistory();
            AddAiBubble("对话已清空。描述一个零件开始建模，或直接提问。");
        }

        private void FastModeChanged(object sender, RoutedEventArgs e)
        {
            if (_config == null) return;
            bool fast = FastModeBox.IsChecked == true;
            _config.Mutate(c => c.FastMode = fast);
            Log.Info("UI", "极速模式：" + (fast ? "开" : "关"));
        }

        private async Task SendChatAsync()
        {
            string text = (ChatInput.Text ?? "").Trim();
            if (text.Length == 0) return;

            ChatInput.Text = "";
            AddUserBubble(text);

            // T22：工程图关键词直调确定性工程图服务，不走 LLM
            if (IsDrawingRequest(text))
            {
                await GenerateDrawingAsync().ConfigureAwait(true);
                return;
            }

            // T23：装配体辅助关键词（同轴装配 / 干涉检查）直调确定性服务，不走 LLM
            if (IsInterferenceRequest(text))
            {
                await CheckInterferenceAsync().ConfigureAwait(true);
                return;
            }
            if (IsAssemblyRequest(text))
            {
                await AssembleCurrentPartsAsync().ConfigureAwait(true);
                return;
            }

            // T24：DXF 图纸反建（实验功能）：选文件 → 识别候选计划卡片，未点「执行」绝不建模
            if (IsDxfImportRequest(text))
            {
                await ImportDxfAsync().ConfigureAwait(true);
                return;
            }

            // T25：图片/PDF 图纸反建（实验功能，视觉模型）：同样只出候选，人工确认后才建模
            if (IsImageImportRequest(text))
            {
                await ImportImageAsync().ConfigureAwait(true);
                return;
            }

            // 按当前 SolidWorks 工程图反建零件：确定性提取 → 候选计划，人工确认后才建模
            if (IsDrawingReverseRequest(text))
            {
                await ReverseFromDrawingAsync().ConfigureAwait(true);
                return;
            }

            // T14：数据问答（多重/体积/包围盒/孔数等）确定性真实回读，不走 LLM、不臆答
            if (IsDataQuestion(text))
            {
                await AnswerDataQuestionAsync(text).ConfigureAwait(true);
                return;
            }

            if (_planner == null)
            {
                AddAiBubble("AI 服务尚未就绪，请稍候或重启 SolidWorks。");
                return;
            }

            _chatCts = new CancellationTokenSource();
            SetChatBusy(true);
            var bubble = AddAiBubble("思考中…");
            var streamed = new StringBuilder();
            try
            {
                string docContext = BuildDocContextSafe();
                var profiles = await GetProfilesAsync(_chatCts.Token).ConfigureAwait(true);
                var resp = await _planner.RequestAsync(text, docContext, profiles,
                    delta => PostToUi(() =>
                    {
                        streamed.Append(delta);
                        bubble.Text = streamed.ToString();
                        ScrollChat();
                    }), _chatCts.Token).ConfigureAwait(true);

                if (resp.Type == PlannerResponseType.Plan)
                {
                    bubble.Text = $"已生成 {resp.Plan.Steps.Count} 步建模计划（{resp.Plan.Part?.Name ?? "AI零件"}），" +
                        "请在「计划」页核对数值后执行。";
                    BuildPlanCard(resp.Plan);
                    RootTabs.SelectedItem = PlanTab;
                    Log.Info("UI", $"计划生成：{resp.Plan.Steps.Count} 步，零件「{resp.Plan.Part?.Name}」");
                    if (_config.Current.FastMode)
                    {
                        PlanHint.Text = "极速模式：计划不弹确认，直接执行。";
                        await ExecuteCurrentPlanAsync().ConfigureAwait(true);
                    }
                }
                else if (resp.Type == PlannerResponseType.Edit)
                {
                    await ApplyEditResponseAsync(resp, bubble).ConfigureAwait(true);
                }
                else
                {
                    bubble.Text = resp.Text;
                }
            }
            catch (OperationCanceledException)
            {
                bubble.Text = "已取消。";
            }
            catch (LlmException ex)
            {
                Log.Warn("UI", "模型调用失败：" + ex.Message);
                bubble.Text = "模型调用失败：" + ex.Message;
            }
            catch (Exception ex)
            {
                Log.Error("UI", "对话请求异常", ex);
                bubble.Text = SwAiAssistant.Core.Diagnostics.ErrorText.Friendly("对话请求", ex);
            }
            finally
            {
                SetChatBusy(false);
                _chatCts?.Dispose();
                _chatCts = null;
                ScrollChat();
            }
        }

        /// <summary>已启用模型的能力画像字典（缓存优先，缺失则在线探测）。</summary>
        private async Task<Dictionary<string, ModelProfile>> GetProfilesAsync(CancellationToken ct)
        {
            var dict = new Dictionary<string, ModelProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _config.EnabledModels())
            {
                var p = _probe.GetCached(entry.Id, entry.Model);
                if (p == null)
                {
                    try { p = await _probe.ProbeAsync(entry, ct).ConfigureAwait(true); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Log.Warn("UI", $"探测「{entry.Name}」失败：{ex.Message}");
                        p = null;
                    }
                }
                if (p != null) dict[entry.Id] = p;
            }
            return dict;
        }

        private string BuildDocContextSafe()
        {
            try
            {
                var state = _docs.GetState();
                string features = null;
                string material = null;
                if (state.HasActiveDocument && state.ActiveIsPart)
                {
                    var doc = _docs.GetActivePart();
                    if (doc != null)
                    {
                        var names = _query.GetFeatureNames(doc);
                        if (names.Length > 0)
                        {
                            features = string.Join("、", names.Take(30));
                        }
                        try
                        {
                            if (_materials != null) material = _materials.GetCurrentMaterialName(doc);
                        }
                        catch (Exception ex) { Log.Warn("UI", "读取当前材料失败：" + ex.Message); }

                        // T14：附上「尺寸全名=当前值」清单，供 edit 响应逐字取 dimensionFullName
                        if (_dims != null)
                        {
                            try
                            {
                                var dims = _dims.ListDimensions(doc);
                                if (dims.Count > 0)
                                {
                                    string dimText = string.Join("；",
                                        dims.Take(40)
                                            .Select(d => d.FullName + "=" + d.ValueMm.ToString("0.###", CultureInfo.InvariantCulture)));
                                    string ctx = DomainSystemPrompt.BuildDocContext(
                                        state.ActiveDocTitle, features, material);
                                    return ctx + "。尺寸清单（尺寸全名=当前值 mm，edit 时逐字引用全名）：" + dimText;
                                }
                            }
                            catch (Exception ex) { Log.Warn("UI", "枚举尺寸失败：" + ex.Message); }
                        }
                    }
                }
                return DomainSystemPrompt.BuildDocContext(
                    state.HasActiveDocument ? state.ActiveDocTitle : null, features, material);
            }
            catch (Exception ex)
            {
                Log.Warn("UI", "组装文档上下文失败：" + ex.Message);
                return null;
            }
        }

        private void SetChatBusy(bool busy)
        {
            SendButton.IsEnabled = !busy;
            ChatInput.IsEnabled = !busy;
            CancelChatButton.IsEnabled = busy;
            StatusText.Text = busy ? "AI 处理中…" : "就绪";
        }

        // ==================== 对话修改与数据问答（T14） ====================

        /// <summary>
        /// 数据问答判定：问质量/体积/包围盒/孔数/特征清单等查询类语句。
        /// 含修改动词（改/加/删/加厚等）的句子不算问答，交给 LLM 走 edit 路径。
        /// </summary>
        private static bool IsDataQuestion(string text)
        {
            string[] editVerbs =
                { "改成", "改为", "加到", "加厚", "减薄", "变成", "删除", "删掉", "去掉",
                  "追加", "加一个", "新增", "沉孔", "倒个", "开个", "钻个" };
            if (editVerbs.Any(v => text.Contains(v))) return false;

            string[] askWords =
                { "多重", "质量", "重量", "体积", "包围盒", "外形尺寸", "多大",
                  "几个孔", "多少个孔", "孔数", "孔径", "特征清单", "哪些特征", "什么特征" };
            return askWords.Any(w => text.Contains(w));
        }

        /// <summary>确定性数据问答：QAService 真实回读活动零件后用中文回答（不经 LLM）。</summary>
        private async Task AnswerDataQuestionAsync(string question)
        {
            if (_qa == null || _docs == null)
            {
                AddAiBubble("数据问答服务尚未就绪，请重启 SolidWorks 后重试。");
                return;
            }
            SetChatBusy(true);
            try
            {
                var state = _docs.GetState();
                if (!state.HasActiveDocument || !state.ActiveIsPart)
                {
                    AddAiBubble("当前没有打开的零件，请先建模或打开一个零件后再查询数据。");
                    return;
                }
                var doc = _docs.GetActivePart();
                if (doc == null)
                {
                    AddAiBubble("无法获取活动零件。");
                    return;
                }
                var snap = await Task.Run(() => _qa.Capture(doc)).ConfigureAwait(true);
                AddAiBubble(_qa.AnswerQuestion(snap, question));
            }
            catch (Exception ex)
            {
                Log.Error("UI", "数据问答失败", ex);
                AddAiBubble("读取零件数据失败：" + SwAiAssistant.Core.Diagnostics.ErrorText.Friendly("数据问答", ex));
            }
            finally { SetChatBusy(false); }
        }

        /// <summary>
        /// 执行 LLM 给出的结构化修改 edit：执行前自动快照（可一键回滚），逐条应用，
        /// 完成后回读质量/体积/包围盒在气泡汇报。修改不走「计划确认」页——
        /// 其安全网是执行前快照 + 仅允许删 AI__ 特征的红线。
        /// </summary>
        private async Task ApplyEditResponseAsync(PlannerResponse resp, TextBlock bubble)
        {
            if (_editPlanner == null || _docs == null)
            {
                bubble.Text = "对话修改服务尚未就绪，请重启 SolidWorks 后重试。";
                return;
            }
            var state = _docs.GetState();
            if (!state.HasActiveDocument || !state.ActiveIsPart)
            {
                bubble.Text = "对话修改只针对当前活动零件：请先打开（或新建并建成）一个零件再发修改指令。";
                return;
            }
            var doc = _docs.GetActivePart();
            if (doc == null)
            {
                bubble.Text = "无法获取活动零件。";
                return;
            }

            // 执行前快照（安全网）
            if (_snapshots != null)
            {
                try
                {
                    var pre = await Task.Run(() =>
                        _snapshots.Capture(doc, "对话修改前：" + (resp.EditSummary ?? ""))).ConfigureAwait(true);
                    Log.Info("UI", "对话修改前快照：" + pre?.Id);
                }
                catch (Exception ex) { Log.Warn("UI", "对话修改前快照失败（不阻断）：" + ex.Message); }
            }

            var notes = new List<string>();
            try
            {
                for (int i = 0; i < resp.Edits.Count; i++)
                {
                    var req = resp.Edits[i];
                    bubble.Text = $"正在修改（{i + 1}/{resp.Edits.Count}）：{EditIntentText(req)}…";
                    var r = await Task.Run(() => _editPlanner.Apply(doc, req, _chatCts.Token))
                        .ConfigureAwait(true);
                    if (r?.Notes != null) notes.AddRange(r.Notes);
                }

                string tail = "";
                if (_qa != null)
                {
                    try
                    {
                        var snap = await Task.Run(() => _qa.Capture(doc)).ConfigureAwait(true);
                        tail = $"\n回读：质量 {snap.MassKg:F4} kg，体积 {snap.VolumeMm3:F1} mm³，"
                            + $"包围盒 {snap.BoxX:F1}×{snap.BoxY:F1}×{snap.BoxZ:F1} mm，孔 {snap.HoleCount} 个。";
                    }
                    catch (Exception ex) { Log.Warn("UI", "修改后回读失败：" + ex.Message); }
                }
                bubble.Text = "修改已完成：" + (string.IsNullOrWhiteSpace(resp.EditSummary) ? "" : resp.EditSummary)
                    + "\n" + string.Join("\n", notes) + tail
                    + "\n如需撤销，可在「快照」页一键回滚到修改前状态。";
                Log.Info("UI", $"对话修改完成：{resp.Edits.Count} 条");
                RefreshSnapshotList();
            }
            catch (OperationCanceledException)
            {
                bubble.Text = "修改已取消。部分修改可能已生效，可用「快照」页回滚。";
            }
            catch (CadException ex)
            {
                Log.Error("UI", "对话修改失败", ex);
                bubble.Text = "修改失败：" + ex.Message + "\n已成功的步骤保留在零件上，可用「快照」页回滚到修改前状态。";
            }
            catch (Exception ex)
            {
                Log.Error("UI", "对话修改异常", ex);
                bubble.Text = "修改失败：" + SwAiAssistant.Core.Diagnostics.ErrorText.Friendly("对话修改", ex)
                    + "\n可用「快照」页回滚到修改前状态。";
            }
        }

        private static string EditIntentText(EditRequest req)
        {
            switch (req.Intent)
            {
                case EditIntent.ChangeDimension:
                case EditIntent.ChangeFeatureParam:
                    return $"尺寸「{req.DimensionFullName}」→ {req.NewValueMm} mm";
                case EditIntent.AddFeature:
                    return "追加特征（" + (req.NewFeatureTree?.Steps.Count.ToString() ?? "?") + " 步）";
                case EditIntent.DeleteFeature:
                    return "删除 AI 特征「" + req.TargetFeatureName + "」";
                default:
                    return req.Intent.ToString();
            }
        }

        private TextBlock AddUserBubble(string text) => AddBubble(text, true);
        private TextBlock AddAiBubble(string text) => AddBubble(text, false);

        private TextBlock AddBubble(string text, bool isUser)
        {
            var tb = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = text,
                Foreground = isUser ? BrushOf("#FF10395E") : BrushOf("#FF333333")
            };
            var border = new Border
            {
                Background = isUser ? BrushOf("#FFDCEBF7") : BrushOf("#FFF5F5F5"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 4, 0, 0),
                Child = tb
            };
            ChatPanel.Children.Add(border);
            ScrollChat();
            return tb;
        }

        private void ScrollChat()
        {
            ChatScroll.ScrollToEnd();
        }

        // ==================== 工程图（T22 GB 三视图） ====================

        /// <summary>对话直调关键词：命中后不走 LLM，直接确定性出图。</summary>
        private static bool IsDrawingRequest(string text)
        {
            return text.Contains("出工程图")
                || text.Contains("生成工程图")
                || text.Contains("三视图");
        }

        private async void GenerateDrawingClick(object sender, RoutedEventArgs e)
            => await GenerateDrawingAsync().ConfigureAwait(true);

        /// <summary>Ribbon 入口（经 TaskPaneHost 转发）；async void 仅作事件式入口。</summary>
        public void GenerateDrawing() => _ = GenerateDrawingAsync();

        /// <summary>
        /// 为当前活动零件一键生成 GB 第一角三视图工程图（.slddrw + PDF）。
        /// 重入保护；输出到 %AppData%\SwAiAssistant\temp\drawings\零件名.*；全程气泡反馈。
        /// </summary>
        private async Task GenerateDrawingAsync()
        {
            if (_drawings == null || _docs == null)
            {
                AddAiBubble("工程图服务尚未就绪，请重启 SolidWorks 后重试。");
                return;
            }
            if (_drawingBusy) return;

            DocState state;
            try { state = _docs.GetState(); }
            catch (Exception ex)
            {
                Log.Error("UI", "读取活动文档状态失败", ex);
                AddAiBubble("无法读取活动文档状态：" + ex.Message);
                return;
            }
            if (!state.HasActiveDocument || !state.ActiveIsPart)
            {
                AddAiBubble("请先打开一个已保存的零件，再生成工程图（工程图视图按磁盘文件引用模型）。");
                RootTabs.SelectedItem = ChatTab;
                return;
            }

            string title = state.ActiveDocTitle ?? "零件";
            string baseName = title.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase)
                ? title.Substring(0, title.Length - ".sldprt".Length)
                : title;
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                baseName = baseName.Replace(c, '_');
            }
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "零件";

            string dir = Path.Combine(AppPaths.Temp, "drawings");
            string drwPath = Path.Combine(dir, baseName + ".slddrw");
            string pdfPath = Path.Combine(dir, baseName + ".pdf");

            _drawingBusy = true;
            if (DrawingButton != null) DrawingButton.IsEnabled = false;
            StatusText.Text = "正在生成工程图…";
            RootTabs.SelectedItem = ChatTab;
            var bubble = AddAiBubble($"正在为「{baseName}」生成 GB 第一角三视图工程图（含模型尺寸与 PDF）…");
            try
            {
                // 重 COM 流程放后台线程（DrawingService 内部自带 STA 封送）
                var result = await Task.Run(() =>
                {
                    var part = _docs.GetActivePart();
                    if (part == null) throw new CadException("活动零件已丢失，请重新打开零件。");
                    return _drawings.CreateThreeViewDrawing(part, drwPath, pdfPath);
                }).ConfigureAwait(true);

                var sb = new StringBuilder();
                sb.Append($"工程图已生成：{result.ViewCount} 个视图，比例 {result.ScaleNum}:{result.ScaleDen}，");
                sb.Append($"插入模型尺寸 {result.InsertedDimensionCount}/{result.MarkedDimensionCount} 个。");
                if (result.UsedFallbackLayout)
                {
                    sb.Append("（一键三视图不可用，已走手动第一角降级布图）");
                }
                sb.Append("\n工程图：" + result.DrawingPath);
                if (!string.IsNullOrEmpty(result.PdfPath)) sb.Append("\nPDF：" + result.PdfPath);
                bubble.Text = sb.ToString();
                Log.Info("UI", "工程图生成完成：" + result.DrawingPath);
            }
            catch (CadException ex)
            {
                Log.Warn("UI", "工程图生成失败：" + ex.Message);
                bubble.Text = "工程图生成失败：" + ex.Message;
            }
            catch (Exception ex)
            {
                Log.Error("UI", "工程图生成异常", ex);
                bubble.Text = SwAiAssistant.Core.Diagnostics.ErrorText.Friendly("生成工程图", ex);
            }
            finally
            {
                _drawingBusy = false;
                if (DrawingButton != null) DrawingButton.IsEnabled = true;
                StatusText.Text = "就绪";
                ScrollChat();
            }
        }

        // ==================== 装配体辅助（T23 同轴装配 / 干涉检查） ====================

        /// <summary>干涉检查关键词（先于装配关键词判定）。</summary>
        private static bool IsInterferenceRequest(string text)
        {
            return text.Contains("干涉检查") || text.Contains("检查干涉") || text.Contains("干涉");
        }

        /// <summary>同轴装配关键词：「同轴装到…孔」「把 A 装到 B」「装配」。</summary>
        private static bool IsAssemblyRequest(string text)
        {
            return text.Contains("同轴")
                || (text.Contains("装到") && text.Contains("孔"))
                || text.Contains("装配");
        }

        /// <summary>
        /// 对话式一键装配：枚举已打开已保存零件（包围盒最大=含孔基座、次大=销轴装配件），
        /// 弹确认框列出配对与操作计划，确认后新建装配体执行同轴+端面重合并回贴干涉结果。
        /// </summary>
        private async Task AssembleCurrentPartsAsync()
        {
            if (_assemblies == null)
            {
                AddAiBubble("装配服务尚未就绪，请重启 SolidWorks 后重试。");
                return;
            }
            if (_assemblyBusy) return;
            RootTabs.SelectedItem = ChatTab;

            _assemblyBusy = true;
            StatusText.Text = "正在准备装配…";
            var bubble = AddAiBubble("正在读取当前打开的零件，准备装配计划…");
            try
            {
                IList<OpenPartInfo> parts = await Task.Run(
                    () => _assemblies.GetOpenSavedParts()).ConfigureAwait(true);
                if (parts.Count < 2)
                {
                    bubble.Text = "未找到两个已保存的零件。请先在 SolidWorks 中打开并保存：一个含孔的基座件和一个销轴类装配件，然后再发出装配指令。";
                    return;
                }

                OpenPartInfo basePart = parts[0];
                OpenPartInfo pinPart = parts[1];
                string plan =
                    "即将执行自动装配（自动识别：体积较大者为含孔基座）：\n" +
                    $"• 基座（含孔）：{basePart.Title}\n" +
                    $"• 装配件（销/轴）：{pinPart.Title}\n" +
                    "• 操作：新建装配体 → 插入两零件 → 同轴配合 → 端面重合 → 干涉检查\n\n" +
                    "确认执行？";
                var choice = MessageBox.Show(plan, "装配计划确认",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (choice != MessageBoxResult.Yes)
                {
                    bubble.Text = "已取消装配。";
                    return;
                }

                bubble.Text = $"正在将「{pinPart.Title}」同轴装配到「{basePart.Title}」并对齐端面…";
                AssemblyBuildResult result = await Task.Run(
                    () => _assemblies.AssembleTwoOpenParts()).ConfigureAwait(true);

                var sb = new StringBuilder();
                sb.AppendLine($"装配完成：基座「{result.BaseTitle}」+ 装配件「{result.PinTitle}」。");
                foreach (MateResult m in result.Mates)
                {
                    string label = m.MateType == "同轴" ? "同轴配合"
                        : m.MateType == "重合" ? "端面重合配合" : m.MateType;
                    sb.AppendLine(m.Success ? $"✓ {label}成功。" : $"✗ {label}失败：{m.Message}");
                }
                sb.Append(result.Interference.TextReport);
                bubble.Text = sb.ToString();
                Log.Info("UI", $"对话式装配完成：{result.BaseTitle} + {result.PinTitle}");
            }
            catch (CadException ex)
            {
                Log.Warn("UI", "装配失败：" + ex.Message);
                bubble.Text = "装配失败：" + ex.Message;
            }
            catch (Exception ex)
            {
                Log.Error("UI", "装配异常", ex);
                bubble.Text = SwAiAssistant.Core.Diagnostics.ErrorText.Friendly("自动装配", ex);
            }
            finally
            {
                _assemblyBusy = false;
                StatusText.Text = "就绪";
                ScrollChat();
            }
        }

        /// <summary>对当前活动装配体直接执行干涉检查并回贴中文报告。</summary>
        private async Task CheckInterferenceAsync()
        {
            if (_assemblies == null)
            {
                AddAiBubble("装配服务尚未就绪，请重启 SolidWorks 后重试。");
                return;
            }
            if (_assemblyBusy) return;
            RootTabs.SelectedItem = ChatTab;

            _assemblyBusy = true;
            StatusText.Text = "正在进行干涉检查…";
            var bubble = AddAiBubble("正在对当前装配体进行干涉检查…");
            try
            {
                InterferenceReport report = await Task.Run(
                    () => _assemblies.CheckInterferenceActive()).ConfigureAwait(true);
                bubble.Text = report.TextReport;
                Log.Info("UI", "干涉检查完成：" + report.Count + " 处");
            }
            catch (CadException ex)
            {
                Log.Warn("UI", "干涉检查失败：" + ex.Message);
                bubble.Text = "干涉检查失败：" + ex.Message;
            }
            catch (Exception ex)
            {
                Log.Error("UI", "干涉检查异常", ex);
                bubble.Text = SwAiAssistant.Core.Diagnostics.ErrorText.Friendly("干涉检查", ex);
            }
            finally
            {
                _assemblyBusy = false;
                StatusText.Text = "就绪";
                ScrollChat();
            }
        }

        // ==================== DXF 图纸反建（T24 实验功能） ====================

        /// <summary>DXF 反建关键词：对话中提到 dxf / 图纸反建也可唤起文件选择框。</summary>
        private static bool IsDxfImportRequest(string text)
        {
            return text.IndexOf("dxf", StringComparison.OrdinalIgnoreCase) >= 0
                || text.Contains("图纸反建")
                || text.Contains("反建模型");
        }

        private async void ImportDxfClick(object sender, RoutedEventArgs e)
            => await ImportDxfAsync().ConfigureAwait(true);

        private bool _reverseBusy;

        /// <summary>
        /// 选 DXF → ReversePlanner 纯托管识别 → 候选计划卡片（实验标识 + 识别摘要/警告）。
        /// 唯一人工确认门是计划页既有「执行」按钮；识别不出板厚时仅提示、不生成计划（SW 零变化）。
        /// </summary>
        private async Task ImportDxfAsync()
        {
            if (_reverseBusy) return;

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要反建的 DXF 图纸（实验功能）",
                Filter = "DXF 图纸 (*.dxf)|*.dxf|DWG 图纸 (*.dwg)|*.dwg|所有文件 (*.*)|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog() != true) return;

            string path = dlg.FileName;
            if (path.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(DxfParser.DwgGuidance, "DWG 需先转换为 DXF",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _reverseBusy = true;
            ImportDxfButton.IsEnabled = false;
            RootTabs.SelectedItem = ChatTab;
            StatusText.Text = "正在识别 DXF 图纸…";
            var bubble = AddAiBubble(
                $"正在识别 DXF 图纸「{Path.GetFileName(path)}」，生成候选建模计划（此阶段不会改动 SolidWorks）…");
            try
            {
                var result = await Task.Run(() => new ReversePlanner().Plan(path)).ConfigureAwait(true);

                var sb = new StringBuilder();
                sb.AppendLine($"DXF 图纸识别完成：{result.Sheet.FileName}");
                foreach (var s in result.Summary) sb.AppendLine(s);
                foreach (var w in result.Warnings) sb.AppendLine("⚠ " + w);

                if (!result.CanBuild || result.Tree == null)
                {
                    sb.Append("已停在识别阶段：当前无法生成可执行建模计划（常见原因：图纸缺少板厚标注），SolidWorks 未发生任何变化。");
                    sb.Append("可在图纸中补充「t=厚度」标注或在 CAD 中设置毫米单位后另存 DXF，再重新导入。");
                    bubble.Text = sb.ToString();
                    Log.Info("UI", "DXF 反建停在识别阶段（未生成候选）：" + path);
                    return;
                }

                string banner = $"⚠ 实验功能 · DXF 图纸识别候选（置信度：{result.ConfidenceLevel} {result.Confidence:0%}）。"
                    + "AI 图纸识别结果，请逐项核对尺寸后再点「执行」；未点执行前 SolidWorks 不会有任何变化。";
                BuildPlanCard(result.Tree, banner);
                sb.Append("已生成候选建模计划并切到「计划」页，请逐项核对草图尺寸与拉伸深度，确认无误后点「执行」建模。");
                bubble.Text = sb.ToString();
                RootTabs.SelectedItem = PlanTab;
                Log.Info("UI",
                    $"DXF 反建候选已生成：{result.Sheet.FileName}，{result.Tree.Steps.Count} 步，置信度 {result.Confidence:0%}");
            }
            catch (DxfParseException ex)
            {
                bubble.Text = "DXF 识别失败：" + ex.Message;
                Log.Warn("UI", "DXF 识别失败：" + ex.Message);
            }
            catch (Exception ex)
            {
                Log.Error("UI", "DXF 反建异常", ex);
                bubble.Text = SwAiAssistant.Core.Diagnostics.ErrorText.Friendly("DXF 图纸识别", ex);
            }
            finally
            {
                _reverseBusy = false;
                ImportDxfButton.IsEnabled = true;
                StatusText.Text = "就绪";
                ScrollChat();
            }
        }

        // ==================== 图片/PDF 图纸反建（T25 实验功能） ====================

        /// <summary>图片反建关键词：图片/照片/截图/PDF + 反建/识别，或直接说「导入图片」。</summary>
        private static bool IsImageImportRequest(string text)
        {
            return text.IndexOf("pdf", StringComparison.OrdinalIgnoreCase) >= 0
                || text.Contains("图片反建")
                || text.Contains("图纸照片")
                || text.Contains("导入图片")
                || (text.Contains("图片") && (text.Contains("反建") || text.Contains("识别")))
                || (text.Contains("照片") && text.Contains("图纸"));
        }

        private async void ImportImageClick(object sender, RoutedEventArgs e)
            => await ImportImageAsync().ConfigureAwait(true);

        /// <summary>
        /// 选图片/PDF → ImageReversePlanner 调视觉模型 → 与 DXF 同构的候选计划卡片
        /// （固定「AI 识别结果，请逐项核对尺寸」实验提示）。未点计划页「执行」前绝不建模；
        /// 模型要求澄清或缺视觉模型时仅气泡提示，SW 零变化。
        /// </summary>
        private async Task ImportImageAsync()
        {
            if (_reverseBusy) return;

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要反建的图纸照片/截图/PDF（实验功能）",
                Filter = "图纸图片/PDF (*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.pdf)|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.pdf"
                    + "|JPEG 图片 (*.jpg;*.jpeg)|*.jpg;*.jpeg|PNG 图片 (*.png)|*.png|PDF 文档 (*.pdf)|*.pdf"
                    + "|所有文件 (*.*)|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog() != true) return;

            string path = dlg.FileName;
            if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && !PdfToImage.IsSupported)
            {
                MessageBox.Show(PdfToImage.PdfGuidance, "PDF 需先转为图片",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _reverseBusy = true;
            ImportDxfButton.IsEnabled = false;
            ImportImageButton.IsEnabled = false;
            RootTabs.SelectedItem = ChatTab;
            StatusText.Text = "正在识别图纸图片…";
            var bubble = AddAiBubble(
                $"正在用视觉模型识别图纸「{Path.GetFileName(path)}」，生成候选建模计划（此阶段不会改动 SolidWorks）…");
            try
            {
                var profiles = await GetProfilesAsync(CancellationToken.None).ConfigureAwait(true);
                var planner = new ImageReversePlanner(_scheduler);
                var result = await Task.Run(async () =>
                    await planner.PlanAsync(path, profiles, CancellationToken.None).ConfigureAwait(false)
                ).ConfigureAwait(true);

                if (result.NeedClarify)
                {
                    bubble.Text = "视觉模型未能从图纸中识别出足够信息，已停在识别阶段，SolidWorks 未发生任何变化。\n"
                        + "模型说明：" + result.ClarifyText
                        + "\n建议：换正视、清晰、带长宽与板厚标注的图纸照片，或直接导入 DXF 源文件。";
                    Log.Info("UI", "图片反建模型要求澄清，未生成候选：" + result.ClarifyText);
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine("图片/PDF 图纸识别完成：" + Path.GetFileName(path));
                if (!string.IsNullOrWhiteSpace(result.Summary)) sb.AppendLine(result.Summary);
                sb.AppendLine($"模型自评置信度：{result.ConfidenceText}");
                foreach (var w in result.Warnings) sb.AppendLine("⚠ " + w);

                BuildPlanCard(result.Tree, ImageReversePlanner.FormatBanner(result.Confidence));
                sb.Append("已生成候选建模计划并切到「计划」页，请逐项核对草图尺寸、孔径与板厚，确认无误后点「执行」建模。");
                bubble.Text = sb.ToString();
                RootTabs.SelectedItem = PlanTab;
                Log.Info("UI",
                    $"图片反建候选已生成：{Path.GetFileName(path)}，{result.Tree.Steps.Count} 步，置信度 {result.ConfidenceText}");
            }
            catch (LlmException ex)
            {
                bubble.Text = "图片识别未能完成：" + ex.Message;
                Log.Warn("UI", "图片反建失败：" + ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                // PDF 渲染不可用/无法打开等中文引导
                bubble.Text = ex.Message;
                Log.Warn("UI", "图片反建前置失败：" + ex.Message);
            }
            catch (Exception ex)
            {
                Log.Error("UI", "图片反建异常", ex);
                bubble.Text = SwAiAssistant.Core.Diagnostics.ErrorText.Friendly("图片/PDF 图纸识别", ex);
            }
            finally
            {
                _reverseBusy = false;
                ImportDxfButton.IsEnabled = true;
                ImportImageButton.IsEnabled = true;
                StatusText.Text = "就绪";
                ScrollChat();
            }
        }

        // ==================== 按 SolidWorks 工程图反建零件 ====================

        /// <summary>
        /// 按图反建关键词：「工程图 + 建模/反建/制作零件」，或按图建模/按此图纸/根据（按照）图纸。
        /// 注意与 IsDrawingRequest（生成工程图）区分：后者是出图纸，本处是按图纸出零件。
        /// </summary>
        private static bool IsDrawingReverseRequest(string text)
        {
            if (text.Contains("按图建模") || text.Contains("按此图纸")
                || text.Contains("根据图纸") || text.Contains("按照图纸")
                || text.Contains("当前图纸") || text.Contains("这张图纸"))
            {
                return true;
            }
            bool drawing = text.Contains("工程图");
            bool build = text.Contains("反建") || text.Contains("建模")
                || text.Contains("制作零件") || text.Contains("生成零件模型");
            return drawing && build;
        }

        private async void ReverseDrawingClick(object sender, RoutedEventArgs e)
            => await ReverseFromDrawingAsync().ConfigureAwait(true);

        /// <summary>
        /// 按当前 SolidWorks 工程图反建：读取活动工程图 → 确定性提取（视图标注 + 可见圆边模型坐标）
        /// + 导出图纸图（视觉补充）→ DrawingReversePlanner（文本优先）→ 候选计划卡片；
        /// 信息不足时气泡向用户追问。未点计划页「执行」前绝不建模；不打开工程图引用的源零件。
        /// </summary>
        private async Task ReverseFromDrawingAsync()
        {
            if (_reverseBusy) return;
            if (_drawings == null || _capture == null)
            {
                AddAiBubble("按图建模服务尚未就绪，请稍候或重启 SolidWorks。");
                return;
            }

            SolidWorks.Interop.sldworks.IModelDoc2 drawing = null;
            try { drawing = _docs.GetActiveDrawing(); }
            catch (Exception ex) { Log.Warn("UI", "活动工程图读取失败：" + ex.Message); }
            if (drawing == null)
            {
                AddAiBubble("当前没有打开的 SolidWorks 工程图（.slddrw）。请先切换到要反建的工程图文档，再点「按图建模」。");
                return;
            }

            _reverseBusy = true;
            ImportDxfButton.IsEnabled = false;
            ImportImageButton.IsEnabled = false;
            ReverseDrawingButton.IsEnabled = false;
            RootTabs.SelectedItem = ChatTab;
            StatusText.Text = "正在按工程图提取尺寸…";
            string title = "";
            try { title = drawing.GetTitle(); } catch { }
            var bubble = AddAiBubble(
                $"正在读取当前工程图「{title}」的视图、标注与几何，生成候选建模计划"
                + "（不打开源零件；此阶段不会改动 SolidWorks）…");
            try
            {
                DrawingExtract extract = await Task.Run(
                    () => _drawings.ExtractDrawing(drawing)).ConfigureAwait(true);

                string sheetImage = null;
                try
                {
                    string shotDir = Path.Combine(Path.GetTempPath(), "SwAiAssistant", "draw");
                    IList<string> shots = await Task.Run(
                        () => _capture.CaptureDrawingSheets(drawing, shotDir, 1600)).ConfigureAwait(true);
                    sheetImage = shots.FirstOrDefault();
                }
                catch (Exception ex)
                {
                    // 图纸图导出失败不阻断文本路径
                    Log.Warn("UI", "工程图图纸图导出失败（继续纯文本路径）：" + ex.Message);
                }

                var profiles = await GetProfilesAsync(CancellationToken.None).ConfigureAwait(true);
                var planner = new DrawingReversePlanner(_scheduler);
                var result = await Task.Run(async () =>
                    await planner.PlanAsync(extract, profiles, sheetImage, CancellationToken.None)
                        .ConfigureAwait(false)).ConfigureAwait(true);

                if (result.NeedClarify)
                {
                    bubble.Text = "工程图中可确定的信息不足，已停在规划阶段，SolidWorks 未发生任何变化。\n"
                        + "需要你确认：" + result.ClarifyText
                        + "\n请直接回复补充信息，我会重新按图建模；或换用标注更完整的图纸。";
                    Log.Info("UI", "工程图反建要求澄清：" + result.ClarifyText);
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine("工程图反建完成：" + title);
                if (!string.IsNullOrWhiteSpace(result.Summary)) sb.AppendLine(result.Summary);
                sb.AppendLine($"规划路径：{(result.UsedVision ? "视觉模型读图" : "确定性尺寸文本")}"
                    + $"，模型自评置信度：{result.ConfidenceText}");
                foreach (var w in result.Warnings) sb.AppendLine("⚠ " + w);

                BuildPlanCard(result.Tree,
                    DrawingReversePlanner.FormatBanner(result.Confidence, result.UsedVision));
                sb.Append("已生成候选建模计划并切到「计划」页，请逐项核对草图尺寸与孔径，确认无误后点「执行」建模。");
                bubble.Text = sb.ToString();
                RootTabs.SelectedItem = PlanTab;
                Log.Info("UI", $"工程图反建候选已生成：{title}，{result.Tree.Steps.Count} 步，"
                    + $"路径={(result.UsedVision ? "视觉" : "文本")}，置信度 {result.ConfidenceText}");
            }
            catch (LlmException ex)
            {
                bubble.Text = "按图建模未能完成：" + ex.Message;
                Log.Warn("UI", "工程图反建失败：" + ex.Message);
            }
            catch (Exception ex)
            {
                Log.Error("UI", "工程图反建异常", ex);
                bubble.Text = SwAiAssistant.Core.Diagnostics.ErrorText.Friendly("按工程图反建", ex);
            }
            finally
            {
                _reverseBusy = false;
                ImportDxfButton.IsEnabled = true;
                ImportImageButton.IsEnabled = true;
                ReverseDrawingButton.IsEnabled = true;
                StatusText.Text = "就绪";
                ScrollChat();
            }
        }

        // ==================== 计划卡片 ====================

        private sealed class PlanRow
        {
            public PlanStep Step;
            public Border Ui;
            public readonly Dictionary<string, Control> Fields =
                new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>渲染计划卡片；banner 非空时在顶部插入醒目提示条（图纸反建候选的实验/核对提示）。</summary>
        private void BuildPlanCard(FeatureTree plan, string banner = null)
        {
            _currentPlan = plan;
            _planRows.Clear();
            PlanStepsPanel.Children.Clear();

            if (!string.IsNullOrEmpty(banner))
            {
                PlanStepsPanel.Children.Add(new Border
                {
                    Background = BrushOf("#FFFFF4E5"),
                    BorderBrush = BrushOf("#FFE0A040"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8),
                    Margin = new Thickness(0, 0, 0, 6),
                    Child = new TextBlock
                    {
                        Text = banner,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = BrushOf("#FF8A5200"),
                        FontWeight = FontWeights.SemiBold
                    }
                });
            }

            PlanHeader.Text = $"零件：{plan.Part?.Name ?? "AI零件"}" +
                (string.IsNullOrWhiteSpace(plan.Part?.MaterialGuess) ? "" : $"　材料猜测：{plan.Part.MaterialGuess}") +
                $"　共 {plan.Steps.Count} 步（数值可直接修改）";

            foreach (var step in plan.Steps)
            {
                var row = BuildStepRow(step);
                _planRows.Add(row);
                PlanStepsPanel.Children.Add(row.Ui);
            }
            ExecutePlanButton.IsEnabled = true;
            CancelPlanButton.IsEnabled = true;
            PlanHint.Text = "";
        }

        private void ClearPlan(string hint)
        {
            _currentPlan = null;
            _planRows.Clear();
            PlanStepsPanel.Children.Clear();
            PlanHeader.Text = "暂无计划。在对话页描述零件后，AI 会把建模步骤列在这里。";
            ExecutePlanButton.IsEnabled = false;
            CancelPlanButton.IsEnabled = false;
            PlanHint.Text = hint ?? "";
        }

        private PlanRow BuildStepRow(PlanStep step)
        {
            var row = new PlanRow { Step = step };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = $"{step.Id} · {KindName(step.Kind)} · {step.Title}",
                FontWeight = FontWeights.Bold
            });
            string detail = StepDetail(step);
            if (!string.IsNullOrEmpty(detail))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = detail,
                    FontSize = 11,
                    Foreground = BrushOf("#FF777777"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 2)
                });
            }
            AddStepEditors(panel, step, row);

            row.Ui = new Border
            {
                BorderBrush = BrushOf("#FFDDDDDD"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6),
                Margin = new Thickness(0, 3, 0, 3),
                Child = panel
            };
            return row;
        }

        private void AddStepEditors(StackPanel panel, PlanStep step, PlanRow row)
        {
            switch ((step.Kind ?? "").ToLowerInvariant())
            {
                case "sketch":
                    if (step.Sketch != null)
                    {
                        AddTextField(panel, row, "sketch.plane", "基准面(top/front/right)", step.Sketch.Plane);
                    }
                    break;
                case "extrudeboss":
                case "extrudecut":
                    if (step.Extrude != null)
                    {
                        AddTextField(panel, row, "extrude.depthMm", "深度 mm",
                            Fmt(step.Extrude.DepthMm));
                        if (step.Kind.Equals("extrudeCut", StringComparison.OrdinalIgnoreCase))
                        {
                            AddBoolField(panel, row, "extrude.throughAll", "双向贯穿", step.Extrude.ThroughAll);
                        }
                        AddBoolField(panel, row, "extrude.flip", "反方向", step.Extrude.Flip);
                    }
                    break;
                case "revolveboss":
                case "revolvecut":
                    if (step.Revolve != null)
                    {
                        AddTextField(panel, row, "revolve.angleDeg", "旋转角 度",
                            Fmt(step.Revolve.AngleDeg));
                    }
                    break;
                case "fillet":
                    if (step.Fillet != null)
                    {
                        AddTextField(panel, row, "fillet.radiusMm", "圆角半径 mm", Fmt(step.Fillet.RadiusMm));
                    }
                    break;
                case "chamfer":
                    if (step.Chamfer != null)
                    {
                        AddTextField(panel, row, "chamfer.distanceMm", "倒角距离 mm", Fmt(step.Chamfer.DistanceMm));
                    }
                    break;
                case "holewizard":
                    if (step.Hole != null)
                    {
                        AddTextField(panel, row, "hole.diameterMm", "孔径 mm", Fmt(step.Hole.DiameterMm));
                    }
                    break;
                case "linearpattern":
                    if (step.Pattern != null)
                    {
                        AddTextField(panel, row, "pattern.spacingMm", "间距 mm", Fmt(step.Pattern.SpacingMm));
                        AddTextField(panel, row, "pattern.count", "数量", step.Pattern.Count?.ToString(CultureInfo.InvariantCulture));
                    }
                    break;
                case "circularpattern":
                    if (step.Pattern != null)
                    {
                        AddTextField(panel, row, "pattern.totalAngleDeg", "总角 度", Fmt(step.Pattern.TotalAngleDeg));
                        AddTextField(panel, row, "pattern.count", "数量", step.Pattern.Count?.ToString(CultureInfo.InvariantCulture));
                    }
                    break;
                case "mirror":
                    if (step.Mirror != null)
                    {
                        AddTextField(panel, row, "mirror.plane", "镜像面(front/top/right)", step.Mirror.Plane);
                    }
                    break;
                case "rib":
                    if (step.Rib != null)
                    {
                        AddTextField(panel, row, "rib.thicknessMm", "筋厚 mm", Fmt(step.Rib.ThicknessMm));
                    }
                    break;
                case "draft":
                    if (step.Draft != null)
                    {
                        AddTextField(panel, row, "draft.angleDeg", "拔模角 度", Fmt(step.Draft.AngleDeg));
                    }
                    break;
                case "shell":
                    if (step.Shell != null)
                    {
                        AddTextField(panel, row, "shell.thicknessMm", "壁厚 mm", Fmt(step.Shell.ThicknessMm));
                    }
                    break;
                case "setmaterial":
                    if (step.SetMaterial != null)
                    {
                        AddTextField(panel, row, "setmaterial.materialName", "材料", step.SetMaterial.MaterialName);
                    }
                    break;
            }
        }

        private void AddTextField(StackPanel panel, PlanRow row, string key, string label, string value)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            sp.Children.Add(new TextBlock
            {
                Text = label + "：",
                Width = 150,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11
            });
            var tb = new TextBox
            {
                Width = 100,
                Padding = new Thickness(3, 1, 3, 1),
                Text = value ?? ""
            };
            sp.Children.Add(tb);
            panel.Children.Add(sp);
            row.Fields[key] = tb;
        }

        private void AddBoolField(StackPanel panel, PlanRow row, string key, string label, bool value)
        {
            var cb = new CheckBox
            {
                Content = label,
                IsChecked = value,
                Margin = new Thickness(0, 1, 0, 1),
                FontSize = 11
            };
            panel.Children.Add(cb);
            row.Fields[key] = cb;
        }

        private async void ExecutePlanClick(object sender, RoutedEventArgs e) => await ExecuteCurrentPlanAsync();

        private void CancelPlanClick(object sender, RoutedEventArgs e)
        {
            // 双角色（M4-T19）：执行中 = 取消执行；执行前 = 清空计划
            if (_execCts != null)
            {
                PlanHint.Text = "正在取消执行…";
                Log.Info("UI", "用户请求取消执行");
                _execCts.Cancel();
                return;
            }
            ClearPlan("计划已取消。");
            Log.Info("UI", "用户取消计划");
        }

        private async Task ExecuteCurrentPlanAsync()
        {
            // 重入保护（M4-T19 串行化）：同一时间只允许一个执行任务
            if (_currentPlan == null || _executor == null || _execCts != null) return;

            if (!ApplyPlanEdits(out string editError))
            {
                PlanHint.Text = editError;
                return;
            }
            var errors = FeatureTreeValidator.Validate(_currentPlan);
            if (errors.Count > 0)
            {
                PlanHint.Text = "修改后校验未通过：" + errors[0];
                return;
            }

            // 有打开零件时先二选一（UI 线程弹窗，避免在 STA 封送内弹窗）
            var state = _docs.GetState();
            DocChoice preset = DocChoice.ContinueCurrent;
            if (state.HasActiveDocument && state.ActiveIsPart)
            {
                var dlg = new DocChoiceDialog(state.ActiveDocTitle);
                dlg.ShowDialog();
                if (dlg.Result == DocChoiceResult.Cancel)
                {
                    PlanHint.Text = "已取消执行。";
                    return;
                }
                preset = dlg.Result == DocChoiceResult.New ? DocChoice.CreateNew : DocChoice.ContinueCurrent;
            }

            // 在既有零件上执行前先记快照（M4-T16：一键回滚数据源；新建文档无旧状态可回滚，跳过）
            // M4-T19 零阻塞审计：快照枚举尺寸为重 COM 操作，放后台线程（Cad 服务自带 STA 封送）
            SnapshotData preSnap = null;
            if (preset == DocChoice.ContinueCurrent && _snapshots != null)
            {
                try
                {
                    preSnap = await Task.Run(() =>
                    {
                        var doc = _docs.GetActivePart();
                        return doc != null
                            ? _snapshots.Capture(doc, "执行计划：" + (_currentPlan.Part?.Name ?? "AI零件"))
                            : null;
                    }).ConfigureAwait(true);
                    if (preSnap != null)
                    {
                        Log.Info("UI", "执行前快照：" + preSnap.Id);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("UI", "执行前快照失败（不阻断执行）：" + ex.Message);
                }
            }

            ExecutePlanButton.IsEnabled = false;
            // 取消按钮执行期间保持可用（双角色：此时为「取消执行」）
            CancelPlanButton.IsEnabled = true;
            var plan = _currentPlan;
            _execCts = new CancellationTokenSource();
            try
            {
                // FR-15：ExecuteWithRetry 在 SolidWorks 报几何类错误时带错误信息回传模型重规划（最多 2 次）。
                // 整个同步执行链放后台线程（Cad 服务自带 STA 封送）；fixPlan 回调内阻塞等待异步 LLM 请求。
                var profiles = await GetProfilesAsync(_execCts.Token).ConfigureAwait(true);
                var token = _execCts.Token;
                var report = await Task.Run(() => _executor.ExecuteWithRetry(plan,
                    // 既有零件：已在 fixPlan 回调回滚到 preSnap，继续同一文档整树重跑；
                    // 新建路径：EnsurePartDocument 每次新建空白零件，半成品留在旧窗口（未保存）
                    _ => preset, token,
                    (failed, swError) =>
                    {
                        // B-1 修复：重试前必须先把半成品恢复到执行前状态，否则整树重跑会让
                        // 抽壳二次减薄、拔模角度翻倍等步骤产生静默错误几何。
                        if (preset == DocChoice.ContinueCurrent)
                        {
                            if (_snapshots == null || preSnap == null)
                            {
                                Log.Warn("UI", "无执行前快照，无法回滚半成品，放弃自动重试以避免重复施加特征。");
                                PostToUi(() => AddAiBubble(
                                    "执行中途失败，且没有可回滚的执行前快照，为避免在半成品上重复建模，已停止自动重试。请检查模型后手动重试或从快照页回滚。"));
                                return null;
                            }
                            try
                            {
                                var part = _docs.GetActivePart();
                                if (part == null) return null;
                                _snapshots.Rollback(part, preSnap.Id);
                                Log.Info("UI", "AI 重试前已回滚到执行前快照：" + preSnap.Id);
                                PostToUi(() => AddAiBubble(
                                    "SolidWorks 执行报错，已先回滚到执行前状态，正在让 AI 修正计划后重试：" + swError));
                            }
                            catch (Exception rbEx)
                            {
                                Log.Warn("UI", "重试前回滚半成品失败，放弃自动重试：" + rbEx.Message);
                                PostToUi(() => AddAiBubble(
                                    "执行中途失败，且半成品回滚失败（" + rbEx.Message + "），已停止自动重试。请从快照页手动回滚后再试。"));
                                return null;
                            }
                        }
                        else
                        {
                            PostToUi(() => AddAiBubble(
                                "SolidWorks 执行报错，正在让 AI 修正计划后重试（将在新建零件中重跑，半成品窗口保留未保存）：" + swError));
                        }
                        try
                        {
                            return _planner.FixPlanAsync(failed, swError, profiles, token)
                                .GetAwaiter().GetResult();
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Log.Warn("UI", "自动重规划调用失败：" + ex.Message);
                            return null;
                        }
                    }, 2), token).ConfigureAwait(true);
                var sb = new StringBuilder();
                sb.Append($"已在「{report.DocTitle}」建成 {report.CreatedFeatures.Count} 个特征。");
                sb.Append($"\n体积 {report.VolumeMm3:F1} mm³，质量 {report.MassKg:F3} kg");
                if (report.BoundingBox != null)
                {
                    sb.Append($"\n包围盒 {report.BoundingBox.SizeX:F1} × {report.BoundingBox.SizeY:F1} × {report.BoundingBox.SizeZ:F1} mm");
                }
                foreach (var note in report.Notes)
                {
                    sb.Append("\n提示：" + note);
                }

                // 强校验闭环（M4-T18）：理论预算 vs 实际回读；超限弹「采纳/回滚/让 AI 修复」
                var verifyReport = await VerifyAfterExecuteAsync(plan, report).ConfigureAwait(true);
                sb.Append("\n" + verifyReport.SummaryText());
                string vision = await RunVisionReviewAsync(plan).ConfigureAwait(true);
                if (!string.IsNullOrEmpty(vision))
                {
                    sb.Append("\n视觉复核：" + vision);
                }
                AddAiBubble(sb.ToString());
                if (!verifyReport.Passed)
                {
                    await HandleVerifyFailureAsync(verifyReport, preSnap).ConfigureAwait(true);
                }
                ClearPlan("执行完成。");
                RootTabs.SelectedItem = ChatTab;
                Log.Info("UI", "计划执行完成：" + report.DocTitle);
            }
            catch (OperationCanceledException)
            {
                Log.Info("UI", "计划执行被用户取消");
                AddAiBubble("执行已取消。已建成的特征可能残留，可在「快照」页回滚到执行前状态。");
                PlanHint.Text = "执行已取消。";
                ExecutePlanButton.IsEnabled = true;
                CancelPlanButton.IsEnabled = true;
            }
            catch (CadException ex)
            {
                Log.Error("UI", "计划执行失败", ex);
                AddAiBubble("建模失败：" + ex.Message);
                PlanHint.Text = "执行失败：" + ex.Message;
                ExecutePlanButton.IsEnabled = true;
                CancelPlanButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                Log.Error("UI", "计划执行异常", ex);
                string friendly = SwAiAssistant.Core.Diagnostics.ErrorText.Friendly("执行计划", ex);
                AddAiBubble(friendly);
                PlanHint.Text = friendly;
                ExecutePlanButton.IsEnabled = true;
                CancelPlanButton.IsEnabled = true;
            }
            finally
            {
                _execCts?.Dispose();
                _execCts = null;
            }
        }

        /// <summary>把计划卡片中的用户编辑写回特征树。</summary>
        private bool ApplyPlanEdits(out string error)
        {
            error = null;
            foreach (var row in _planRows)
            {
                foreach (var kv in row.Fields)
                {
                    if (kv.Value is TextBox tb)
                    {
                        if (!AssignText(row.Step, kv.Key, tb.Text.Trim(), out string err))
                        {
                            error = $"步骤「{row.Step.Title}」：{err}";
                            return false;
                        }
                    }
                    else if (kv.Value is CheckBox cb)
                    {
                        AssignBool(row.Step, kv.Key, cb.IsChecked == true);
                    }
                }
            }
            return true;
        }

        private static bool AssignText(PlanStep step, string key, string v, out string error)
        {
            error = null;
            // 卡片中 Fmt(null) 渲染的占位符「-」按空值处理（如贯穿切除无深度、旋转角缺省）
            if (v == "-" || v == "–") v = "";
            switch (key)
            {
                case "sketch.plane": step.Sketch.Plane = v; return true;
                case "mirror.plane": step.Mirror.Plane = v; return true;
                case "setmaterial.materialName": step.SetMaterial.MaterialName = v; return true;
                case "extrude.depthMm":
                    if (v.Length == 0) { step.Extrude.DepthMm = null; return true; }
                    if (TryDouble(v, out double depth)) { step.Extrude.DepthMm = depth; return true; }
                    error = "深度必须是数字。"; return false;
                case "revolve.angleDeg":
                    if (v.Length == 0) return true; // 留空＝保持 null（执行器默认 360°）
                    if (TryDouble(v, out double ang)) { step.Revolve.AngleDeg = ang; return true; }
                    error = "旋转角必须是数字。"; return false;
                case "fillet.radiusMm":
                    if (TryDouble(v, out double r)) { step.Fillet.RadiusMm = r; return true; }
                    error = "圆角半径必须是数字。"; return false;
                case "chamfer.distanceMm":
                    if (TryDouble(v, out double c)) { step.Chamfer.DistanceMm = c; return true; }
                    error = "倒角距离必须是数字。"; return false;
                case "hole.diameterMm":
                    if (TryDouble(v, out double hd)) { step.Hole.DiameterMm = hd; return true; }
                    error = "孔径必须是数字。"; return false;
                case "pattern.spacingMm":
                    if (TryDouble(v, out double sp)) { step.Pattern.SpacingMm = sp; return true; }
                    error = "阵列间距必须是数字。"; return false;
                case "pattern.totalAngleDeg":
                    if (v.Length == 0) return true; // 留空＝保持 null（执行器默认 360°）
                    if (TryDouble(v, out double ta)) { step.Pattern.TotalAngleDeg = ta; return true; }
                    error = "阵列总角必须是数字。"; return false;
                case "pattern.count":
                    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                    {
                        step.Pattern.Count = n; return true;
                    }
                    error = "阵列数量必须是整数。"; return false;
                case "rib.thicknessMm":
                    if (TryDouble(v, out double rt)) { step.Rib.ThicknessMm = rt; return true; }
                    error = "筋厚必须是数字。"; return false;
                case "draft.angleDeg":
                    if (TryDouble(v, out double da)) { step.Draft.AngleDeg = da; return true; }
                    error = "拔模角必须是数字。"; return false;
                case "shell.thicknessMm":
                    if (TryDouble(v, out double st)) { step.Shell.ThicknessMm = st; return true; }
                    error = "壁厚必须是数字。"; return false;
                default:
                    return true;
            }
        }

        private static void AssignBool(PlanStep step, string key, bool value)
        {
            switch (key)
            {
                case "extrude.throughAll": step.Extrude.ThroughAll = value; break;
                case "extrude.flip": step.Extrude.Flip = value; break;
            }
        }

        private void OnPlanProgress(object sender, PlanProgressEventArgs ev)
        {
            PostToUi(() =>
            {
                StatusText.Text = ev.Message;
                if (_currentPlan != null)
                {
                    PlanHint.Text = ev.Message;
                }
            });
        }

        // ==================== 强校验闭环（M4-T18） ====================

        /// <summary>
        /// 执行后校验：理论预算 vs 实际回读（体积/包围盒/孔数），阈值取配置 VerifyThresholdPct。
        /// 孔数经 QAService 真实回读（服务未就绪或读取失败时传 -1 跳过该项）。
        /// </summary>
        private async Task<SwAiAssistant.Verify.VerifyReport> VerifyAfterExecuteAsync(
            FeatureTree plan, ExecutionReport report)
        {
            // 孔数真实回读（COM 枚举放后台线程）
            int actualHoleCount = -1;
            if (_qa != null)
            {
                try
                {
                    var docForHoles = _docs.GetActivePart();
                    if (docForHoles != null)
                    {
                        var snap = await Task.Run(() => _qa.Capture(docForHoles)).ConfigureAwait(true);
                        actualHoleCount = snap.HoleCount;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("UI", "校验前孔数回读失败（跳过孔数项）：" + ex.Message);
                }
            }
            try
            {
                var box = report.BoundingBox != null
                    ? new SwAiAssistant.Verify.Theory.BoxTheory
                    {
                        X = report.BoundingBox.SizeX,
                        Y = report.BoundingBox.SizeY,
                        Z = report.BoundingBox.SizeZ
                    }
                    : null;
                return new SwAiAssistant.Verify.VerifyService().Verify(plan,
                    report.VolumeMm3, report.MassKg, box,
                    _config?.Current.VerifyThresholdPct ?? 5.0, null, actualHoleCount);
            }
            catch (Exception ex)
            {
                // 低-1 修复：校验自身异常不得按通过放行，否则偏差模型会被静默采纳
                Log.Error("UI", "执行后校验过程异常", ex);
                return new SwAiAssistant.Verify.VerifyReport
                {
                    PartName = plan.Part?.Name ?? "AI零件",
                    Passed = false,
                    Warnings =
                    {
                        "校验过程自身异常，无法确认模型是否符合理论预算：" + ex.Message
                        + "。请人工核对尺寸，必要时用「快照」页回滚。"
                    }
                };
            }
        }

        /// <summary>
        /// 视觉复核（有视觉模型时）：多视角截图 → VisionVerify 送审 → 结论文本；
        /// 无视觉模型/截图失败/调用失败均返回降级说明，绝不抛出阻断主流程。
        /// </summary>
        private async Task<string> RunVisionReviewAsync(FeatureTree plan)
        {
            if (_capture == null || _scheduler == null) return null;
            try
            {
                var vision = new SwAiAssistant.Ai.Vision.VisionVerify(_scheduler);
                var profiles = await GetProfilesAsync(CancellationToken.None).ConfigureAwait(true);
                if (!vision.HasVisionCandidate(profiles))
                {
                    return "当前配置为纯文本模型，已自动跳过视觉复核（可在设置页添加视觉模型）。";
                }
                var doc = _docs.GetActivePart();
                if (doc == null) return null;
                string dir = Path.Combine(AppPaths.Temp, "shots");
                var views = await Task.Run(() => _capture.CapturePartViews(doc, dir)).ConfigureAwait(true);
                string conclusion = await vision.ReviewAsync(BuildPlanSummary(plan), views, profiles,
                    CancellationToken.None).ConfigureAwait(true);
                Log.Info("UI", "视觉复核完成（" + views.Count + " 视角）");
                return conclusion;
            }
            catch (Exception ex)
            {
                Log.Warn("UI", "视觉复核失败（降级跳过）：" + ex.Message);
                return "视觉复核不可用（已跳过）：" + ex.Message;
            }
        }

        /// <summary>供视觉模型的设计意图摘要（零件名 + 各步标题与关键参数）。</summary>
        private static string BuildPlanSummary(FeatureTree plan)
        {
            var sb = new StringBuilder();
            sb.Append("零件「" + (plan.Part?.Name ?? "AI零件") + "」，共 " + plan.Steps.Count + " 步：");
            foreach (var step in plan.Steps)
            {
                string detail = StepDetail(step);
                sb.Append("\n- " + step.Title + "（" + KindName(step.Kind) + "）"
                    + (string.IsNullOrEmpty(detail) ? "" : "：" + detail));
            }
            return sb.ToString();
        }

        /// <summary>偏差超限三选项：采纳（保留）/ 回滚（撤销至执行前快照）/ 让 AI 修复（错误回传对话）。</summary>
        private async Task HandleVerifyFailureAsync(SwAiAssistant.Verify.VerifyReport verifyReport, SnapshotData preSnap)
        {
            var choice = MessageBox.Show(
                verifyReport.SummaryText() + "\n\n【是】采纳（保留模型）\n【否】回滚（撤销本次建模）\n【取消】让 AI 修复",
                "校验偏差超限", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            Log.Info("UI", "校验超限用户选择：" + choice);
            if (choice == MessageBoxResult.No)
            {
                if (preSnap == null || _snapshots == null)
                {
                    AddAiBubble("本次执行为新建文档（无执行前快照），无法自动回滚；可直接关闭该文档。");
                    return;
                }
                try
                {
                    var doc = _docs.GetActivePart();
                    // M4-T19 零阻塞审计：回滚为重 COM 操作，放后台线程
                    var rb = await Task.Run(() => _snapshots.Rollback(doc, preSnap.Id)).ConfigureAwait(true);
                    AddAiBubble($"已回滚本次建模：删除 {rb.DeletedFeatures.Count} 个特征"
                        + $"（{string.Join("、", rb.DeletedFeatures.Take(6))}），"
                        + $"恢复 {rb.RestoredDimensions.Count} 项尺寸。"
                        + (rb.Conflicts.Count > 0 ? "\n冲突：" + string.Join("；", rb.Conflicts) : ""));
                }
                catch (Exception ex)
                {
                    Log.Error("UI", "校验超限回滚失败", ex);
                    AddAiBubble("回滚失败：" + ex.Message);
                }
            }
            else if (choice == MessageBoxResult.Cancel)
            {
                ChatInput.Text = $"刚才的建模校验未通过（体积偏差 {verifyReport.VolumeDeviation:P2}，"
                    + $"阈值 {verifyReport.Threshold:P0}）。请分析原因并给出修正后的建模方案。";
                RootTabs.SelectedItem = ChatTab;
                await SendChatAsync();
            }
            // Yes = 采纳：保留模型，仅记录
        }

        // ==================== 快照页（M4-T16） ====================

        // WPF {Binding} 仅对公共属性生效（公共字段绑定会静默失败渲染为空）
        private sealed class SnapshotRow
        {
            public SnapshotData Data { get; set; }
            public string DisplayName { get; set; }
            public string DisplayDetail { get; set; }
        }

        private void SnapshotRefreshClick(object sender, RoutedEventArgs e) => RefreshSnapshotList();

        private void SnapshotListSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RollbackButton.IsEnabled = SnapshotListBox.SelectedItem is SnapshotRow && _snapshots != null;
            SnapshotResult.Text = "";
        }

        /// <summary>列出当前活动零件的全部存盘快照（按时间倒序）。</summary>
        private void RefreshSnapshotList()
        {
            SnapshotListBox.ItemsSource = null;
            _snapshotItems.Clear();
            RollbackButton.IsEnabled = false;
            if (_snapshots == null)
            {
                SnapshotDocLabel.Text = "快照服务未就绪。";
                return;
            }
            string docTitle = ActivePartTitle();
            if (docTitle == null)
            {
                SnapshotDocLabel.Text = "当前无活动零件文档。";
                return;
            }
            SnapshotDocLabel.Text = "文档：" + docTitle;
            List<SnapshotData> list;
            try
            {
                list = _snapshots.List(docTitle);
            }
            catch (Exception ex)
            {
                SnapshotResult.Text = "快照列表读取失败：" + ex.Message;
                return;
            }
            _snapshotItems.AddRange(list);
            SnapshotListBox.ItemsSource = _snapshotItems.Select(s => new SnapshotRow
            {
                Data = s,
                DisplayName = s.CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss") + " · " + s.CommandSummary,
                DisplayDetail = $"AI 特征 {s.AiFeatures.Count} · 尺寸 {s.Dimensions.Count}"
                    + (string.IsNullOrWhiteSpace(s.MaterialName) ? "" : " · 材料 " + s.MaterialName)
            }).ToList();
            if (_snapshotItems.Count == 0)
            {
                SnapshotResult.Text = "该零件暂无快照。每次 AI 执行前会自动记录快照。";
            }
        }

        /// <summary>当前活动零件标题；无活动零件返回 null。</summary>
        private string ActivePartTitle()
        {
            try
            {
                var state = _docs.GetState();
                return state.HasActiveDocument && state.ActiveIsPart ? state.ActiveDocTitle : null;
            }
            catch (Exception ex)
            {
                Log.Warn("UI", "读取活动文档状态失败：" + ex.Message);
                return null;
            }
        }

        private async void RollbackClick(object sender, RoutedEventArgs e)
        {
            if (!(_snapshots != null && SnapshotListBox.SelectedItem is SnapshotRow row)) return;
            var doc = _docs.GetActivePart();
            if (doc == null)
            {
                SnapshotResult.Text = "当前无活动零件文档。";
                return;
            }
            var answer = MessageBox.Show(
                $"回滚到快照「{row.Data.CreatedAtUtc.ToLocalTime():MM-dd HH:mm:ss} · {row.Data.CommandSummary}」？\n"
                + "回滚前会自动记录当前状态为新快照（可反悔）。",
                "一键回滚", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            RollbackButton.IsEnabled = false;
            SnapshotResult.Foreground = BrushOf("#FF777777");
            SnapshotResult.Text = "回滚中…";
            try
            {
                // M4-T19 零阻塞审计：回滚含删特征/改尺寸/强制重建等重 COM 操作，放后台线程
                string snapId = row.Data.Id;
                var report = await Task.Run(() => _snapshots.Rollback(doc, snapId)).ConfigureAwait(true);
                var sb = new StringBuilder();
                sb.Append($"回滚完成：删除 {report.DeletedFeatures.Count} 个特征"
                    + $"（{string.Join("、", report.DeletedFeatures.Take(6))}），"
                    + $"恢复 {report.RestoredDimensions.Count} 项尺寸");
                if (!string.IsNullOrEmpty(report.RestoredMaterial))
                {
                    sb.Append($"，材料恢复为 {report.RestoredMaterial}");
                }
                if (report.Conflicts.Count > 0)
                {
                    sb.Append("\n冲突提示：\n" + string.Join("\n", report.Conflicts));
                }
                if (report.Notes.Count > 0)
                {
                    sb.Append("\n备注：\n" + string.Join("\n", report.Notes));
                }
                SnapshotResult.Text = sb.ToString();
                SnapshotResult.Foreground = report.Conflicts.Count > 0
                    ? BrushOf("#FFB26A00") : BrushOf("#FF2F6B3A");
                AddAiBubble("已回滚到快照「" + row.Data.CommandSummary + "」。"
                    + (report.Conflicts.Count > 0 ? "存在冲突，请到「快照」页查看。" : ""));
                Log.Info("UI", $"回滚完成 → {row.Data.Id}（删 {report.DeletedFeatures.Count} 特征，"
                    + $"恢复 {report.RestoredDimensions.Count} 尺寸，冲突 {report.Conflicts.Count}）");
            }
            catch (Exception ex)
            {
                SnapshotResult.Foreground = BrushOf("#FFB00000");
                SnapshotResult.Text = SwAiAssistant.Core.Diagnostics.ErrorText.Friendly("回滚", ex);
                Log.Error("UI", "回滚失败", ex);
            }
            RefreshSnapshotList();
        }

        // ==================== 设置页 ====================

        // WPF {Binding} 仅对公共属性生效（公共字段绑定会静默失败渲染为空）
        private sealed class ModelItem
        {
            public ModelConfigEntry Entry { get; set; }
            public string DisplayName { get; set; }
            public string DisplayDetail { get; set; }
        }

        private void RefreshModelList()
        {
            if (_config == null) return;
            var items = _config.Current.Models.Select(m =>
            {
                var p = _probe?.GetCached(m.Id, m.Model);
                string proto = m.Protocol == ModelProtocol.Auto ? "Auto" : m.Protocol.ToString();
                string caps = p != null ? p.CapabilityTags() : "未探测";
                return new ModelItem
                {
                    Entry = m,
                    DisplayName = m.Name + (m.Enabled ? "" : "（已停用）"),
                    DisplayDetail = $"{m.Model} · {proto} · {caps} · {ConnectTag(m)} · {m.BaseUrl}"
                };
            }).ToList();
            ModelListBox.ItemsSource = items;
        }

        /// <summary>模型列表条目上的连通状态标签。</summary>
        private static string ConnectTag(ModelConfigEntry m)
        {
            string t = string.IsNullOrEmpty(m.LastConnectTimeUtc) ? "" : m.LastConnectTimeUtc;
            if (m.LastConnectOk == true) return "连通✓" + (t.Length > 0 ? " " + t : "");
            if (m.LastConnectOk == false) return "连通✗（不参与调度，重测可恢复）";
            return "未连通测试";
        }

        private ModelItem SelectedModel()
        {
            return ModelListBox.SelectedItem as ModelItem;
        }

        private void ModelListSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ModelActionResult.Text = "";
        }

        private void SaveGlobalsClick(object sender, RoutedEventArgs e)
        {
            if (_config == null) return;
            if (!int.TryParse(TimeoutBox.Text.Trim(), out int timeout) || timeout < 5 || timeout > 600)
            {
                ModelActionResult.Text = "请求超时需为 5-600 的整数秒。";
                return;
            }
            if (!double.TryParse(ThresholdBox.Text.Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double threshold) || threshold <= 0 || threshold > 100)
            {
                ModelActionResult.Text = "校验阈值需为 0-100 的数字（%）。";
                return;
            }
            _config.Mutate(c =>
            {
                c.RequestTimeoutSeconds = timeout;
                c.VerifyThresholdPct = threshold;
            });
            ModelActionResult.Text = "全局设置已保存。";
            Log.Info("UI", $"全局设置保存：超时 {timeout}s，校验阈值 {threshold}%");
        }

        private void AddModelClick(object sender, RoutedEventArgs e)
        {
            var dlg = new ModelEditDialog(_config, null);
            if (dlg.ShowDialog() == true)
            {
                _config.UpsertModel(dlg.Entry);
                RefreshModelList();
                ModelActionResult.Text = $"已添加「{dlg.Entry.Name}」。建议点「探测能力」确认可用。";
                Log.Info("UI", "新增模型配置：" + dlg.Entry.Name);
            }
        }

        private void EditModelClick(object sender, RoutedEventArgs e)
        {
            var item = SelectedModel();
            if (item == null) { ModelActionResult.Text = "请先选中一个模型。"; return; }
            var dlg = new ModelEditDialog(_config, item.Entry);
            if (dlg.ShowDialog() == true)
            {
                _config.UpsertModel(dlg.Entry);
                _probe?.Invalidate(dlg.Entry.Id);
                RefreshModelList();
                ModelActionResult.Text = $"已保存「{dlg.Entry.Name}」（能力缓存已失效，请重新探测）。";
                Log.Info("UI", "模型配置已修改：" + dlg.Entry.Name);
            }
        }

        private void RemoveModelClick(object sender, RoutedEventArgs e)
        {
            var item = SelectedModel();
            if (item == null) { ModelActionResult.Text = "请先选中一个模型。"; return; }
            var answer = MessageBox.Show($"确定删除模型「{item.Entry.Name}」？", "删除模型",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            _config.RemoveModel(item.Entry.Id);
            _probe?.Invalidate(item.Entry.Id);
            RefreshModelList();
            ModelActionResult.Text = $"已删除「{item.Entry.Name}」。";
            Log.Info("UI", "模型配置已删除：" + item.Entry.Name);
        }

        private async void TestModelClick(object sender, RoutedEventArgs e)
        {
            var item = SelectedModel();
            if (item == null) { ModelActionResult.Text = "请先选中一个模型。"; return; }
            var entry = item.Entry;
            ModelActionResult.Text = $"正在测试「{entry.Name}」…";
            try
            {
                var profile = _probe.GetCached(entry.Id, entry.Model);
                var sw = Stopwatch.StartNew();
                using (var client = _probe.CreateClient(entry, profile))
                {
                    string reply = await client.ChatAsync(
                        new List<LlmMessage> { LlmMessage.User("只回复两个字：正常") },
                        new ChatRequestOptions { JsonMode = false, MaxTokens = 16, Temperature = 0 },
                        null, CancellationToken.None).ConfigureAwait(true);
                    sw.Stop();
                    ModelActionResult.Text = $"连通正常（{sw.ElapsedMilliseconds} ms），模型回复：{reply.Trim()}";
                }
                MarkConnect(entry, true);
                Log.Info("UI", $"连通测试通过：{entry.Name}（{sw.ElapsedMilliseconds} ms）");
            }
            catch (Exception ex)
            {
                ModelActionResult.Text = $"连通失败：{ex.Message}";
                MarkConnect(entry, false);
                Log.Warn("UI", $"连通测试失败：{entry.Name}（{ex.Message}）");
            }
        }

        /// <summary>连通测试结果回写模型连通状态并持久化，刷新列表标签。</summary>
        private void MarkConnect(ModelConfigEntry entry, bool ok)
        {
            try
            {
                entry.LastConnectOk = ok;
                entry.LastConnectTimeUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);
                _config.Save();
            }
            catch (Exception ex)
            {
                Log.Warn("UI", "连通状态保存失败：" + ex.Message);
            }
            RefreshModelList();
        }

        private async void ProbeModelClick(object sender, RoutedEventArgs e)
        {
            var item = SelectedModel();
            if (item == null) { ModelActionResult.Text = "请先选中一个模型。"; return; }
            var entry = item.Entry;
            ModelActionResult.Text = $"正在探测「{entry.Name}」能力…";
            try
            {
                var profile = await _probe.ProbeAsync(entry, CancellationToken.None, forceRefresh: true)
                    .ConfigureAwait(true);
                ModelActionResult.Text = $"能力画像：{profile.CapabilityTags()}" +
                    (profile.Source == "probed" ? "" : $"（{profile.Source}：{profile.ProbeError}）");
                Log.Info("UI", $"能力探测完成：{entry.Name} → {profile.CapabilityTags()}");
            }
            catch (Exception ex)
            {
                ModelActionResult.Text = $"探测失败：{ex.Message}";
                Log.Warn("UI", $"能力探测失败：{entry.Name}（{ex.Message}）");
            }
            RefreshModelList();
        }

        // ==================== Ollama 区 ====================

        private async void OllamaRefreshClick(object sender, RoutedEventArgs e)
            => await RefreshOllamaStatusAsync();

        private async Task RefreshOllamaStatusAsync()
        {
            if (_ollamaMgr == null) return;
            try
            {
                OllamaStatus.Text = "检测中…";
                bool alive = await _ollamaMgr.IsAliveAsync().ConfigureAwait(true);
                if (!alive)
                {
                    OllamaStatus.Text = "服务未运行。可点「一键修复」尝试拉起本机 Ollama。";
                    return;
                }
                var models = await OllamaClient.ListInstalledAsync(
                    _ollamaMgr.BaseUrl, _config, CancellationToken.None).ConfigureAwait(true);
                OllamaStatus.Text = $"服务正常（{_ollamaMgr.BaseUrl}），已安装 {models.Count} 个模型：" +
                    (models.Count == 0 ? "无（可点「拉取推荐模型」）" : string.Join("、", models.Take(8)));
            }
            catch (Exception ex)
            {
                OllamaStatus.Text = "检测失败：" + ex.Message;
            }
        }

        private async void OllamaRepairClick(object sender, RoutedEventArgs e)
        {
            OllamaStatus.Text = "修复中…";
            try
            {
                bool ok = await _ollamaMgr.RepairAsync(
                    msg => PostToUi(() => OllamaStatus.Text = msg), CancellationToken.None).ConfigureAwait(true);
                OllamaStatus.Text = ok ? "修复完成，服务正常。" : "修复未成功，请查看日志。";
            }
            catch (Exception ex)
            {
                OllamaStatus.Text = "修复失败：" + ex.Message;
            }
        }

        private async void OllamaPullClick(object sender, RoutedEventArgs e)
        {
            if (_pullCts != null) return;
            _pullCts = new CancellationTokenSource();
            OllamaCancelPullButton.IsEnabled = true;
            OllamaPullProgress.Visibility = Visibility.Visible;
            OllamaPullProgress.Value = 0;
            try
            {
                await _ollamaMgr.PullRecommendedAsync((model, pct, status) => PostToUi(() =>
                {
                    OllamaPullProgress.Value = pct;
                    OllamaPullText.Text = $"{model}：{pct}%（{status}）";
                }), _pullCts.Token).ConfigureAwait(true);
                OllamaPullText.Text = "推荐模型拉取完成。";
                OllamaPullProgress.Value = 100;
            }
            catch (OperationCanceledException)
            {
                OllamaPullText.Text = "已取消拉取。";
            }
            catch (Exception ex)
            {
                OllamaPullText.Text = "拉取失败：" + ex.Message;
                Log.Warn("UI", "Ollama 拉取失败：" + ex.Message);
            }
            finally
            {
                OllamaCancelPullButton.IsEnabled = false;
                _pullCts?.Dispose();
                _pullCts = null;
                await RefreshOllamaStatusAsync();
            }
        }

        private void OllamaCancelPullClick(object sender, RoutedEventArgs e)
        {
            _pullCts?.Cancel();
        }

        // ==================== 计划步骤展示辅助 ====================

        private static string KindName(string kind)
        {
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "sketch": return "草图";
                case "extrudeboss": return "拉伸凸台";
                case "extrudecut": return "拉伸切除";
                case "revolveboss": return "旋转凸台";
                case "revolvecut": return "旋转切除";
                case "fillet": return "圆角";
                case "chamfer": return "倒角";
                case "holewizard": return "孔";
                case "linearpattern": return "线性阵列";
                case "circularpattern": return "圆周阵列";
                case "mirror": return "镜像";
                case "rib": return "筋板";
                case "draft": return "拔模";
                case "shell": return "抽壳";
                case "setmaterial": return "材料";
                default: return kind ?? "?";
            }
        }

        private static string StepDetail(PlanStep step)
        {
            switch ((step.Kind ?? "").ToLowerInvariant())
            {
                case "sketch" when step.Sketch != null:
                    return string.Join("；", step.Sketch.Entities.Select(EntitySummary));
                case "extrudeboss" when step.Extrude != null:
                    return $"盲拉伸 {Fmt(step.Extrude.DepthMm)} mm";
                case "extrudecut" when step.Extrude != null:
                    return step.Extrude.ThroughAll ? "双向贯穿切除" : $"盲切深 {Fmt(step.Extrude.DepthMm)} mm";
                case "fillet" when step.Fillet != null:
                    return $"R{Fmt(step.Fillet.RadiusMm)}，目标 {step.Fillet.Target}";
                case "chamfer" when step.Chamfer != null:
                    return $"C{Fmt(step.Chamfer.DistanceMm)}，目标 {step.Chamfer.Target}";
                case "holewizard" when step.Hole != null:
                {
                    var h = step.Hole;
                    string s = $"φ{Fmt(h.DiameterMm)}，{(h.ThroughAll ? "通孔" : "深 " + Fmt(h.DepthMm) + " mm")}，{h.Positions?.Count ?? 0} 个位置";
                    if (h.CboreDiameterMm != null)
                    {
                        s += $"，沉孔 φ{Fmt(h.CboreDiameterMm)}×{Fmt(h.CboreDepthMm)}";
                    }
                    return s;
                }
                case "linearpattern" when step.Pattern != null:
                    return $"源 {step.Pattern.SourceStepId}，方向 {step.Pattern.Direction}，间距 {Fmt(step.Pattern.SpacingMm)} × {step.Pattern.Count}";
                case "circularpattern" when step.Pattern != null:
                    return $"源 {step.Pattern.SourceStepId}，总角 {Fmt(step.Pattern.TotalAngleDeg)}° × {step.Pattern.Count}";
                case "mirror" when step.Mirror != null:
                    return $"源 {step.Mirror.SourceStepId}，镜像面 {step.Mirror.Plane}";
                case "shell" when step.Shell != null:
                    return $"壁厚 {Fmt(step.Shell.ThicknessMm)} mm，移除面 {step.Shell.RemoveFace}";
                case "setmaterial" when step.SetMaterial != null:
                    return step.SetMaterial.MaterialName;
                default:
                    return null;
            }
        }

        private static string EntitySummary(SketchEntity e)
        {
            switch ((e.Type ?? "").ToLowerInvariant())
            {
                case "rectcenter":
                    return $"中心矩形 {Fmt(e.Width)}×{Fmt(e.Height)} @({Fmt(e.Cx)},{Fmt(e.Cy)})";
                case "rectcorner":
                    return $"矩形 ({Fmt(e.X1)},{Fmt(e.Y1)})-({Fmt(e.X2)},{Fmt(e.Y2)})";
                case "circle":
                    return $"圆 φ{Fmt(e.Diameter ?? e.Radius * 2)} @({Fmt(e.Cx)},{Fmt(e.Cy)})";
                case "slot":
                    return $"槽口 长{Fmt(e.Length)}×宽{Fmt(e.Width)} @({Fmt(e.Cx)},{Fmt(e.Cy)})";
                case "polygon":
                    return $"{e.Sides} 边形 外接φ{Fmt(e.CircumDiameter)}";
                case "polyline":
                    return $"轮廓 {e.Points?.Count ?? 0} 点{(e.Closed == true ? "闭合" : "")}";
                default:
                    return e.Type;
            }
        }

        private static string Fmt(double? v)
            => v?.ToString("0.##", CultureInfo.InvariantCulture) ?? "-";

        private static bool TryDouble(string v, out double d)
        {
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return true;
            return double.TryParse(v, NumberStyles.Float, CultureInfo.CurrentCulture, out d);
        }

        private static SolidColorBrush BrushOf(string hex)
            => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        private void PostToUi(Action action)
        {
            if (Dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                Dispatcher.BeginInvoke(action);
            }
        }

        // ==================== 日志页 ====================

        private const int LogTailLines = 300;

        /// <summary>读取今日日志末尾 N 行显示（文件可能被本进程写入，用 ReadWrite 共享打开）。</summary>
        private void LoadLogTail()
        {
            try
            {
                AppPaths.Ensure();
                string file = Path.Combine(AppPaths.Logs,
                    "app-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                if (!File.Exists(file))
                {
                    LogTextBox.Text = "今日暂无日志。\r\n日志目录：" + AppPaths.Logs;
                    return;
                }
                string[] lines;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fs, Encoding.UTF8))
                {
                    lines = reader.ReadToEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                }
                var tail = lines.Skip(Math.Max(0, lines.Length - LogTailLines));
                LogTextBox.Text = string.Join("\r\n", tail);
                LogTextBox.ScrollToEnd();
                StatusText.Text = $"日志已刷新（今日共 {lines.Length} 行，显示末 {Math.Min(LogTailLines, lines.Length)} 行）";
            }
            catch (Exception ex)
            {
                LogTextBox.Text = "读取日志失败：" + ex.Message;
            }
        }

        private void LogRefreshClick(object sender, RoutedEventArgs e) => LoadLogTail();

        private void LogOpenFolderClick(object sender, RoutedEventArgs e)
        {
            try
            {
                AppPaths.Ensure();
                Process.Start(new ProcessStartInfo(AppPaths.Logs) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                StatusText.Text = "打开日志目录失败：" + ex.Message;
            }
        }

        /// <summary>打包全部日志为 ZIP 另存，便于反馈问题时附带。</summary>
        private void LogExportClick(object sender, RoutedEventArgs e)
        {
            try
            {
                AppPaths.Ensure();
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "导出插件运行日志",
                    Filter = "ZIP 压缩包 (*.zip)|*.zip",
                    FileName = "SwAiAssistant-logs-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".zip"
                };
                if (dialog.ShowDialog() != true) return;

                string staging = Path.Combine(Path.GetTempPath(), "SwAiAssistantLogExport",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                foreach (var log in Directory.GetFiles(AppPaths.Logs, "app-*.log"))
                {
                    string copy = Path.Combine(staging, Path.GetFileName(log));
                    using (var src = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var dst = new FileStream(copy, FileMode.Create, FileAccess.Write))
                    {
                        src.CopyTo(dst);
                    }
                }
                if (File.Exists(dialog.FileName)) File.Delete(dialog.FileName);
                ZipFile.CreateFromDirectory(staging, dialog.FileName);
                Directory.Delete(staging, true);

                StatusText.Text = "日志已导出：" + dialog.FileName;
                Log.Info("UI", "日志已导出 ZIP：" + dialog.FileName);
            }
            catch (Exception ex)
            {
                StatusText.Text = "导出日志失败：" + ex.Message;
                Log.Error("UI", "导出日志 ZIP 失败", ex);
            }
        }

        /// <summary>切到日志页/快照页时自动刷新一次。</summary>
        private void RootTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source != RootTabs) return;
            if (RootTabs.SelectedItem == LogTab)
            {
                LoadLogTail();
            }
            else if (RootTabs.SelectedItem == SnapshotTab)
            {
                RefreshSnapshotList();
            }
        }
    }
}
