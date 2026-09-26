using System;
using System.Collections.Generic;
using System.Linq;
using SwAiAssistant.Core.Security;

namespace SwAiAssistant.Core.Configuration
{
    /// <summary>
    /// 应用配置访问门面：包装 <see cref="JsonConfigStore{T}"/>，
    /// 提供 API Key 的 DPAPI 读写、界面掩码、模型项增删改。
    /// 插件内以 <see cref="Default"/> 单例使用；测试可独立实例化到临时目录。
    /// </summary>
    public class ConfigService
    {
        private static readonly Lazy<ConfigService> LazyDefault =
            new Lazy<ConfigService>(() => new ConfigService("app.json", null));

        /// <summary>插件全局单例（配置位于 %AppData%\SwAiAssistant\config\app.json）。</summary>
        public static ConfigService Default => LazyDefault.Value;

        private readonly JsonConfigStore<AppConfig> _store;

        public ConfigService(string fileName, string directory)
        {
            _store = new JsonConfigStore<AppConfig>(fileName, directory);
        }

        /// <summary>当前配置（修改后需 <see cref="Save"/>）。</summary>
        public AppConfig Current => _store.Current;

        /// <summary>底层存储文件路径（诊断/测试用）。</summary>
        public string FilePath => _store.FilePath;

        public void Save() => _store.Save();

        /// <summary>在锁内修改配置并立即保存。</summary>
        public void Mutate(Action<AppConfig> action) => _store.Mutate(action);

        public void Reload() => _store.Load();

        // ---- 模型项 ----

        public ModelConfigEntry FindModel(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return _store.Current.Models.FirstOrDefault(m => m.Id == id);
        }

        public void UpsertModel(ModelConfigEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            _store.Mutate(cfg =>
            {
                int idx = cfg.Models.FindIndex(m => m.Id == entry.Id);
                if (idx >= 0) cfg.Models[idx] = entry;
                else cfg.Models.Add(entry);
            });
        }

        public bool RemoveModel(string id)
        {
            bool removed = false;
            _store.Mutate(cfg =>
            {
                removed = cfg.Models.RemoveAll(m => m.Id == id) > 0;
            });
            return removed;
        }

        /// <summary>已启用模型列表（调度候选）。</summary>
        public IReadOnlyList<ModelConfigEntry> EnabledModels()
        {
            return _store.Current.Models.Where(m => m.Enabled).ToList();
        }

        // ---- API Key（DPAPI） ----

        /// <summary>解密读取 Key；无 Key 返回空串；密文损坏返回空串（不抛）。</summary>
        public string GetApiKey(ModelConfigEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.ApiKeyProtected)) return "";
            try
            {
                return DpapiHelper.Unprotect(entry.ApiKeyProtected);
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>以 DPAPI 加密写入 Key（空串表示清除）。</summary>
        public void SetApiKey(ModelConfigEntry entry, string plainKey)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            entry.ApiKeyProtected = string.IsNullOrEmpty(plainKey)
                ? ""
                : DpapiHelper.Protect(plainKey);
        }

        /// <summary>界面掩码：sk-****ab12；长度不足时全掩码。</summary>
        public static string MaskKey(string plainKey)
        {
            if (string.IsNullOrEmpty(plainKey)) return "";
            if (plainKey.Length <= 8) return new string('*', Math.Max(4, plainKey.Length));
            return plainKey.Substring(0, 3) + "****" + plainKey.Substring(plainKey.Length - 4);
        }

        /// <summary>该模型是否已保存过 Key（编辑对话框据此显示「已保存」占位）。</summary>
        public bool HasApiKey(ModelConfigEntry entry)
        {
            return entry != null && !string.IsNullOrEmpty(entry.ApiKeyProtected);
        }
    }
}
