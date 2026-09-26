using System;
using System.IO;
using SwAiAssistant.Core.Logging;
using SwAiAssistant.Core.Text;

namespace SwAiAssistant.Core.Configuration
{
    /// <summary>
    /// 强类型 JSON 配置文件存储（位于 %AppData%\SwAiAssistant\config）。
    /// 仅负责落盘/读取；敏感字段由上层先用 <see cref="Security.DpapiHelper"/> 加密。
    /// 写入采用"临时文件 + 替换"，避免崩溃导致配置损坏。
    /// </summary>
    public class JsonConfigStore<T> where T : class, new()
    {
        private readonly string _filePath;
        private readonly object _sync = new object();

        public JsonConfigStore(string fileName, string directory = null)
        {
            string dir = string.IsNullOrEmpty(directory) ? AppPaths.Config : directory;
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, fileName);
            Current = Load();
        }

        /// <summary>当前内存中的配置实例（修改后需调用 <see cref="Save"/>）。</summary>
        public T Current { get; private set; }

        /// <summary>文件完整路径（测试/诊断用）。</summary>
        public string FilePath => _filePath;

        public T Load()
        {
            try
            {
                lock (_sync)
                {
                    if (!File.Exists(_filePath))
                    {
                        Current = new T();
                        return Current;
                    }

                    string text = File.ReadAllText(_filePath);
                    Current = Json.Deserialize<T>(text) ?? new T();
                    return Current;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Config", $"配置文件损坏，将使用默认配置：{_filePath}", ex);
                string backup = _filePath + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                try { File.Copy(_filePath, backup, true); } catch { /* 忽略 */ }
                Current = new T();
                return Current;
            }
        }

        public void Save()
        {
            lock (_sync)
            {
                string json = Json.Serialize(Current, indented: true);
                string tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(_filePath))
                {
                    File.Replace(tmp, _filePath, null);
                }
                else
                {
                    File.Move(tmp, _filePath);
                }
            }
        }

        /// <summary>在锁内修改配置并立即保存。</summary>
        public void Mutate(Action<T> action)
        {
            lock (_sync)
            {
                action(Current);
                Save();
            }
        }
    }
}
