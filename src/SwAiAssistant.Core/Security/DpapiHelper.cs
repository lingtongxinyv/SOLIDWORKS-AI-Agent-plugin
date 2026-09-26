using System;
using System.Security.Cryptography;
using System.Text;

namespace SwAiAssistant.Core.Security
{
    /// <summary>
    /// 基于 Windows DPAPI（CurrentUser 范围）的本机密钥加解密。
    /// 密文只能由同一 Windows 用户在同一台机器上解开，满足"Key 存本机、不硬编码"。
    /// </summary>
    public static class DpapiHelper
    {
        // 固定附加熵，使密文与本应用绑定（非秘密，仅区分用途）。
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SwAiAssistant.v1.DPAPI.Entropy");

        /// <summary>加密明文，返回 Base64 字符串；输入为空时原样返回空串。</summary>
        public static string Protect(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
            {
                return string.Empty;
            }

            byte[] data = Encoding.UTF8.GetBytes(plainText);
            byte[] cipher = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(cipher);
        }

        /// <summary>解密 <see cref="Protect"/> 产生的 Base64 密文；空串原样返回。</summary>
        public static string Unprotect(string protectedBase64)
        {
            if (string.IsNullOrEmpty(protectedBase64))
            {
                return string.Empty;
            }

            byte[] cipher = Convert.FromBase64String(protectedBase64);
            byte[] data = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
    }
}
