using System;
using System.Collections.Generic;
using System.Linq;
using SwAiAssistant.Cad.Session;
using SwAiAssistant.Core.Logging;
using SolidWorks.Interop.sldworks;

namespace SwAiAssistant.Cad.Queries
{
    /// <summary>单个尺寸参数回读（毫米）。</summary>
    public sealed class DimensionInfo
    {
        /// <summary>尺寸全名（如 "D1@草图1"、"D1@凸台-拉伸1"），IModelDoc2.Parameter 寻址用。</summary>
        public string FullName { get; set; } = "";

        /// <summary>当前值（毫米）。</summary>
        public double ValueMm { get; set; }

        /// <summary>枚举来源特征名（便于按特征过滤）。</summary>
        public string FeatureName { get; set; } = "";
    }

    /// <summary>
    /// 尺寸参数读写服务：对话式修改「改孔径 6→8」「加厚 10→12」的核心原语（T14）。
    /// 全名寻址：IModelDoc2.Parameter("D1@草图1") → IDimension（COM 已验证存在）。
    /// 单位约定：对外一律毫米；内部 IDimension.SystemValue 为米（MKS）。
    ///   （TR-5.1 实测：IDimension.Value 非米而是用户显示单位原值，改尺寸一律用 SystemValue。）
    /// </summary>
    public sealed class DimensionService
    {
        private readonly SwSession _session;

        public DimensionService(SwSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        /// <summary>供上层（如 EditPlanner 删除特征）在同一 STA 封送内组合多次 COM 调用。</summary>
        public T OnSta<T>(Func<T> work) => _session.OnSta(work);

        /// <summary>按全名读尺寸（毫米）。全名形如 "D1@草图1"、"D1@凸台-拉伸1"。</summary>
        public double GetDimensionMm(IModelDoc2 doc, string fullName)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (string.IsNullOrWhiteSpace(fullName)) throw new ArgumentNullException(nameof(fullName));
            return _session.OnSta(() =>
            {
                try
                {
                    IDimension dim = doc.Parameter(fullName) as IDimension;
                    if (dim == null)
                    {
                        throw new CadException($"尺寸「{fullName}」未找到：确认全名（D1@特征名）拼写与当前文档。");
                    }
                    return Units.MToMm(dim.SystemValue);
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom($"读取尺寸「{fullName}」", ex); }
            });
        }

        /// <summary>
        /// 按全名写尺寸（毫米）并强制重建。
        /// 最小修改语义（TR-14.1）：只写这一个尺寸参数 + 一次 ForceRebuild3，
        /// 不触碰任何其他特征 API——只重建该尺寸驱动的特征，不重建非相关特征。
        /// </summary>
        public void SetDimensionMm(IModelDoc2 doc, string fullName, double valueMm)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (string.IsNullOrWhiteSpace(fullName)) throw new ArgumentNullException(nameof(fullName));
            if (valueMm <= 0) throw new ArgumentOutOfRangeException(nameof(valueMm), "尺寸值必须为正。");
            _session.OnSta<object>(() =>
            {
                try
                {
                    IDimension dim = doc.Parameter(fullName) as IDimension;
                    if (dim == null)
                    {
                        throw new CadException($"尺寸「{fullName}」未找到：确认全名（D1@特征名）拼写与当前文档。");
                    }
                    dim.SystemValue = Units.MmToM(valueMm);
                    doc.ForceRebuild3(false);
                    Log.Info("Cad", $"尺寸修改完成：「{fullName}」= {valueMm} mm");
                    return null;
                }
                catch (CadException) { throw; }
                catch (Exception ex) { throw CadException.FromCom($"写入尺寸「{fullName}」", ex); }
            });
        }

        /// <summary>
        /// 枚举零件全部显示尺寸：遍历特征树，逐特征取 DisplayDimension → IDimension。
        /// COM 签名待真机确认：IFeature.GetFirstDisplayDimension()/GetNextDisplayDimension(disp)
        ///   与 IDimension.FullName（部分版本/配置需 GetNameForSelection 替代）——
        ///   逐特征 try/catch 降级，单个特征失败不中断整体枚举。
        /// </summary>
        public List<DimensionInfo> ListDimensions(IModelDoc2 doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            return _session.OnSta(() =>
            {
                var list = new List<DimensionInfo>();
                try
                {
                    IFeature feat = doc.FirstFeature() as IFeature;
                    while (feat != null)
                    {
                        try
                        {
                            EnumerateFeatureDimensions(feat, list);
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("Cad", $"特征「{SafeName(feat)}」尺寸枚举失败（跳过）：" + ex.Message);
                        }
                        feat = feat.GetNextFeature() as IFeature;
                    }
                }
                catch (Exception ex) { throw CadException.FromCom("枚举尺寸", ex); }
                return list;
            });
        }

        /// <summary>逐特征枚举 DisplayDimension（COM 签名待真机确认，失败由调用方捕获跳过）。</summary>
        private static void EnumerateFeatureDimensions(IFeature feat, List<DimensionInfo> list)
        {
            string featName = SafeName(feat);
            IDisplayDimension disp = feat.GetFirstDisplayDimension() as IDisplayDimension;
            while (disp != null)
            {
                IDimension dim = null;
                try { dim = disp.GetDimension2(0) as IDimension; } catch { /* 单尺寸失败跳过 */ }
                if (dim != null)
                {
                    string fullName = TryFullName(dim, featName);
                    double valMm = 0;
                    try { valMm = Units.MToMm(dim.SystemValue); } catch { /* 保留 0 */ }
                    // 去重：同一 DisplayDimension 可能被 GetFirst/GetNext 链重复返回
                    if (!list.Any(x => x.FullName == fullName && x.FeatureName == featName))
                    {
                        list.Add(new DimensionInfo
                        {
                            FullName = fullName,
                            ValueMm = valMm,
                            FeatureName = featName
                        });
                    }
                }
                disp = feat.GetNextDisplayDimension(disp) as IDisplayDimension;
            }
        }

        /// <summary>尺寸全名：优先 FullName，降级 GetNameForSelection，再降级短名@特征名。</summary>
        private static string TryFullName(IDimension dim, string featName)
        {
            try { string n = dim.FullName; if (!string.IsNullOrWhiteSpace(n)) return n; } catch { }
            try { string n = dim.GetNameForSelection(); if (!string.IsNullOrWhiteSpace(n)) return n; } catch { }
            try { string n = dim.Name; if (!string.IsNullOrWhiteSpace(n)) return n + "@" + featName; } catch { }
            return "";
        }

        private static string SafeName(IFeature feat)
        {
            try { return feat.Name; } catch { return "<未知>"; }
        }
    }
}
