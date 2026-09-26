using System;
using System.IO;
using System.Threading.Tasks;

namespace SwAiAssistant.Reverse.Image
{
    /// <summary>
    /// PDF 首页转 PNG（M9）引导文案统一放在这里；真正的渲染实现见文件下半部
    /// 的 HAVE_WINRT_PDF 分支（Windows.Data.Pdf，Win10+ 系统自带，零三方部署）。
    /// </summary>
    public static class PdfToImage
    {
        /// <summary>当前构建是否支持 PDF 直接渲染（不支持时 UI 展示转换引导）。</summary>
        public static bool IsSupported
        {
            get
            {
#if HAVE_WINRT_PDF
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>不支持 PDF 渲染时给用户的中文引导。</summary>
        public const string PdfGuidance =
            "当前构建未启用 PDF 直接解析。请用以下任一方式把图纸首页转为图片后再「导入图片」：\n"
            + "1. 用 PDF 阅读器打开图纸 → 另存/导出为 PNG 或 JPG；\n"
            + "2. 打开首页后截图（Win+Shift+S）保存为 PNG；\n"
            + "3. 或使用免费的 ODA File Converter / 在线工具把 PDF 首页导出为图片。\n"
            + "提示：DXF/DWG 源文件可直接用「导入DXF」获得更精确的结构化反建。";

#if !HAVE_WINRT_PDF
        /// <summary>未启用 WinRT 渲染的构建：直接失败并给转换引导（UI 捕获后展示）。</summary>
        public static Task<string> RenderFirstPageAsync(string pdfPath, string outPngPath, uint maxWidth = 1600)
        {
            throw new InvalidOperationException(PdfGuidance);
        }
#else
        /// <summary>
        /// 用 Windows.Data.Pdf 把 PDF 第一页渲染为 PNG（长边限制 maxWidth 像素）。
        /// 仅在调用线程可使用 WinRT 的桌面进程中调用（Win10 1809+ / Win11）。
        /// </summary>
        public static async Task<string> RenderFirstPageAsync(string pdfPath, string outPngPath, uint maxWidth = 1600)
        {
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            {
                throw new FileNotFoundException("PDF 文件不存在：" + pdfPath, pdfPath);
            }
            outPngPath = Path.GetFullPath(outPngPath);
            string outDir = Path.GetDirectoryName(outPngPath);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            var file = await Windows.Storage.StorageFile
                .GetFileFromPathAsync(Path.GetFullPath(pdfPath))
                .AsTask().ConfigureAwait(false);
            Windows.Data.Pdf.PdfDocument doc;
            try
            {
                doc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file).AsTask().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "PDF 无法打开（可能已加密或已损坏）。建议先另存为 PNG/JPG 后用「导入图片」。原始错误：" + ex.Message, ex);
            }
            if (doc.PageCount == 0)
            {
                throw new InvalidOperationException("PDF 不含任何页面。");
            }

            var page = doc.GetPage(0);
            try
            {
                var options = new Windows.Data.Pdf.PdfPageRenderOptions();
                double srcW = page.Size.Width;
                double srcH = page.Size.Height;
                if (srcW > maxWidth && srcW > 0)
                {
                    double scale = (double)maxWidth / srcW;
                    options.DestinationWidth = (uint)Math.Round(srcW * scale);
                    options.DestinationHeight = (uint)Math.Round(srcH * scale);
                }
                using (var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream())
                {
                    await page.RenderToStreamAsync(ras, options).AsTask().ConfigureAwait(false);
                    using (var netStream = ras.AsStreamForRead())
                    using (var src = System.Drawing.Image.FromStream(netStream))
                    {
                        src.Save(outPngPath, System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
            }
            finally
            {
                page.Dispose();
            }
            return outPngPath;
        }
#endif
    }
}
