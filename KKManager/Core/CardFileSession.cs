using System;
using System.IO;
using System.Text;

namespace KKManager.Core
{
    /// <summary>
    /// 卡片文件会话——一次打开、按需 seek 读，供同一读取段内的多个步骤共用（IO 账的核心）。
    /// 图片区终点（IEND）与头段（卡类型 / 数据版本）只定位一次并缓存；只读，不写文件。
    /// </summary>
    public class CardFileSession : IDisposable
    {
        private readonly FileStream _fs;
        private long _imageEnd;
        private bool _headDone;
        private string _cardType;
        private string _dataVersion;

        private CardFileSession(FileStream fs)
        {
            _fs = fs;
            _imageEnd = -1;
        }

        /// <summary>打开一个卡片文件（不存在或打不开返回 null）。</summary>
        public static CardFileSession Open(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }
            try
            {
                FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
                return new CardFileSession(fs);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>文件路径。</summary>
        public string Path
        {
            get { return _fs.Name; }
        }

        /// <summary>文件字节数。</summary>
        public long Length
        {
            get { return _fs.Length; }
        }

        /// <summary>底层流——解析器需要自行 seek 时直接用（调用方负责 seek，勿关流）。</summary>
        public FileStream Stream
        {
            get { return _fs; }
        }

        /// <summary>图片区终点（IEND 结束偏移）——只定位一次；未找到返回 -1。</summary>
        public long ImageEnd
        {
            get
            {
                EnsureHead();
                return _imageEnd;
            }
        }

        /// <summary>卡类型（头段，缓存；未识别为 null）。</summary>
        public string CardType
        {
            get
            {
                EnsureHead();
                return _cardType;
            }
        }

        /// <summary>数据版本（头段，缓存）。</summary>
        public string DataVersion
        {
            get
            {
                EnsureHead();
                return _dataVersion;
            }
        }

        /// <summary>头段是否成功读到（图片区终点有效）。</summary>
        public bool HeadOk
        {
            get
            {
                EnsureHead();
                return _imageEnd > 0;
            }
        }

        /// <summary>在会话流上按偏移读满 count 字节（越界或短读返回 null）。</summary>
        public byte[] Read(long offset, int count)
        {
            if (offset < 0 || count <= 0 || offset + count > _fs.Length)
            {
                return null;
            }
            byte[] buf = new byte[count];
            int total = 0;
            _fs.Position = offset;
            while (total < count)
            {
                int n = _fs.Read(buf, total, count - total);
                if (n <= 0)
                {
                    break;
                }
                total += n;
            }
            if (total != count)
            {
                return null;
            }
            return buf;
        }

        /// <summary>头段定位（幂等）——IEND + 卡类型 / 数据版本。</summary>
        private void EnsureHead()
        {
            if (_headDone)
            {
                return;
            }
            _headDone = true;
            _imageEnd = CardReader.FindPngEnd(_fs, _fs.Length);
            if (_imageEnd <= 0 || _fs.Length - _imageEnd < 5)
            {
                _imageEnd = -1;
                return;
            }
            _fs.Position = _imageEnd;
            BinaryReader br = new BinaryReader(_fs, Encoding.UTF8, true);
            CardReader.ReadCardHead(br, out _cardType, out _dataVersion);
        }

        /// <summary>释放文件句柄。</summary>
        public void Dispose()
        {
            _fs.Dispose();
        }
    }
}
