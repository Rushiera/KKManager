using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace KKManager.Core
{
    /// <summary>缩略图提取——从卡片图片区（PNG）截取并缩为 JPEG 字节。</summary>
    public static class Thumbnail
    {
        /// <summary>最近一次失败诊断（成功为 null）。</summary>
        public static string LastError;

        /// <summary>
        /// 截取卡片图片区并生成缩略图。
        /// </summary>
        /// <param name="path">卡片文件路径。</param>
        /// <param name="imageEnd">图片区长度（IEND 结束偏移）。</param>
        /// <param name="width">目标宽度（像素）。</param>
        /// <param name="quality">JPEG 质量（1-100）。</param>
        /// <returns>JPEG 字节；失败返回 null（诊断见 <see cref="LastError"/>）。</returns>
        public static byte[] FromCard(string path, long imageEnd, int width, int quality)
        {
            LastError = null;
            if (imageEnd <= 0)
            {
                LastError = "图片区长度非法: " + imageEnd;
                return null;
            }
            if (imageEnd > int.MaxValue)
            {
                LastError = "图片区过大: " + imageEnd;
                return null;
            }

            try
            {
                byte[] raw = new byte[(int)imageEnd];
                using (FileStream fs = File.OpenRead(path))
                {
                    int total = 0;
                    while (total < raw.Length)
                    {
                        int n = fs.Read(raw, total, raw.Length - total);
                        if (n <= 0)
                        {
                            break;
                        }
                        total += n;
                    }
                    if (total != raw.Length)
                    {
                        LastError = "图片区读取不足: " + total + "/" + raw.Length;
                        return null;
                    }
                }

                using (Image img = Image.Load(raw))
                {
                    int w = Math.Min(width, img.Width);
                    int h = Math.Max(1, (int)Math.Round(img.Height * (double)w / img.Width));
                    img.Mutate(x => x.Resize(w, h));
                    using (var ms = new MemoryStream())
                    {
                        img.Save(ms, new JpegEncoder { Quality = quality });
                        return ms.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }
        /// <summary>
        /// 从内存里的图片字节生成缩略图。
        /// </summary>
        /// <param name="raw">原始图片字节（PNG / JPEG 等 ImageSharp 可解码格式）。</param>
        /// <param name="width">目标宽度（像素）。</param>
        /// <param name="quality">JPEG 质量（1-100）。</param>
        /// <returns>JPEG 字节；失败返回 null（诊断见 <see cref="LastError"/>）。</returns>
        public static byte[] FromBytes(byte[] raw, int width, int quality)
        {
            LastError = null;
            if (raw == null || raw.Length == 0)
            {
                LastError = "字节为空";
                return null;
            }
            try
            {
                using (Image img = Image.Load(raw))
                {
                    int w = Math.Min(width, img.Width);
                    int h = Math.Max(1, (int)Math.Round(img.Height * (double)w / img.Width));
                    img.Mutate(x => x.Resize(w, h));
                    using (var ms = new MemoryStream())
                    {
                        img.Save(ms, new JpegEncoder { Quality = quality });
                        return ms.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }
    }
}
