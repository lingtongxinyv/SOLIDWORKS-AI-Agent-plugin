using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SwAiAssistant.Core.Logging;

namespace SwAiAssistant.Cad.Capture
{
    /// <summary>
    /// 截图捕获（M4-T18）：标准视图定向（等轴测/前/上/右）+ ZoomToFit + SaveAs 图片导出，
    /// System.Drawing 等比缩放为长边 ≤1024 的 PNG（适配视觉模型输入）。
    /// PNG 直出失败时回退 JPG 导出再转码（JPG 导出各版本 SW 均支持）。
    /// 全部 COM 调用经 SwSession STA 封送；单视角失败仅记日志，不中断其余视角。
    /// </summary>
    public sealed class CaptureService
    {
        // swStandardViews_e
        private const int SwFrontView = 1;
        private const int SwRightView = 4;
        private const int SwTopView = 5;
        private const int SwIsometricView = 7;

        private readonly Session.SwSession _session;

        public CaptureService(Session.SwSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        /// <summary>
        /// 捕获当前零件 4 视角 PNG（等轴测/前视/上视/右视），返回成功文件路径列表。
        /// outDir 不存在会自动创建；全部失败时抛 CadException。
        /// </summary>
        public IList<string> CapturePartViews(IModelDoc2 doc, string outDir, int maxSide = 1024)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            Directory.CreateDirectory(outDir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            var files = new List<string>();
            TryCapture(doc, Path.Combine(outDir, $"shot-{stamp}-iso.png"), "*Isometric", SwIsometricView, maxSide, files);
            TryCapture(doc, Path.Combine(outDir, $"shot-{stamp}-front.png"), "*Front", SwFrontView, maxSide, files);
            TryCapture(doc, Path.Combine(outDir, $"shot-{stamp}-top.png"), "*Top", SwTopView, maxSide, files);
            TryCapture(doc, Path.Combine(outDir, $"shot-{stamp}-right.png"), "*Right", SwRightView, maxSide, files);
            if (files.Count == 0)
            {
                throw new CadException("全部视角截图导出失败（4/4）。");
            }
            Log.Info("Capture", $"截图完成 {files.Count}/4 视角 → {outDir}");
            return files;
        }

        /// <summary>
        /// 逐图纸导出工程图高清 PNG（ActivateSheet + ZoomToFit；PNG 失败回退 JPG 再转码），
        /// 等比缩放长边 ≤maxSide；全部失败抛 CadException；导出后还原原活动图纸。
        /// </summary>
        public IList<string> CaptureDrawingSheets(IModelDoc2 doc, string outDir, int maxSide = 1600)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            Directory.CreateDirectory(outDir);
            var drw = doc as IDrawingDoc;
            if (drw == null) throw new CadException("文档不是工程图。");

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            object snObj = _session.OnSta<object>(() => drw.GetSheetNames());
            string[] sheetNames = snObj is string[] sn
                ? sn
                : (snObj is object[] oa ? oa.OfType<string>().ToArray() : new string[0]);

            string activeSheet = _session.OnSta(
                () => (drw.IGetCurrentSheet() as ISheet)?.GetName());

            var files = new List<string>();
            for (int i = 0; i < sheetNames.Length; i++)
            {
                string pngPath = Path.Combine(outDir,
                    $"sheet-{stamp}-{i + 1}-{SanitizeFilePart(sheetNames[i])}.png");
                TryCaptureSheet(drw, sheetNames[i], pngPath, maxSide, files);
            }

            _session.OnSta<object>(() =>
            {
                if (!string.IsNullOrEmpty(activeSheet)) drw.ActivateSheet(activeSheet);
                return null;
            });

            if (files.Count == 0) throw new CadException("全部工程图图纸导出失败。");
            Log.Info("Capture", $"工程图导出完成 {files.Count}/{sheetNames.Length} 图纸 → {outDir}");
            return files;
        }

        private void TryCaptureSheet(IDrawingDoc drw, string sheetName, string pngPath,
            int maxSide, List<string> files)
        {
            string tmpJpg = null;
            try
            {
                _session.OnSta<object>(() =>
                {
                    drw.ActivateSheet(sheetName);
                    var model = (IModelDoc2)drw;
                    model.ViewZoomtofit2();
                    int errors = 0, warnings = 0;
                    bool pngOk = model.SaveAs4(pngPath, 0, 1, ref errors, ref warnings)
                        && errors == 0 && File.Exists(pngPath);
                    if (!pngOk)
                    {
                        tmpJpg = Path.ChangeExtension(pngPath, ".jpg");
                        int e2 = 0, w2 = 0;
                        bool jpgOk = model.SaveAs4(tmpJpg, 0, 1, ref e2, ref w2)
                            && e2 == 0 && File.Exists(tmpJpg);
                        if (!jpgOk)
                        {
                            throw new CadException(
                                $"图纸「{sheetName}」导出失败（PNG errors={errors}，JPG errors={e2}）。");
                        }
                    }
                    return null;
                });
                if (tmpJpg != null)
                {
                    ConvertToScaledPng(tmpJpg, pngPath, maxSide);
                    try { File.Delete(tmpJpg); } catch { /* 临时文件删除失败忽略 */ }
                }
                else
                {
                    ConvertToScaledPng(pngPath, pngPath, maxSide);
                }
                files.Add(pngPath);
            }
            catch (Exception ex)
            {
                Log.Warn("Capture", $"图纸「{sheetName}」导出失败（跳过）：{ex.Message}");
            }
        }

        private static string SanitizeFilePart(string name)
        {
            if (string.IsNullOrEmpty(name)) return "sheet";
            char[] invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }

        /// <summary>捕获等轴测单视角 PNG（快速复核用）。</summary>
        public string CaptureIsometric(IModelDoc2 doc, string outDir, int maxSide = 1024)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            Directory.CreateDirectory(outDir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            string path = Path.Combine(outDir, $"shot-{stamp}-iso.png");
            var files = new List<string>();
            TryCapture(doc, path, "*Isometric", SwIsometricView, maxSide, files);
            if (files.Count == 0)
            {
                throw new CadException("等轴测视图截图导出失败。");
            }
            return files[0];
        }

        private void TryCapture(IModelDoc2 doc, string pngPath, string namedView, int viewId,
            int maxSide, List<string> files)
        {
            string tmpJpg = null;
            try
            {
                _session.OnSta<object>(() =>
                {
                    doc.ShowNamedView2(namedView, viewId);
                    doc.ViewZoomtofit2();
                    int errors = 0, warnings = 0;
                    bool pngOk = doc.SaveAs4(pngPath, 0, 1, ref errors, ref warnings)
                        && errors == 0 && File.Exists(pngPath);
                    if (!pngOk)
                    {
                        tmpJpg = Path.ChangeExtension(pngPath, ".jpg");
                        int e2 = 0, w2 = 0;
                        bool jpgOk = doc.SaveAs4(tmpJpg, 0, 1, ref e2, ref w2)
                            && e2 == 0 && File.Exists(tmpJpg);
                        if (!jpgOk)
                        {
                            throw new CadException(
                                $"视图「{namedView}」导出失败（PNG errors={errors}，JPG errors={e2}）。");
                        }
                    }
                    return null;
                });
                if (tmpJpg != null)
                {
                    ConvertToScaledPng(tmpJpg, pngPath, maxSide);
                    try { File.Delete(tmpJpg); } catch { /* 临时文件删除失败忽略 */ }
                }
                else
                {
                    ConvertToScaledPng(pngPath, pngPath, maxSide);
                }
                files.Add(pngPath);
            }
            catch (Exception ex)
            {
                Log.Warn("Capture", $"视角「{namedView}」截图失败（跳过）：{ex.Message}");
            }
        }

        /// <summary>
        /// 读取源图，等比缩放到长边 ≤maxSide 后存为 PNG（源=目标时覆盖写）。
        /// SW 导出图片后句柄释放有延迟，且 Image.FromFile 终生持锁——
        /// 故带重试读入并立即复制为内存位图，释放文件锁后再写盘。
        /// </summary>
        private static void ConvertToScaledPng(string srcPath, string destPng, int maxSide)
        {
            Bitmap loaded = LoadUnlocked(srcPath);
            using (loaded)
            {
                var size = FitSize(loaded.Width, loaded.Height, maxSide);
                bool sameFile = string.Equals(srcPath, destPng, StringComparison.OrdinalIgnoreCase);
                if (sameFile && size.Width == loaded.Width && size.Height == loaded.Height
                    && srcPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    return; // 已是 PNG 且无需缩放
                }
                byte[] bytes;
                using (var bmp = new Bitmap(size.Width, size.Height))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.Clear(Color.White);
                    g.DrawImage(loaded, 0, 0, size.Width, size.Height);
                    using (var ms = new MemoryStream())
                    {
                        bmp.Save(ms, ImageFormat.Png);
                        bytes = ms.ToArray();
                    }
                }
                File.WriteAllBytes(destPng, bytes);
            }
        }

        /// <summary>读图并立即复制为内存位图（释放文件锁）；占用冲突时重试 5 次（间隔 200ms）。</summary>
        private static Bitmap LoadUnlocked(string path)
        {
            for (int i = 0; ; i++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var img = Image.FromStream(fs))
                    {
                        return new Bitmap(img);
                    }
                }
                catch (IOException) when (i < 4)
                {
                    System.Threading.Thread.Sleep(200);
                }
            }
        }

        private static Size FitSize(int w, int h, int maxSide)
        {
            int longest = Math.Max(w, h);
            if (longest <= maxSide || longest <= 0) return new Size(w, h);
            double k = (double)maxSide / longest;
            return new Size(Math.Max(1, (int)Math.Round(w * k)), Math.Max(1, (int)Math.Round(h * k)));
        }
    }
}
