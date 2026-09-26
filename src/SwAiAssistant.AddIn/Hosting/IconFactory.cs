using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using SwAiAssistant.Core.Configuration;

namespace SwAiAssistant.AddIn.Hosting
{
    /// <summary>
    /// 运行时生成命令组/任务面板所需的 PNG 图标（蓝色圆角底 + 白色「AI」字样），
    /// 避免在工程中维护二进制资源，也避免对外部图标文件的依赖。
    /// </summary>
    internal static class IconFactory
    {
        private static readonly string IconDir = Path.Combine(AppPaths.Temp, "icons");

        public static string GetOrCreate(int size)
        {
            AppPaths.Ensure();
            Directory.CreateDirectory(IconDir);
            string path = Path.Combine(IconDir, $"ai-{size}.png");
            if (File.Exists(path))
            {
                return path;
            }

            using (var bmp = new Bitmap(size, size))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.FromArgb(31, 111, 178));

                using (var brush = new SolidBrush(Color.FromArgb(31, 111, 178)))
                using (var pen = new Pen(Color.FromArgb(31, 111, 178)))
                {
                    g.FillRectangle(brush, 0, 0, size, size);
                }

                // 白色 AI 字样，按尺寸缩放
                float fontSize = size * 0.42f;
                using (var font = new Font("Microsoft YaHei", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var white = new SolidBrush(Color.White))
                {
                    string text = "AI";
                    SizeF sf = g.MeasureString(text, font);
                    float x = (size - sf.Width) / 2f;
                    float y = (size - sf.Height) / 2f;
                    g.DrawString(text, font, white, x, y);
                }

                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            return path;
        }

        /// <summary>SolidWorks 图标列表：多个尺寸路径以换行分隔。</summary>
        public static string GetIconList(int[] sizes)
        {
            var sb = new StringBuilder();
            foreach (int size in sizes)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(GetOrCreate(size));
            }
            return sb.ToString();
        }
    }
}
