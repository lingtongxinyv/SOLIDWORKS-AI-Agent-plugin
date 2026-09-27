using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SwAiAssistant.Cad.Materials;
using SwAiAssistant.Cad.Queries;
using SwAiAssistant.Cad.Session;
using SwAiAssistant.Core.Logging;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SwAiAssistant.Cad.Drawings
{
    /// <summary>工程图单个模型视图的快照信息（POCO，不暴露 COM）。</summary>
    public sealed class DrawingViewInfo
    {
        public string Name { get; set; } = "";
        /// <summary>标准视图方向名（如 "*前视"；投影视图为空）。</summary>
        public string Orientation { get; set; } = "";
        /// <summary>IView.Type：1=图纸伪视图，4=投影视图，7=命名模型视图。</summary>
        public int Type { get; set; }
        public double PosXMm { get; set; }
        public double PosYMm { get; set; }
        public double Scale { get; set; }
        public int DimCount { get; set; }
    }

    /// <summary>工程图生成结果。</summary>
    public sealed class DrawingResult
    {
        public string DrawingPath { get; set; } = "";
        /// <summary>PDF 导出路径；导出失败/未请求时为 null。</summary>
        public string PdfPath { get; set; }
        public string TemplatePath { get; set; } = "";
        public int ViewCount { get; set; }
        /// <summary>零件中被标记「为工程图标注」的驱动尺寸数。</summary>
        public int MarkedDimensionCount { get; set; }
        /// <summary>实际插入工程图的模型尺寸数。</summary>
        public int InsertedDimensionCount { get; set; }
        public int ScaleNum { get; set; } = 1;
        public int ScaleDen { get; set; } = 1;
        /// <summary>一键三视图失败、走了手动第一角降级布图。</summary>
        public bool UsedFallbackLayout { get; set; }
        public List<DrawingViewInfo> Views { get; set; } = new List<DrawingViewInfo>();
    }

    /// <summary>工程图单个尺寸的提取结果（POCO，不暴露 COM）。</summary>
    public sealed class DrawingDimensionExtract
    {
        /// <summary>尺寸全名（如 "D1@草图1"）。</summary>
        public string FullName { get; set; } = "";

        /// <summary>尺寸种类：linear / diameter / radial / angular / chamfer / ordinate / arclength / scalar / other。</summary>
        public string Kind { get; set; } = "";

        /// <summary>尺寸值（毫米；角度为度）。</summary>
        public double ValueMm { get; set; }

        /// <summary>尺寸前缀文本（如 "⌀"、"R"、"M"，可能为空）。</summary>
        public string Prefix { get; set; } = "";

        /// <summary>尺寸后缀文本（可能为空）。</summary>
        public string Suffix { get; set; } = "";
    }

    /// <summary>工程图视图中可见圆边的提取结果（模型空间坐标，POCO）。</summary>
    public sealed class DrawingCircleExtract
    {
        public double CenterXMm { get; set; }
        public double CenterYMm { get; set; }
        public double CenterZMm { get; set; }
        /// <summary>圆所在平面的轴向单位向量。</summary>
        public double AxisX { get; set; }
        public double AxisY { get; set; }
        public double AxisZ { get; set; }
        public double RadiusMm { get; set; }
    }

    /// <summary>工程图单个模型视图的提取结果（POCO）。</summary>
    public sealed class DrawingViewExtract
    {
        public string Name { get; set; } = "";

        /// <summary>标准视图方向名（如 "*前视"；投影视图为空）。</summary>
        public string Orientation { get; set; } = "";

        /// <summary>IView.Type：1=图纸伪视图，4=投影视图，7=命名模型视图。</summary>
        public int ViewType { get; set; }

        public double PosXMm { get; set; }
        public double PosYMm { get; set; }
        public double Scale { get; set; }

        /// <summary>视图轮廓在模型空间的宽/高（毫米；轮廓框 ÷ 视图比例反算）。</summary>
        public double OutlineWModelMm { get; set; }
        public double OutlineHModelMm { get; set; }

        /// <summary>视图引用的模型文件磁盘路径（可能为空/文件已不存在）。</summary>
        public string ReferencedModelPath { get; set; } = "";

        /// <summary>引用模型当前是否已加载。</summary>
        public bool ModelLoaded { get; set; }

        public List<DrawingDimensionExtract> Dimensions { get; set; } = new List<DrawingDimensionExtract>();

        /// <summary>视图内可见圆边（模型空间坐标；用于孔位/凸台/回转体的确定性定位）。</summary>
        public List<DrawingCircleExtract> Circles { get; set; } = new List<DrawingCircleExtract>();
    }

    /// <summary>工程图单张图纸的提取结果（POCO）。</summary>
    public sealed class DrawingSheetExtract
    {
        public string Name { get; set; } = "";
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
        public List<DrawingViewExtract> Views { get; set; } = new List<DrawingViewExtract>();
    }

    /// <summary>整张工程图的确定性提取结果（POCO，供反建规划器使用）。</summary>
    public sealed class DrawingExtract
    {
        public string Title { get; set; } = "";
        public string FilePath { get; set; } = "";
        public List<DrawingSheetExtract> Sheets { get; set; } = new List<DrawingSheetExtract>();
    }

    /// <summary>
    /// GB 第一角三视图工程图服务（M6-T22）：
    /// 模板自动发现（gb_a3 优先）→ 一键第一角三视图（Create1stAngleViews2，调色板预刷新）→
    /// 失败降级手动 CreateDrawViewFromModelView3（前/上/左第一角坐标）→ 比例自适应（ISheet.SetScale）→
    /// 模型尺寸插入（全尺寸 MarkedForDrawing 后调旧版 InsertModelAnnotations——SW2026 实测仅此路径生效）→
    /// 标题栏回填（gb 模板 $PRPSHEET 键：名称/材料/设计/设计日期/代号）→ 保存 .slddrw + 导出 PDF。
    /// 全程经 SwSession.OnSta 封送 STA 线程。
    /// </summary>
    public sealed class DrawingService
    {
        /// <summary>swUserPreferenceStringValue_e.swDefaultTemplateDrawing（默认工程图模板首选项）。</summary>
        private const int SwDefaultTemplateDrawing = 10;

        /// <summary>swInsertAnnotation_e.swInsertDimensionsMarkedForDrawing（仅插入已标记为工程图标注的尺寸）。</summary>
        private const int SwInsertDimensionsMarkedForDrawing = 32768;

        private readonly SwSession _session;
        private readonly QueryService _query;
        private readonly MaterialService _materials;

        public DrawingService(SwSession session, QueryService query, MaterialService materials = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _query = query ?? throw new ArgumentNullException(nameof(query));
            _materials = materials; // 可空：缺省时标题栏材料字段跳过
        }

        /// <summary>
        /// 工程图模板自动发现（FindPartTemplate 同款策略）：
        /// 1) SW 首选项默认工程图模板（存在性校验，坏配置跳过）；
        /// 2) ProgramData 按版本年目录回退：gb_a3.drwdot → 任意 gb_*.drwdot → 任意 *.drwdot；
        /// 3) 全部失败抛中文 CadException。
        /// </summary>
        public string FindDrawingTemplate()
        {
            return _session.OnSta(() =>
            {
                try
                {
                    string preferred = _session.App.GetUserPreferenceStringValue(SwDefaultTemplateDrawing);
                    if (!string.IsNullOrWhiteSpace(preferred) && File.Exists(preferred)
                        && preferred.EndsWith(".drwdot", StringComparison.OrdinalIgnoreCase))
                    {
                        return preferred;
                    }

                    int year = RevisionYear();
                    foreach (int y in new[] { year, year - 1 })
                    {
                        string dir = Path.Combine(
                            System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData),
                            "SOLIDWORKS", $"SOLIDWORKS {y}", "templates");
                        if (!Directory.Exists(dir)) continue;
                        string gbA3 = Path.Combine(dir, "gb_a3.drwdot");
                        if (File.Exists(gbA3)) return gbA3;
                        string anyGb = Directory.GetFiles(dir, "gb_*.drwdot").FirstOrDefault();
                        if (anyGb != null) return anyGb;
                        string any = Directory.GetFiles(dir, "*.drwdot").FirstOrDefault();
                        if (any != null) return any;
                    }
                }
                catch (Exception ex) when (!(ex is CadException))
                {
                    throw CadException.FromCom("查找工程图模板", ex);
                }
                throw new CadException("找不到工程图模板（.drwdot）：请确认 SolidWorks 已安装 GB 工程图模板（gb_a3.drwdot）。");
            });
        }

        /// <summary>枚举工程图全部模型视图（排除 type=1 图纸伪视图），返回 POCO 快照。</summary>
        public List<DrawingViewInfo> GetViewInfos(IModelDoc2 drawing)
        {
            if (drawing == null) throw new ArgumentNullException(nameof(drawing));
            return _session.OnSta(() =>
            {
                var drw = drawing as IDrawingDoc;
                if (drw == null) throw new CadException("文档不是工程图。");
                return EnumModelViews(drw).Select(ToViewInfo).ToList();
            });
        }

        /// <summary>汇总工程图全部模型视图中已插入的 DisplayDimension 总数。</summary>
        public int CountViewDimensions(IModelDoc2 drawing)
        {
            if (drawing == null) throw new ArgumentNullException(nameof(drawing));
            return _session.OnSta(() =>
            {
                var drw = drawing as IDrawingDoc;
                if (drw == null) throw new CadException("文档不是工程图。");
                return CountViewDimensions(EnumModelViews(drw));
            });
        }

        /// <summary>
        /// 工程图确定性提取（按图反建数据源）：逐图纸枚举模型视图，提取视图方向/比例/
        /// 模型空间轮廓尺寸/引用模型路径 + 视图内全部尺寸（类型/前缀/值）。
        /// 逐视图、逐尺寸失败仅记日志不中断；提取后还原原活动图纸。
        /// </summary>
        public DrawingExtract ExtractDrawing(IModelDoc2 drawing)
        {
            if (drawing == null) throw new ArgumentNullException(nameof(drawing));
            return _session.OnSta(() =>
            {
                var drw = drawing as IDrawingDoc;
                if (drw == null) throw new CadException("文档不是工程图。");

                var extract = new DrawingExtract
                {
                    Title = SafeTitle(drawing),
                    FilePath = SafePath(drawing)
                };

                object snObj = null;
                try { snObj = drw.GetSheetNames(); } catch { }
                string[] sheetNames = snObj is string[] sn
                    ? sn
                    : (snObj is object[] oa ? oa.OfType<string>().ToArray() : new string[0]);

                string activeSheet = null;
                try { activeSheet = (drw.IGetCurrentSheet() as ISheet)?.GetName(); } catch { }

                foreach (string sheetName in sheetNames)
                {
                    var se = new DrawingSheetExtract { Name = sheetName };
                    try
                    {
                        drw.ActivateSheet(sheetName);
                        ISheet sheet = drw.IGetCurrentSheet() as ISheet;
                        if (sheet != null)
                        {
                            double pw = 0, ph = 0;
                            sheet.GetSize(ref pw, ref ph);
                            se.WidthMm = pw * 1000;
                            se.HeightMm = ph * 1000;

                            object viewsObj = sheet.GetViews();
                            if (viewsObj is object[] viewRows)
                            {
                                foreach (object row in viewRows)
                                {
                                    // ISheet.GetViews 一般直接返回视图数组；兼容二维嵌套返回
                                    if (row is IView single) AddView(se, single);
                                    else if (row is object[] nested)
                                    {
                                        foreach (object vo in nested)
                                        {
                                            if (vo is IView view) AddView(se, view);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Cad", $"图纸「{sheetName}」提取失败（继续下一张）：" + ex.Message);
                    }
                    extract.Sheets.Add(se);
                }

                if (!string.IsNullOrEmpty(activeSheet))
                {
                    try { drw.ActivateSheet(activeSheet); } catch { /* 忽略 */ }
                }
                int viewCount = extract.Sheets.Sum(s => s.Views.Count);
                int dimCount = extract.Sheets.SelectMany(s => s.Views).Sum(v => v.Dimensions.Count);
                Log.Info("Cad", $"工程图提取完成：{extract.Sheets.Count} 图纸，{viewCount} 模型视图，{dimCount} 尺寸");
                return extract;
            });
        }

        /// <summary>
        /// 主流程：由已保存零件生成 GB 第一角三视图工程图（尺寸/比例/标题栏/PDF 全自动）。
        /// 零件未保存（无磁盘路径）时抛 CadException——工程图视图按文件路径引用模型。
        /// 生成后工程图保持打开（交给用户查看）；PDF 导出失败不阻断（工程图仍有效）。
        /// </summary>
        public DrawingResult CreateThreeViewDrawing(IModelDoc2 part, string drawingPath,
            string pdfPath = null, CancellationToken ct = default(CancellationToken))
        {
            if (part == null) throw new ArgumentNullException(nameof(part));
            if (string.IsNullOrWhiteSpace(drawingPath)) throw new ArgumentNullException(nameof(drawingPath));
            ct.ThrowIfCancellationRequested();

            return _session.OnSta(() =>
            {
                string partPath;
                try { partPath = part.GetPathName(); } catch { partPath = ""; }
                if (string.IsNullOrWhiteSpace(partPath) || !File.Exists(partPath))
                {
                    throw new CadException("生成工程图前请先保存零件：工程图视图按磁盘文件引用模型。");
                }
                if (part.GetType() != (int)swDocumentTypes_e.swDocPART)
                {
                    throw new CadException("当前文档不是零件，无法生成零件工程图。");
                }

                var result = new DrawingResult { DrawingPath = drawingPath, PdfPath = pdfPath };

                // 1. 全尺寸标记「为工程图标注」+ 重建 + 重存（InsertModelAnnotations 只抓已标记尺寸）
                result.MarkedDimensionCount = MarkAllDimensionsForDrawing(part);
                try { part.ForceRebuild3(true); }
                catch (Exception ex) { Log.Warn("Cad", "零件重建失败（继续）：" + ex.Message); }
                int se = 0, sw2 = 0;
                if (!part.SaveAs4(partPath, 0, 1, ref se, ref sw2) || se != 0)
                {
                    throw new CadException($"零件重存失败（SaveAs4 errors={se}）：{partPath}");
                }
                ct.ThrowIfCancellationRequested();

                // 2. 模板 + 新建工程图（A3 幅面）
                string template = FindDrawingTemplate();
                result.TemplatePath = template;
                var drawing = _session.App.NewDocument(template,
                    (int)swDwgPaperSizes_e.swDwgPaperA3size, 0.0, 0.0) as IModelDoc2;
                if (!(drawing is IDrawingDoc drw))
                {
                    throw new CadException("新建工程图失败：NewDocument 返回空（模板可能损坏：" + template + "）。");
                }
                Log.Info("Cad", "工程图已建：" + SafeTitle(drawing) + "（模板 " + Path.GetFileName(template) + "）");

                // 3. 三视图：调色板预刷新 + 一键第一角；失败降级手动布图
                //    （SW2026 实测：同零件第 2 张图起 Create1stAngleViews2 不预刷新调色板会静默失败）
                try { drw.GenerateViewPaletteViews(partPath); }
                catch (Exception ex) { Log.Warn("Cad", "视图调色板预刷新失败（继续）：" + ex.Message); }
                bool okAuto = false;
                try { okAuto = drw.Create1stAngleViews2(partPath); }
                catch (Exception ex) { Log.Warn("Cad", "Create1stAngleViews2 异常（将降级）：" + ex.Message); }
                var views = EnumModelViews(drw);
                if (!okAuto || views.Count < 3)
                {
                    Log.Warn("Cad", $"一键三视图未成功（rc={okAuto}，视图={views.Count}），降级手动第一角布图");
                    result.UsedFallbackLayout = true;
                    CreateViewsManually(drw, partPath);
                    views = EnumModelViews(drw);
                }
                result.ViewCount = views.Count;
                if (views.Count < 3)
                {
                    throw new CadException($"三视图创建失败：仅 {views.Count} 个模型视图（已尝试自动与手动两条路径）。");
                }
                ct.ThrowIfCancellationRequested();

                // 4. 比例自适应（按模型包围盒 vs 幅面可用区选标准比例档）
                PickSheetScale(part, drw, out int num, out int den);
                result.ScaleNum = num;
                result.ScaleDen = den;
                try { drw.IGetCurrentSheet().SetScale(num, den, true, false); }
                catch (Exception ex) { Log.Warn("Cad", "设置图纸比例失败（保持模板默认）：" + ex.Message); }

                // 5. 插入模型尺寸
                //    （SW2026 实测：InsertModelAnnotations4/ImportAnnotations 均静默为 0，
                //      仅旧版 InsertModelAnnotations(option=已标记, allTypes=true) 生效——probe-draw4 结论）
                int before = CountViewDimensions(views);
                bool insOk = false;
                try { insOk = drw.InsertModelAnnotations(SwInsertDimensionsMarkedForDrawing, true, 0, true); }
                catch (Exception ex) { Log.Warn("Cad", "插入模型尺寸异常：" + ex.Message); }
                int after = CountViewDimensions(EnumModelViews(drw));
                result.InsertedDimensionCount = Math.Max(0, after - before);
                if (result.InsertedDimensionCount == 0 && result.MarkedDimensionCount > 0)
                {
                    Log.Warn("Cad", $"模型尺寸插入为 0（已标记 {result.MarkedDimensionCount}，rc={insOk}）——工程图将无尺寸，需人工补标。");
                }
                ct.ThrowIfCancellationRequested();

                // 6. 标题栏回填（gb 模板 $PRPSHEET 链接键，probe-draw2 实测）
                FillTitleBlock(drawing, part);

                // 7. 保存 .slddrw
                string dir = Path.GetDirectoryName(drawingPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                int e2 = 0, w2 = 0;
                if (!drawing.SaveAs4(drawingPath, 0, 1, ref e2, ref w2) || e2 != 0)
                {
                    throw new CadException($"工程图保存失败（SaveAs4 errors={e2}）：{drawingPath}");
                }

                // 8. 导出 PDF（可选；失败仅告警不阻断）
                if (!string.IsNullOrWhiteSpace(pdfPath))
                {
                    int e3 = 0, w3 = 0;
                    bool okPdf = false;
                    try { okPdf = drawing.Extension.SaveAs3(pdfPath, 0, 1, null, null, ref e3, ref w3); }
                    catch (Exception ex) { Log.Warn("Cad", "PDF 导出异常：" + ex.Message); }
                    if (!okPdf || e3 != 0 || !File.Exists(pdfPath))
                    {
                        Log.Warn("Cad", $"PDF 导出失败（rc={okPdf} errors={e3}）——工程图已保存，PDF 缺失。");
                        result.PdfPath = null;
                    }
                }

                result.Views = EnumModelViews(drw).Select(ToViewInfo).ToList();
                Log.Info("Cad", $"工程图完成：{result.ViewCount} 视图，插尺寸 {result.InsertedDimensionCount}/{result.MarkedDimensionCount}，" +
                    $"比例 {num}:{den}{(result.UsedFallbackLayout ? "（降级布图）" : "")} → {drawingPath}");
                return result;
            });
        }

        // ==================== 内部实现 ====================

        /// <summary>遍历零件全部特征的 DisplayDimension 并标记「为工程图标注」，返回标记数。</summary>
        private static int MarkAllDimensionsForDrawing(IModelDoc2 part)
        {
            int marked = 0;
            IFeature feat = part.FirstFeature() as IFeature;
            while (feat != null)
            {
                IDisplayDimension disp = feat.GetFirstDisplayDimension() as IDisplayDimension;
                while (disp != null)
                {
                    try { disp.MarkedForDrawing = true; marked++; }
                    catch (Exception ex)
                    {
                        Log.Warn("Cad", $"尺寸标记失败（跳过，特征「{SafeFeatName(feat)}」）：" + ex.Message);
                    }
                    disp = feat.GetNextDisplayDimension(disp) as IDisplayDimension;
                }
                feat = feat.GetNextFeature() as IFeature;
            }
            return marked;
        }

        /// <summary>
        /// 手动第一角降级布图：前视（左上部）、上视（前视正下方）、左视（前视正右方）。
        /// 视图名按中文 SW 标准名，失败回退英文名。坐标按图纸实际幅面比例摆放。
        /// </summary>
        private static void CreateViewsManually(IDrawingDoc drw, string partPath)
        {
            double w = 0.42, h = 0.297; // A3 横向兜底（米）
            try
            {
                double pw = 0, ph = 0;
                drw.IGetCurrentSheet().GetSize(ref pw, ref ph);
                if (pw > 0.05 && ph > 0.05) { w = pw; h = ph; }
            }
            catch { /* 用兜底幅面 */ }

            // 第一角：前视左上；上视在前视正下方；左视在前视正右方
            double fx = 0.36 * w, fy = 0.70 * h;
            double tx = fx, ty = 0.28 * h;
            double lx = 0.68 * w, ly = fy;
            var placements = new[]
            {
                new[] { "*前视", "*Front" }, new[] { "*上视", "*Top" }, new[] { "*左视", "*Left" }
            };
            var coords = new[] { (fx, fy), (tx, ty), (lx, ly) };
            for (int i = 0; i < placements.Length; i++)
            {
                object view = null;
                foreach (string vn in placements[i])
                {
                    try
                    {
                        view = drw.CreateDrawViewFromModelView3(partPath, vn, coords[i].Item1, coords[i].Item2, 0.0);
                        if (view != null) break;
                    }
                    catch { /* 试下一个语言名 */ }
                }
                if (view == null)
                {
                    Log.Warn("Cad", $"手动布图：视图「{placements[i][0]}」创建失败（两种语言名均失败）");
                }
            }
        }

        /// <summary>
        /// 比例自适应：按模型包围盒估算三视图占位（宽≈X+Z+间隙，高≈Y+Z+间隙），
        /// 对幅面可用区（宽 82%、高 78%）选最大标准比例档（5:1 → 1:20）。
        /// </summary>
        private void PickSheetScale(IModelDoc2 part, IDrawingDoc drw, out int num, out int den)
        {
            num = 1; den = 1;
            double sheetW = 0.42, sheetH = 0.297;
            try
            {
                double pw = 0, ph = 0;
                drw.IGetCurrentSheet().GetSize(ref pw, ref ph);
                if (pw > 0.05 && ph > 0.05) { sheetW = pw; sheetH = ph; }
            }
            catch { /* 兜底幅面 */ }

            double x = 100, y = 100, z = 100; // 兜底 100mm
            try
            {
                var box = _query.GetBoundingBoxMm(part);
                x = Math.Max(box.SizeX, 1); y = Math.Max(box.SizeY, 1); z = Math.Max(box.SizeZ, 1);
            }
            catch (Exception ex)
            {
                Log.Warn("Cad", "包围盒回读失败，按 100mm 兜底估算比例：" + ex.Message);
            }

            const double gapMm = 60; // 视图间最小间隙
            double needW = x + z + gapMm;       // 前视宽 + 左视宽 + 间隙
            double needH = y + z + gapMm;       // 前视高 + 俯视深 + 间隙
            double availW = sheetW * 1000 * 0.82;
            double availH = sheetH * 1000 * 0.78;

            // 标准比例档（大→小），取能放下的最大比例
            var scales = new[] { (5, 1), (2, 1), (1, 1), (1, 2), (1, 5), (1, 10), (1, 20) };
            foreach (var (n, d) in scales)
            {
                double s = (double)n / d;
                if (needW * s <= availW && needH * s <= availH)
                {
                    num = n; den = d;
                    return;
                }
            }
            num = 1; den = 20; // 最小档兜底
        }

        /// <summary>标题栏回填：gb 模板 $PRPSHEET 链接键（probe-draw2 实测清单）。</summary>
        private void FillTitleBlock(IModelDoc2 drawing, IModelDoc2 part)
        {
            string name = Path.GetFileNameWithoutExtension(SafeTitle(part));
            SetCustomText(drawing, "名称", name);
            SetCustomText(drawing, "代号", name);
            string material = "";
            try { material = _materials?.GetCurrentMaterialName(part) ?? ""; }
            catch (Exception ex) { Log.Warn("Cad", "材料名回读失败（标题栏材料字段跳过）：" + ex.Message); }
            if (!string.IsNullOrWhiteSpace(material))
            {
                SetCustomText(drawing, "材料", material);
            }
            SetCustomText(drawing, "设计", "AI 助手");
            SetCustomText(drawing, "设计日期", DateTime.Now.ToString("yyyy-MM-dd"));
        }

        /// <summary>写文档自定义属性（已存在时覆盖）。</summary>
        private static void SetCustomText(IModelDoc2 doc, string key, string value)
        {
            try
            {
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

        /// <summary>枚举工程图全部模型视图（IView，排除 type=1 图纸伪视图）。调用方须在 STA。</summary>
        private static List<IView> EnumModelViews(IDrawingDoc drw)
        {
            var list = new List<IView>();
            if (drw.GetViews() is object[] sheets)
            {
                foreach (object s in sheets)
                {
                    if (!(s is object[] vs)) continue;
                    foreach (object v in vs)
                    {
                        if (v is IView view && view.Type != 1)
                        {
                            list.Add(view);
                        }
                    }
                }
            }
            return list;
        }

        private static int CountViewDimensions(IEnumerable<IView> views)
        {
            int n = 0;
            foreach (var v in views)
            {
                try { n += v.GetDisplayDimensionCount(); } catch { }
            }
            return n;
        }

        private static DrawingViewInfo ToViewInfo(IView view)
        {
            var info = new DrawingViewInfo();
            try { info.Name = view.Name; } catch { }
            try { info.Orientation = view.GetOrientationName() ?? ""; } catch { }
            try { info.Type = view.Type; } catch { }
            try
            {
                if (view.Position is double[] p && p.Length >= 2)
                {
                    info.PosXMm = p[0] * 1000;
                    info.PosYMm = p[1] * 1000;
                }
            }
            catch { }
            try { info.Scale = view.ScaleDecimal; } catch { }
            try { info.DimCount = view.GetDisplayDimensionCount(); } catch { }
            return info;
        }

        /// <summary>提取单个模型视图（跳过图纸伪视图）；单项读取失败仅记日志。</summary>
        private static void AddView(DrawingSheetExtract se, IView view)
        {
            if (view == null) return;
            try { if (view.Type == 1) return; }
            catch { return; }

            var ve = new DrawingViewExtract();
            try { ve.Name = view.Name ?? ""; } catch { }
            try { ve.Orientation = view.GetOrientationName() ?? ""; } catch { }
            try { ve.ViewType = view.Type; } catch { }
            try
            {
                if (view.Position is double[] p && p.Length >= 2)
                {
                    ve.PosXMm = p[0] * 1000;
                    ve.PosYMm = p[1] * 1000;
                }
            }
            catch { }

            double scale = 0;
            try { scale = view.ScaleDecimal; ve.Scale = scale; } catch { }
            try
            {
                if (view.GetOutline() is double[] ol && ol.Length >= 4 && scale > 0)
                {
                    double wSheet = Math.Abs(ol[2] - ol[0]) * 1000;
                    double hSheet = Math.Abs(ol[3] - ol[1]) * 1000;
                    ve.OutlineWModelMm = wSheet / scale;
                    ve.OutlineHModelMm = hSheet / scale;
                }
            }
            catch (Exception ex) { Log.Warn("Cad", $"视图「{ve.Name}」轮廓提取失败：" + ex.Message); }

            try { ve.ReferencedModelPath = view.GetReferencedModelName() ?? ""; } catch { }
            try { ve.ModelLoaded = view.IsModelLoaded(); } catch { }
            try { ve.Dimensions.AddRange(ExtractViewDimensions(view)); }
            catch (Exception ex) { Log.Warn("Cad", $"视图「{ve.Name}」尺寸提取失败：" + ex.Message); }
            try { ve.Circles.AddRange(ExtractViewCircles(view)); }
            catch (Exception ex) { Log.Warn("Cad", $"视图「{ve.Name}」圆边提取失败：" + ex.Message); }

            se.Views.Add(ve);
        }

        /// <summary>枚举视图内全部 DisplayDimension → 提取全名/类型/值/前缀后缀。</summary>
        private static List<DrawingDimensionExtract> ExtractViewDimensions(IView view)
        {
            var list = new List<DrawingDimensionExtract>();
            object raw = null;
            try { raw = view.GetDisplayDimensions(); }
            catch (Exception ex)
            {
                Log.Warn("Cad", "视图尺寸枚举失败：" + ex.Message);
                return list;
            }
            if (!(raw is object[] arr)) return list;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (object o in arr)
            {
                if (!(o is IDisplayDimension disp)) continue;
                try
                {
                    IDimension dim = disp.GetDimension2(0) as IDimension;
                    if (dim == null) continue;

                    int type = 0;
                    try { type = (int)disp.Type2; } catch { }
                    double sysVal = dim.SystemValue;
                    double val = type == 3
                        ? sysVal * 180.0 / Math.PI       // 角度：弧度 → 度
                        : Units.MToMm(sysVal);

                    string full = "";
                    try { full = dim.FullName; } catch { }
                    if (string.IsNullOrWhiteSpace(full))
                    {
                        try { full = dim.GetNameForSelection() ?? ""; } catch { }
                    }
                    if (!string.IsNullOrWhiteSpace(full) && !seen.Add(full)) continue;

                    list.Add(new DrawingDimensionExtract
                    {
                        FullName = full ?? "",
                        Kind = KindOf(type),
                        ValueMm = val,
                        Prefix = TryGetText(disp, 1),
                        Suffix = TryGetText(disp, 2)
                    });
                }
                catch (Exception ex)
                {
                    Log.Warn("Cad", "单个尺寸提取失败（跳过）：" + ex.Message);
                }
            }
            return list;
        }

        /// <summary>
        /// 枚举视图可见边（GetVisibleEntities2 Edge），提取其中圆边的圆心/轴向/半径。
        /// 返回坐标为模型空间（SW 真机实测）；最多 128 个/视图，失败仅跳过。
        /// </summary>
        private static List<DrawingCircleExtract> ExtractViewCircles(IView view)
        {
            var list = new List<DrawingCircleExtract>();
            object raw = null;
            try { raw = view.GetVisibleEntities2(null, 1); }
            catch (Exception ex)
            {
                Log.Warn("Cad", "视图可见边枚举失败：" + ex.Message);
                return list;
            }
            if (!(raw is object[] arr)) return list;

            int added = 0;
            foreach (object o in arr)
            {
                if (!(o is IEdge edge)) continue;
                try
                {
                    ICurve curve = edge.GetCurve() as ICurve;
                    if (curve == null || !curve.IsCircle()) continue;
                    if (!(curve.CircleParams is double[] p) || p.Length < 7) continue;
                    list.Add(new DrawingCircleExtract
                    {
                        CenterXMm = p[0] * 1000,
                        CenterYMm = p[1] * 1000,
                        CenterZMm = p[2] * 1000,
                        AxisX = p[3],
                        AxisY = p[4],
                        AxisZ = p[5],
                        RadiusMm = p[6] * 1000
                    });
                    if (++added >= 128) break;
                }
                catch (Exception ex)
                {
                    Log.Warn("Cad", "圆边提取失败（跳过）：" + ex.Message);
                }
            }
            return list;
        }

        private static string TryGetText(IDisplayDimension disp, int part)
        {
            try
            {
                string t = disp.GetText(part);
                return t == null ? "" : t.Trim();
            }
            catch { return ""; }
        }

        /// <summary>swDimensionType_e → 稳定英文种类名。</summary>
        private static string KindOf(int type)
        {
            switch (type)
            {
                case 2:
                case 11:
                case 12:
                    return "linear";
                case 3:
                    return "angular";
                case 5:
                case 14:
                    return "radial";
                case 6:
                case 15:
                    return "diameter";
                case 10:
                    return "chamfer";
                case 1:
                case 7:
                case 8:
                    return "ordinate";
                case 4:
                    return "arclength";
                case 9:
                case 13:
                    return "scalar";
                default:
                    return "other";
            }
        }

        private int RevisionYear()
        {
            try
            {
                int major = int.Parse(_session.Revision.Split('.')[0]);
                return 1992 + major;
            }
            catch
            {
                return DateTime.Now.Year;
            }
        }

        private static string SafeTitle(IModelDoc2 doc)
        {
            try { return doc.GetTitle(); } catch { return "<未知>"; }
        }

        private static string SafePath(IModelDoc2 doc)
        {
            try { return doc.GetPathName() ?? ""; } catch { return ""; }
        }

        private static string SafeFeatName(IFeature feat)
        {
            try { return feat.Name; } catch { return "<未知>"; }
        }
    }
}
