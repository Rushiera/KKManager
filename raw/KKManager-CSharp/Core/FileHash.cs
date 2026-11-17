using System;
using System.IO;
using System.Security.Cryptography;

namespace KKManager.Core
{
    /// <summary>文件内容哈希——重复副本内容比对的判据（只读，不改文件、不落盘内容）。</summary>
    public static class FileHash
    {
        /// <summary>哈希算法名（档案与面板展示用）。</summary>
        public const string AlgoName = "MD5";

        /// <summary>读缓冲字节数——大 zipmod 不整读进内存。</summary>
        private const int BufferSize = 1024 * 1024;

        /// <summary>计算文件的 MD5（小写十六进制）；读不到时返回 null 并给出原因（不静默）。</summary>
        public static string Md5(string path, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path))
            {
                error = "路径为空";
                return null;
            }

            // [段1] 打开文件流——只读 + 允许共享读写（游戏 / 资源管理器占用时仍可读）
            FileStream fs = null;
            try
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }

            // [段2] 流式计算——按块读入，峰值内存 = 缓冲大小
            try
            {
                using (fs)
                using (MD5 md5 = MD5.Create())
                {
                    byte[] hash = md5.ComputeHash(fs);
                    return ToHex(hash);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>字节数组转小写十六进制串。</summary>
        public static string ToHex(byte[] bytes)
        {
            if (bytes == null)
            {
                return null;
            }
            const string digits = "0123456789abcdef";
            char[] chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i = i + 1)
            {
                int b = bytes[i];
                chars[i * 2] = digits[b >> 4];
                chars[i * 2 + 1] = digits[b & 0x0F];
            }
            return new string(chars);
        }
    }
}
