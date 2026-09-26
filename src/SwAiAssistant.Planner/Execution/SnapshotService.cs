using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using SolidWorks.Interop.sldworks;
using SwAiAssistant.Cad;
using SwAiAssistant.Cad.Documents;
using SwAiAssistant.Cad.Features;
using SwAiAssistant.Cad.Materials;
using SwAiAssistant.Cad.Queries;
using SwAiAssistant.Core.Configuration;
using SwAiAssistant.Core.Logging;

namespace SwAiAssistant.Planner.Execution
{
    /// <summary>单个 AI 特征的快照记录（名/类型/关联草图）。</summary>
    public sealed class SnapshotFeature
    {
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public string SketchName { get; set; }
    }

    /// <summary>单个受关注尺寸的快照记录（全名 + 毫米值）。</summary>
    public sealed class SnapshotDimension
    {
        public string FullName { get; set; } = "";
        public double ValueMm { get; set; }
    }

    /// <summary>
    /// 一次 AI 执行前的零件状态快照（M4-T16 一键回滚数据源）。
    /// 记录：AI 特征清单（名/类型/顺序）、全部受关注尺寸全名与值、材料、全特征顺序（冲突检测用）。
    /// </summary>
    public sealed class SnapshotData
    {
        public string Id { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }
        public string DocTitle { get; set; } = "";
        public string CommandSummary { get; set; } = "";
        public List<SnapshotFeature> AiFeatures { get; set; } = new List<SnapshotFeature>();
        public List<SnapshotDimension> Dimensions { get; set; } = new List<SnapshotDimension>();
        public string MaterialName { get; set; } = "";
        public double DensityKgM3 { get; set; }
        /// <summary>快照时特征树全部特征名（建模顺序，含非 AI），回滚冲突检测用。</summary>
        public List<string> AllFeatureNames { get; set; } = new List<string>();
    }

    /// <summary>回滚结果报告（删除/恢复/冲突/备注）。</summary>
    public sealed class RollbackReport
    {
        public List<string> DeletedFeatures { get; } = new List<string>();
        public List<string> RestoredDimensions { get; } = new List<string>();
        public string RestoredMaterial { get; set; } = "";
        public List<string> Conflicts { get; } = new List<string>();
        public List<string> Notes { get; } = new List<string>();
        /// <summary>回滚前自记快照 Id（可再次回滚到回滚前状态）。</summary>
        public string PreSnapshotId { get; set; } = "";
    }

    /// <summary>
    /// 快照与一键回滚（M4-T16）：
    /// 每次 AI 执行前 Capture 记录零件状态并随会话存盘（%AppData%\SwAiAssistant\snapshots）；
    /// Rollback 删除快照后新增的 AI 特征、恢复尺寸值与材料，回滚前自记一条快照，
    /// 用户在快照后手工新增的非 AI 特征触发冲突提示（不删除，防误删）。
    /// 红线：不执行任何模型生成代码，仅做确定性 COM 调用。
    /// </summary>
    public sealed class SnapshotService
    {
        private readonly QueryService _query;
        private readonly DimensionService _dims;
        private readonly MaterialService _materials;
        private readonly FeatureRegistry _registry;
        private readonly DocService _docs;
        private readonly FeatureService _features;

        public SnapshotService(QueryService query, DimensionService dims,
            MaterialService materials, FeatureRegistry registry,
            DocService docs, FeatureService features)
        {
            _query = query ?? throw new ArgumentNullException(nameof(query));
            _dims = dims ?? throw new ArgumentNullException(nameof(dims));
            _materials = materials ?? throw new ArgumentNullException(nameof(materials));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _docs = docs ?? throw new ArgumentNullException(nameof(docs));
            _features = features ?? throw new ArgumentNullException(nameof(features));
        }

        /// <summary>
        /// 捕获当前零件快照并存盘，返回快照数据。commandSummary 为触发本次 AI 执行的指令摘要。
        /// 单项读取失败仅记日志不中断（快照尽力而为，不因个别尺寸/材料读取失败而丢失整体）。
        /// </summary>
        public SnapshotData Capture(IModelDoc2 doc, string commandSummary)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            var snap = new SnapshotData
            {
                Id = NewId(),
                CreatedAtUtc = DateTime.UtcNow,
                DocTitle = _docs.GetTitle(doc),
                CommandSummary = commandSummary ?? ""
            };

            // 特征清单（建模顺序）+ AI 特征元数据
            try
            {
                snap.AllFeatureNames.AddRange(_query.GetFeatureNames(doc));
                foreach (string name in snap.AllFeatureNames)
                {
                    if (!_registry.IsAiFeature(name)) continue;
                    var rec = _registry.Find(name);
                    snap.AiFeatures.Add(new SnapshotFeature
                    {
                        Name = name,
                        Kind = rec?.Kind ?? "",
                        SketchName = rec?.SketchName
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Snapshot", "特征清单捕获失败：" + ex.Message);
            }

            // 受关注尺寸（全部显示尺寸全名 + 毫米值）
            try
            {
                foreach (var d in _dims.ListDimensions(doc))
                {
                    if (string.IsNullOrWhiteSpace(d.FullName)) continue;
                    snap.Dimensions.Add(new SnapshotDimension { FullName = d.FullName, ValueMm = d.ValueMm });
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Snapshot", "尺寸捕获失败：" + ex.Message);
            }

            // 注册表登记的关联尺寸兜底：ListDimensions 枚举（DisplayDimension 反射路径）真机不可用时，
            // 仍按登记全名直读当前值入快照，保证回滚可恢复关键尺寸（如拉伸深度 D1@AI__板）
            try
            {
                var known = new HashSet<string>(snap.Dimensions.Select(d => d.FullName), StringComparer.Ordinal);
                foreach (var rec in _registry.All())
                {
                    foreach (string fullName in rec.DimensionFullNames)
                    {
                        if (string.IsNullOrWhiteSpace(fullName) || known.Contains(fullName)) continue;
                        try
                        {
                            double v = _dims.GetDimensionMm(doc, fullName);
                            snap.Dimensions.Add(new SnapshotDimension { FullName = fullName, ValueMm = v });
                            known.Add(fullName);
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("Snapshot", $"登记尺寸「{fullName}」读取失败（跳过）：" + ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Snapshot", "登记尺寸捕获失败：" + ex.Message);
            }

            // 材料
            try { snap.MaterialName = _materials.GetCurrentMaterialName(doc); }
            catch (Exception ex) { Log.Warn("Snapshot", "材料名捕获失败：" + ex.Message); }
            try { snap.DensityKgM3 = _materials.GetEffectiveDensityKgM3(doc, MaterialService.GbMaterials[0]); }
            catch (Exception ex) { Log.Warn("Snapshot", "密度捕获失败：" + ex.Message); }

            Save(snap);
            Log.Info("Snapshot", $"快照已捕获：{snap.Id}（AI 特征 {snap.AiFeatures.Count}，尺寸 {snap.Dimensions.Count}，材料「{snap.MaterialName}」）→ {PathFor(snap)}");
            return snap;
        }

        /// <summary>列出某零件的全部存盘快照（按时间倒序，最新在前）。</summary>
        public List<SnapshotData> List(string docTitle)
        {
            var list = new List<SnapshotData>();
            string dir = DirFor(docTitle);
            if (!Directory.Exists(dir)) return list;
            foreach (string file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var snap = JsonConvert.DeserializeObject<SnapshotData>(File.ReadAllText(file));
                    if (snap != null && !string.IsNullOrEmpty(snap.Id)) list.Add(snap);
                }
                catch (Exception ex)
                {
                    Log.Warn("Snapshot", $"快照文件读取失败（跳过）{Path.GetFileName(file)}：{ex.Message}");
                }
            }
            return list.OrderByDescending(s => s.CreatedAtUtc).ToList();
        }

        /// <summary>按 Id 加载某零件的快照；不存在返回 null。</summary>
        public SnapshotData Load(string docTitle, string snapshotId)
        {
            if (string.IsNullOrWhiteSpace(snapshotId)) return null;
            string file = Path.Combine(DirFor(docTitle), Sanitize(snapshotId) + ".json");
            if (!File.Exists(file)) return null;
            try
            {
                return JsonConvert.DeserializeObject<SnapshotData>(File.ReadAllText(file));
            }
            catch (Exception ex)
            {
                Log.Warn("Snapshot", $"快照加载失败 {snapshotId}：{ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 一键回滚到指定快照：
        /// ① 回滚前先 Capture 当前状态（自记快照，可反悔）；② 删除快照后新增的 AI 特征；
        /// ③ 用户手工新增的非 AI 特征记入冲突（不删除）；④ 恢复尺寸值；⑤ 恢复材料。
        /// </summary>
        public RollbackReport Rollback(IModelDoc2 doc, string snapshotId)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            string docTitle = _docs.GetTitle(doc);
            var target = Load(docTitle, snapshotId);
            if (target == null)
            {
                throw new CadException($"快照「{snapshotId}」不存在（文档「{docTitle}」）。");
            }

            var report = new RollbackReport();

            // ① 回滚前自记快照（当前状态），可随时再回滚回来
            var pre = Capture(doc, "回滚前自动快照（→ " + snapshotId + "）");
            report.PreSnapshotId = pre.Id;

            // ②③ 特征对比：当前特征 vs 快照特征
            var current = _query.GetFeatureNames(doc).ToList();
            var targetSet = new HashSet<string>(target.AllFeatureNames, StringComparer.Ordinal);
            var toDelete = current.Where(n => _registry.IsAiFeature(n) && !targetSet.Contains(n)).ToList();
            var userAdded = current.Where(n => !_registry.IsAiFeature(n) && !targetSet.Contains(n)).ToList();
            foreach (string n in userAdded)
            {
                report.Conflicts.Add($"快照后手工新增的特征「{n}」未删除（非 AI 特征，防误删）。请确认是否保留。");
            }

            // 删除快照后新增的 AI 特征（先删靠后的，避免顺序依赖；逐个选中删除）
            foreach (string n in toDelete)
            {
                try
                {
                    DeleteFeature(doc, n);
                    _registry.Remove(n);
                    report.DeletedFeatures.Add(n);
                }
                catch (Exception ex)
                {
                    Log.Warn("Snapshot", $"删除特征「{n}」失败：" + ex.Message);
                    report.Notes.Add($"删除特征「{n}」失败：{ex.Message}");
                }
            }

            // 快照里存在但当前缺失的 AI 特征（用户已手工删除）：MVP 不重建，仅提示
            foreach (var f in target.AiFeatures)
            {
                if (!current.Contains(f.Name))
                {
                    report.Notes.Add($"快照时的 AI 特征「{f.Name}」当前已不存在（可能已被手工删除），未自动重建。");
                }
            }

            // ④ 恢复尺寸值（当前值与快照值不同才写，SetDimensionMm 自带重建）
            foreach (var dim in target.Dimensions)
            {
                if (string.IsNullOrWhiteSpace(dim.FullName) || dim.ValueMm <= 0) continue;
                try
                {
                    double now = _dims.GetDimensionMm(doc, dim.FullName);
                    if (Math.Abs(now - dim.ValueMm) < 1e-6) continue; // 已是目标值
                    _dims.SetDimensionMm(doc, dim.FullName, dim.ValueMm);
                    report.RestoredDimensions.Add($"{dim.FullName}: {now:0.###}→{dim.ValueMm:0.###} mm");
                }
                catch (Exception ex)
                {
                    Log.Warn("Snapshot", $"恢复尺寸「{dim.FullName}」失败：" + ex.Message);
                    report.Notes.Add($"恢复尺寸「{dim.FullName}」失败：{ex.Message}");
                }
            }

            // ⑤ 恢复材料（目标材料名非空且与当前不同才赋）
            if (!string.IsNullOrWhiteSpace(target.MaterialName))
            {
                try
                {
                    string nowMat = _materials.GetCurrentMaterialName(doc);
                    if (!string.Equals(nowMat, target.MaterialName, StringComparison.OrdinalIgnoreCase))
                    {
                        var mat = _materials.FindByNameOrAlias(target.MaterialName);
                        if (mat != null)
                        {
                            _materials.ApplyMaterial(doc, mat);
                            report.RestoredMaterial = mat.Name;
                        }
                        else
                        {
                            report.Notes.Add($"快照材料「{target.MaterialName}」未匹配 GB 材料库，未恢复。");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("Snapshot", "恢复材料失败：" + ex.Message);
                    report.Notes.Add("恢复材料失败：" + ex.Message);
                }
            }

            try { _docs.ForceRebuild(doc, false); }
            catch (Exception ex) { Log.Warn("Snapshot", "回滚后最终重建失败（忽略）：" + ex.Message); }
            Log.Info("Snapshot", $"回滚完成 → {snapshotId}：删 {report.DeletedFeatures.Count} 特征，"
                + $"恢复 {report.RestoredDimensions.Count} 尺寸，材料「{report.RestoredMaterial}」，冲突 {report.Conflicts.Count}");
            return report;
        }

        /// <summary>删除单个 AI 特征（经 Cad 层 FeatureService 包装，内部 STA 封送）。</summary>
        private void DeleteFeature(IModelDoc2 doc, string featureName)
            => _features.DeleteFeature(doc, featureName);

        // ---- 存盘 ----

        private void Save(SnapshotData snap)
        {
            string file = PathFor(snap);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, JsonConvert.SerializeObject(snap, Formatting.Indented));
        }

        private string PathFor(SnapshotData snap) => Path.Combine(DirFor(snap.DocTitle), Sanitize(snap.Id) + ".json");

        private static string DirFor(string docTitle) => Path.Combine(AppPaths.Snapshots, Sanitize(docTitle ?? "<未知>"));

        private static string NewId() => DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);

        /// <summary>文件名安全化：替换路径非法字符。</summary>
        private static string Sanitize(string name)
        {
            var chars = name.Trim().Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray();
            return new string(chars);
        }
    }
}
