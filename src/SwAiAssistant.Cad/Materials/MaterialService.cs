using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SwAiAssistant.Cad.Session;
using SwAiAssistant.Core.Logging;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SwAiAssistant.Cad.Materials
{
    /// <summary>GB 常用材料定义（密度 kg/m³、弹性模量 GPa）。</summary>
    public sealed class MaterialDef
    {
        public string Name { get; set; } = "";
        public string[] Aliases { get; set; } = new string[0];
        public double DensityKgM3 { get; set; }
        public double ElasticModulusGPa { get; set; }
    }

    /// <summary>
    /// GB 材料库与自动猜材（M3-T15）：
    /// 内置 ≥12 种 GB 常用材料的名称/别名/密度/弹性模量；
    /// 支持自然语言模糊匹配（「做一块铝板」→ 6061）；
    /// 赋材料优先走 SW 材料库（SetMaterialPropertyName2，COM 签名已经 redist 反射确认），
    /// 失败时保底写入文档自定义属性 AI_Density 名义密度，供质量估算回读。
    /// 全程经 SwSession.OnSta 封送到 STA 线程。
    /// </summary>
    public sealed class MaterialService
    {
        /// <summary>记录名义密度的文档自定义属性键（GetEffectiveDensityKgM3 回读源）。</summary>
        public const string DensityPropKey = "AI_Density";
        /// <summary>记录已赋材料名的文档自定义属性键。</summary>
        public const string MaterialPropKey = "AI_Material";

        private readonly SwSession _session;

        public MaterialService(SwSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        /// <summary>
        /// GB 常用材料表。顺序即模糊匹配优先级：
        /// 通用词（铝/不锈钢/铜/铸铁）指向排在前面的最常见牌号。
        /// </summary>
        public static IReadOnlyList<MaterialDef> GbMaterials { get; } = new List<MaterialDef>
        {
            new MaterialDef { Name = "Q235", DensityKgM3 = 7850, ElasticModulusGPa = 200,
                Aliases = new[] { "Q235", "Q235A", "Q235B", "A3钢", "碳素结构钢" } },
            new MaterialDef { Name = "Q345", DensityKgM3 = 7850, ElasticModulusGPa = 200,
                Aliases = new[] { "Q345", "Q345B", "Q355", "16Mn", "低合金钢" } },
            new MaterialDef { Name = "45钢", DensityKgM3 = 7850, ElasticModulusGPa = 200,
                Aliases = new[] { "45钢", "45", "45#", "45号钢", "S45C", "优质碳素钢" } },
            new MaterialDef { Name = "40Cr", DensityKgM3 = 7870, ElasticModulusGPa = 211,
                Aliases = new[] { "40Cr", "40铬", "合金结构钢" } },
            new MaterialDef { Name = "HT200", DensityKgM3 = 7200, ElasticModulusGPa = 148,
                Aliases = new[] { "HT200", "灰铸铁200", "灰铁200", "灰铸铁", "灰铁", "铸铁" } },
            new MaterialDef { Name = "HT250", DensityKgM3 = 7280, ElasticModulusGPa = 155,
                Aliases = new[] { "HT250", "灰铸铁250", "灰铁250" } },
            new MaterialDef { Name = "6061铝合金", DensityKgM3 = 2700, ElasticModulusGPa = 68.9,
                Aliases = new[] { "6061铝合金", "6061", "铝板", "铝合金", "铝材", "铝", "aluminum" } },
            new MaterialDef { Name = "6063铝合金", DensityKgM3 = 2690, ElasticModulusGPa = 68.3,
                Aliases = new[] { "6063铝合金", "6063" } },
            new MaterialDef { Name = "纯铝1060", DensityKgM3 = 2710, ElasticModulusGPa = 69,
                Aliases = new[] { "纯铝1060", "1060", "纯铝" } },
            new MaterialDef { Name = "铸铝ZL102", DensityKgM3 = 2650, ElasticModulusGPa = 71,
                Aliases = new[] { "铸铝ZL102", "ZL102", "铸铝" } },
            new MaterialDef { Name = "304不锈钢", DensityKgM3 = 7900, ElasticModulusGPa = 193,
                Aliases = new[] { "304不锈钢", "304", "不锈钢", "SUS304", "不锈" } },
            new MaterialDef { Name = "316不锈钢", DensityKgM3 = 7950, ElasticModulusGPa = 193,
                Aliases = new[] { "316不锈钢", "316", "316L", "SUS316" } },
            new MaterialDef { Name = "H62黄铜", DensityKgM3 = 8430, ElasticModulusGPa = 105,
                Aliases = new[] { "H62黄铜", "H62", "黄铜", "铜板", "铜" } },
            new MaterialDef { Name = "H59黄铜", DensityKgM3 = 8400, ElasticModulusGPa = 105,
                Aliases = new[] { "H59黄铜", "H59" } },
            new MaterialDef { Name = "尼龙66", DensityKgM3 = 1140, ElasticModulusGPa = 3.3,
                Aliases = new[] { "尼龙66", "尼龙", "PA66", "PA" } },
            new MaterialDef { Name = "ABS", DensityKgM3 = 1050, ElasticModulusGPa = 2.3,
                Aliases = new[] { "ABS", "ABS塑料", "塑料" } },
        };

        /// <summary>
        /// 按名/别名/关键字模糊匹配（大小写不敏感、去空格）。未匹配返回 null。
        /// 优先级：精确（名或别名整体相等）→ 查询串包含关键词（取最长命中，如「做一块铝板」含「铝板」）→ 关键词包含查询串。
        /// </summary>
        public MaterialDef FindByNameOrAlias(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;
            string q = Normalize(query);

            // 1) 精确匹配
            foreach (var m in GbMaterials)
            {
                if (Normalize(m.Name) == q || m.Aliases.Any(a => Normalize(a) == q))
                {
                    return m;
                }
            }

            // 2) 查询串包含关键词（自然语言整句）：最长命中词优先，避免「铝」抢掉「铝板」
            MaterialDef best = null;
            int bestLen = 0;
            foreach (var m in GbMaterials)
            {
                foreach (string kw in m.Aliases)
                {
                    string k = Normalize(kw);
                    if (k.Length > bestLen && q.Contains(k))
                    {
                        best = m;
                        bestLen = k.Length;
                    }
                }
            }
            if (best != null) return best;

            // 3) 反向包含：关键词包含查询串（如查询「Q235A」由「Q235」命中已在 1) 覆盖；
            //    这里兜住查询更短的情形，如「6061铝」拆不出时的残片）
            foreach (var m in GbMaterials)
            {
                if (Normalize(m.Name).Contains(q) || m.Aliases.Any(a => Normalize(a).Contains(q)))
                {
                    return m;
                }
            }
            return null;
        }

        /// <summary>从自然语言（「做一块铝板」「用不锈钢」）猜测材料，内部即 FindByNameOrAlias 的关键词包含匹配。</summary>
        public MaterialDef Guess(string freeText)
        {
            return FindByNameOrAlias(freeText);
        }

        /// <summary>
        /// 赋材料：优先经 SW 材料库 SetMaterialPropertyName2(配置, 库路径, 材料名) 并回读校验；
        /// 库路径不可用/回读不符时保底——Log.Warn 并把名义密度写入文档自定义属性 AI_Density。
        /// 注意：SW 库内材料显示名与 GB 牌号的映射（如「45钢」→ 库中名称）待真机确认。
        /// </summary>
        public void ApplyMaterial(IModelDoc2 doc, MaterialDef mat)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (mat == null) throw new ArgumentNullException(nameof(mat));

            _session.OnSta<object>(() =>
            {
                bool applied = false;
                string lastErr = null;
                if (doc is IPartDoc part)
                {
                    foreach (string db in CandidateMaterialDbs())
                    {
                        try
                        {
                            // COM 签名经 SW2026 redist 反射确认：SetMaterialPropertyName2(String, String, String)
                            part.SetMaterialPropertyName2("", db, mat.Name);
                            string back = part.GetMaterialPropertyName2("", out string backDb);
                            bool match = !string.IsNullOrEmpty(back)
                                && (back.IndexOf(mat.Name, StringComparison.OrdinalIgnoreCase) >= 0
                                    || mat.Aliases.Any(a => a.Length >= 2
                                        && back.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0));
                            if (match)
                            {
                                applied = true;
                                Log.Info("Cad", $"已赋材料「{back}」（库：{backDb}）");
                                break;
                            }
                            lastErr = "回读材料名「" + (back ?? "<空>") + "」与「" + mat.Name + "」不符";
                        }
                        catch (Exception ex)
                        {
                            lastErr = ex.Message;
                        }
                    }
                }
                if (!applied)
                {
                    Log.Warn("Cad", "材料库路径不可用，仅记录名义密度到自定义属性 AI_Density"
                        + (lastErr != null ? "（" + lastErr + "）" : "。"));
                }

                // 无论材料库是否命中，都写入名义密度/材料名自定义属性（质量估算回读源）
                SetCustomText(doc, DensityPropKey,
                    mat.DensityKgM3.ToString("0.###", CultureInfo.InvariantCulture));
                SetCustomText(doc, MaterialPropKey, mat.Name);
                return null;
            });
        }

        /// <summary>
        /// 回读当前材料名（快照记录用，M4-T16）：优先文档自定义属性 AI_Material（ApplyMaterial 写入），
        /// 取不到时读 SW 材料库属性 GetMaterialPropertyName2。均无返回 ""。
        /// </summary>
        public string GetCurrentMaterialName(IModelDoc2 doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            return _session.OnSta(() =>
            {
                try
                {
                    string custom = doc.get_CustomInfo2("", MaterialPropKey);
                    if (!string.IsNullOrWhiteSpace(custom))
                    {
                        return custom.Trim();
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("Cad", "读取 AI_Material 自定义属性失败：" + ex.Message);
                }
                try
                {
                    if (doc is IPartDoc part)
                    {
                        string back = part.GetMaterialPropertyName2("", out _);
                        if (!string.IsNullOrWhiteSpace(back))
                        {
                            return back.Trim();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("Cad", "读取 SW 材料库属性失败：" + ex.Message);
                }
                return "";
            });
        }

        /// <summary>
        /// 回读当前生效密度（kg/m³）：先读文档自定义属性 AI_Density，取不到用 fallback.DensityKgM3。
        /// </summary>
        public double GetEffectiveDensityKgM3(IModelDoc2 doc, MaterialDef fallback)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (fallback == null) throw new ArgumentNullException(nameof(fallback));

            return _session.OnSta(() =>
            {
                try
                {
                    string raw = doc.get_CustomInfo2("", DensityPropKey);
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        // 统一按固定区域解析（写入时用 InvariantCulture）；再按当前区域兜一次
                        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0)
                        {
                            return v;
                        }
                        if (double.TryParse(raw, out v) && v > 0)
                        {
                            return v;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("Cad", "读取 AI_Density 自定义属性失败：" + ex.Message);
                }
                return fallback.DensityKgM3;
            });
        }

        /// <summary>写文档自定义属性（已存在时覆盖）。单个属性失败仅告警，不阻断赋材料流程。</summary>
        private static void SetCustomText(IModelDoc2 doc, string key, string value)
        {
            try
            {
                // COM 签名经 redist 反射确认：AddCustomInfo3(配置, 键, swCustomInfoType_e, 值) → bool
                if (!doc.AddCustomInfo3("", key, (int)swCustomInfoType_e.swCustomInfoText, value))
                {
                    doc.set_CustomInfo2("", key, value);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Cad", $"写自定义属性 {key} 失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 候选 SW 材料数据库（.sldmat）路径：默认安装位置按存在性过滤。
        /// 材料库真实位置（含自定义库）待真机确认；一个都找不到时返回空串占位，
        /// 让 SetMaterialPropertyName2 走 SW 默认库再回读判定。
        /// </summary>
        private static string[] CandidateMaterialDbs()
        {
            var candidates = new List<string>();
            string progFiles = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles);
            string def = Path.Combine(progFiles, "SOLIDWORKS Corp", "SOLIDWORKS", "data",
                "materials", "solidworks materials.sldmat");
            if (File.Exists(def)) candidates.Add(def);
            string progData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData);
            foreach (string dir in new[] { "SOLIDWORKS", "SolidWorks" })
            {
                string root = Path.Combine(progData, dir);
                if (!Directory.Exists(root)) continue;
                try
                {
                    candidates.AddRange(Directory.GetFiles(root, "*.sldmat", SearchOption.AllDirectories));
                }
                catch { /* 无权限/路径异常忽略 */ }
            }
            candidates.Add(""); // 兜底：交给 SW 默认库
            return candidates.Distinct().ToArray();
        }

        /// <summary>匹配归一化：去空格、小写。</summary>
        private static string Normalize(string s)
        {
            return (s ?? "").Replace(" ", "").Replace("　", "").ToLowerInvariant();
        }
    }
}
