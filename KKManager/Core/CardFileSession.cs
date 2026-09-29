using System;
using System.IO;
using System.Text;

namespace KKManager.Core
{
    /// <summary>卡片头段判定状态——Ok = 正常卡片；NoSignature / NoIend / NoData / TypeUnknown = 非卡文件（各有理由）；Io = 文件读不了（按失败出声）。</summary>
    public enum CardHeadState
    {
        /// <summary>尚未判定。</summary>
        Unknown,

        /// <summary>正常卡片（卡类型 / 数据版本读到）。</summary>
        Ok,

        /// <summary>不是 PNG 文件（签名不符）。</summary>
        NoSignature,

        /// <summary>PNG 不完整（块链找不到 IEND）。</summary>
        NoIend,

        /// <summary>无数据区（IEND 之后没有字节——纯图片）。</summary>
        NoData,

        /// <summary>数据区头段无法识别（不是已知卡类型，也不是场景卡版本串）。</summary>
        TypeUnknown,

        /// <summary>文件打不开 / 读取异常。</summary>
        Io
    }

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
        /// <summary>头段判定状态（Ok / 非卡四类 / Io——Io 才算读取失败）。</summary>
        private CardHeadState _headState;

        /// <summary>头段判定理由（人读文案——非卡文件登记进清单时展示）。</summary>
        private string _headReason = "";

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

        /// <summary>头段判定状态（非卡与读取失败分列——Ok / NoSignature / NoIend / NoData / TypeUnknown / Io）。</summary>
        public CardHeadState HeadState
        {
            get
            {
                EnsureHead();
                return _headState;
            }
        }

        /// <summary>头段判定理由（人读文案——非卡文件登记进清单时原样展示；正常时为空串）。</summary>
        public string HeadReason
        {
            get
            {
                EnsureHead();
                return _headReason;
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

        /// <summary>头段定位（幂等）——PNG 签名 / IEND / 数据区 / 卡类型逐段判定：不是 PNG、PNG 不完整、无数据区（纯图片）、头段无法识别四类判为非卡文件并记理由，其余读取异常判 Io（按失败出声）。</summary>
        private void EnsureHead()
        {
            if (_headDone)
            {
                return;
            }
            _headDone = true;
            _headState = CardHeadState.Io;
            // [段1] PNG 签名——不是 PNG 的文件不算卡片
            if (!CardReader.HasPngSignature(_fs))
            {
                _imageEnd = -1;
                _headState = CardHeadState.NoSignature;
                _headReason = "不是 PNG 文件";
                return;
            }
            // [段2] 图片区终点
            _imageEnd = CardReader.FindPngEnd(_fs, _fs.Length);
            if (_imageEnd <= 0)
            {
                _headState = CardHeadState.NoIend;
                _headReason = "PNG 不完整（找不到 IEND）";
                return;
            }
            // [段3] 数据区——IEND 之后不足 5 字节即纯图片（不是卡片）
            if (_fs.Length - _imageEnd < 5)
            {
                _headState = CardHeadState.NoData;
                _headReason = "无数据区（纯图片）";
                return;
            }
            // [段4] 数据区头段——卡类型 / 数据版本
            _fs.Position = _imageEnd;
            BinaryReader br = new BinaryReader(_fs, Encoding.UTF8, true);
            CardReader.ReadCardHead(br, out _cardType, out _dataVersion);
            if (_cardType == null)
            {
                _headState = CardHeadState.TypeUnknown;
                _headReason = "数据区头段无法识别";
                return;
            }
            _headState = CardHeadState.Ok;
        }

        /// <summary>释放文件句柄。</summary>
        public void Dispose()
        {
            _fs.Dispose();
        }
    }
}
