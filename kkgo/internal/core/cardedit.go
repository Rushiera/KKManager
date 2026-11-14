// 卡片数据区字段写入（G6 写面）——对齐 C# Core/CardEdit.cs §Apply / §RewriteFile / §Archive。
//
// 最小改动原则：只替换被编辑的字节，其余逐字节搬运；块长变化时同步块表 pos 与尾部载荷字节数；
// 写完后把文件的修改 / 访问时间与属性还原。
// 边界：只碰 Parameter 块与块表，不碰图片区与内嵌贴图。
package core

import (
	"encoding/binary"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"time"
)

// CardParamEdit 一次编辑的可选字段（nil = 不改这一项）。
type CardParamEdit struct {
	// LastName 姓（nil = 不改）。
	LastName *string
	// FirstName 名（nil = 不改）。
	FirstName *string
	// NickName 爱称（nil = 不改）。
	NickName *string
	// Personality 性格 ID（nil = 不改）。
	Personality *int
	// WeakPoint 敏感带（nil = 不改）。
	WeakPoint *int
	// Denial 五项「是否接受」（nil 项 = 不改）。
	Denial []*bool
}

// CardEditResult 一次写入的结果——Ok 为真才算写盘成功。
type CardEditResult struct {
	// Ok 是否写盘成功。
	Ok bool
	// Error 失败原因（空表示成功）。
	Error string
	// Changes 逐字段变更说明（中文，面板与 CLI 共用）。
	Changes []string
	// Delta 新块相对原块的字节增量（0 = 等长改动，走原地写）。
	Delta int
	// ArchivedFile 原版留档路径（空 = 未留档）。
	ArchivedFile string
	// ArchivedSize 留档字节数。
	ArchivedSize int64
	// NewSize 写入后的文件字节数。
	NewSize int64
}

// cardValueReplace 一处字节替换（At / Len 相对 Parameter 块起点）。
type cardValueReplace struct {
	At    int64
	Len   int
	Bytes []byte
}

// ApplyCardEdit 写入可编辑字段（nil = 不改这一项）。原版先留档到 archiveDir（非空时），
// 再最小改动写回，最后还原文件时间与属性。
func ApplyCardEdit(path string, layout *CardLayout, cur *CardParamInfo, edit CardParamEdit, archiveDir string) *CardEditResult {
	result := &CardEditResult{}
	if layout == nil {
		result.Error = "布局未解析"
		return result
	}
	if layout.Error != "" {
		result.Error = layout.Error
		return result
	}
	if cur == nil {
		result.Error = "字段未读取"
		return result
	}
	if cur.Error != "" {
		result.Error = cur.Error
		return result
	}
	if !cur.HasParameter {
		result.Error = "这张卡没有可编辑的 " + ParamBlockName + " 块"
		return result
	}

	// [段1] 逐字段算替换——值没变就不动（最小改动）
	reps := make([]cardValueReplace, 0, 8)
	addTextReplace(&reps, "姓", cur.LastName, edit.LastName, cur.LastNameAt, cur.LastNameLen, cur.BlockStart, &result.Changes)
	addTextReplace(&reps, "名", cur.FirstName, edit.FirstName, cur.FirstNameAt, cur.FirstNameLen, cur.BlockStart, &result.Changes)
	addTextReplace(&reps, "爱称", cur.NickName, edit.NickName, cur.NickNameAt, cur.NickNameLen, cur.BlockStart, &result.Changes)
	if edit.Personality != nil && *edit.Personality != cur.Personality {
		reps = append(reps, cardValueReplace{
			At: cur.PersonalityAt - cur.BlockStart, Len: cur.PersonalityLen, Bytes: msgPackInt(int64(*edit.Personality)),
		})
		result.Changes = append(result.Changes, "性格 "+ShowPersonalityID(cur.Personality)+" → "+ShowPersonalityID(*edit.Personality))
	}
	if edit.WeakPoint != nil && *edit.WeakPoint != cur.WeakPoint {
		if cur.WeakPointLen == 0 {
			result.Error = "这张卡里没有敏感带字段——未写盘"
			return result
		}
		reps = append(reps, cardValueReplace{
			At: cur.WeakPointAt - cur.BlockStart, Len: cur.WeakPointLen, Bytes: msgPackInt(int64(*edit.WeakPoint)),
		})
		result.Changes = append(result.Changes, "敏感带 "+showWeakPointName(cur.WeakPoint)+" → "+showWeakPointName(*edit.WeakPoint))
	}
	if edit.Denial != nil {
		for i := 0; i < len(DenialKeys) && i < len(edit.Denial); i++ {
			if edit.Denial[i] == nil || *edit.Denial[i] == cur.Denial[i] {
				continue
			}
			if cur.DenialLen[i] == 0 {
				result.Error = "这张卡里没有「接受" + DenialNames[i] + "」字段——未写盘"
				return result
			}
			reps = append(reps, cardValueReplace{
				At: cur.DenialAt[i] - cur.BlockStart, Len: cur.DenialLen[i], Bytes: msgPackBool(*edit.Denial[i]),
			})
			result.Changes = append(result.Changes, "是否接受「"+DenialNames[i]+"」 "+showBoolText(cur.Denial[i])+" → "+showBoolText(*edit.Denial[i]))
		}
	}
	if len(reps) == 0 {
		result.Error = "没有字段发生变化——未写盘"
		return result
	}
	if cur.BlockSize > 16777216 {
		result.Error = "Parameter 块过大（" + itoaSmall64(cur.BlockSize) + " 字节）——拒绝改写"
		return result
	}

	// [段2] 组装新块字节（按偏移升序拼接）
	block := readCardRange(path, cur.BlockStart, int(cur.BlockSize))
	if block == nil {
		result.Error = "Parameter 块读取失败"
		return result
	}
	sort.SliceStable(reps, func(i, j int) bool { return reps[i].At < reps[j].At })
	newBlock := make([]byte, 0, len(block)+16)
	var cursor int64
	for _, r := range reps {
		if r.At < cursor || r.At+int64(r.Len) > cur.BlockSize {
			result.Error = "字段区间越界（@" + itoaSmall64(r.At) + " +" + itoaSmall(r.Len) + "）——拒绝改写"
			return result
		}
		newBlock = append(newBlock, block[cursor:r.At]...)
		newBlock = append(newBlock, r.Bytes...)
		cursor = r.At + int64(r.Len)
	}
	newBlock = append(newBlock, block[cursor:]...)
	result.Delta = len(newBlock) - len(block)

	// [段3] 原版留档（原版留在软件内部）+ 写盘
	fi, err := os.Stat(path)
	if err != nil {
		result.Error = "读不到卡片文件属性：" + err.Error()
		return result
	}
	written := fi.ModTime()
	accessed := fi.ModTime()
	if !archiveDirEmpty(archiveDir) {
		archived := ArchiveCard(path, archiveDir, time.Now())
		if archived == "" {
			result.Error = "原版留档失败——未写盘（留档目录不可写？" + archiveDir + "）"
			return result
		}
		result.ArchivedFile = archived
		if st, statErr := os.Stat(archived); statErr == nil {
			result.ArchivedSize = st.Size()
		}
	}
	if result.Delta == 0 {
		f, openErr := os.OpenFile(path, os.O_WRONLY, 0)
		if openErr != nil {
			result.Error = "打开卡片失败" + FileBusyHint(openErr)
			return result
		}
		_, writeErr := f.WriteAt(newBlock, cur.BlockStart)
		if writeErr == nil {
			writeErr = f.Sync()
		}
		f.Close()
		if writeErr != nil {
			result.Error = "原地写入失败" + FileBusyHint(writeErr)
			return result
		}
	} else {
		if werr := rewriteCardFile(path, layout, cur, newBlock, int64(result.Delta)); werr != "" {
			result.Error = werr
			return result
		}
	}
	if st, statErr := os.Stat(path); statErr == nil {
		result.NewSize = st.Size()
	} else {
		result.NewSize = layout.FileSize + int64(result.Delta)
	}

	// [段4] 还原时间（附加改动改回跟原来一样）
	_ = os.Chtimes(path, accessed, written)
	result.Ok = true
	return result
}

// rewriteCardFile 整文件重写——块长变化时用：头段原样搬运，块表与尾部按新值重编码，载荷逐块搬。
func rewriteCardFile(path string, layout *CardLayout, cur *CardParamInfo, newBlock []byte, delta int64) string {
	orig := make([]CardEditBlock, 0, len(layout.Blocks))
	next := make([]CardEditBlock, 0, len(layout.Blocks))
	var payloadSize int64
	for _, b := range layout.Blocks {
		orig = append(orig, b)
		n := b
		if b.Name == ParamBlockName {
			n.Size = int64(len(newBlock))
		} else if b.Pos > cur.BlockPos {
			n.Pos = b.Pos + delta
		}
		payloadSize += n.Size
		next = append(next, n)
	}
	sort.SliceStable(orig, func(i, j int) bool { return orig[i].Pos < orig[j].Pos })
	table := encodeCardTable(next)
	newTailRaw := payloadSize + layout.TailConst
	tmp := path + ".kcmedit.tmp"
	src, err := os.Open(path)
	if err != nil {
		return "整文件重写失败" + FileBusyHint(err)
	}
	dst, err := os.OpenFile(tmp, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0o644)
	if err != nil {
		src.Close()
		return "整文件重写失败" + FileBusyHint(err)
	}
	fail := func(msg string) string {
		dst.Close()
		src.Close()
		if _, statErr := os.Stat(tmp); statErr == nil {
			if rmErr := os.Remove(tmp); rmErr != nil {
				fmt.Println("[卡片编辑] 临时文件清理失败：" + rmErr.Error())
			}
		}
		return msg
	}
	if err := copyFileRange(src, dst, 0, layout.TableSizeAt); err != nil {
		return fail("整文件重写失败：" + err.Error())
	}
	if err := writeInt32LE(dst, int64(len(table))); err != nil {
		return fail("整文件重写失败：" + err.Error())
	}
	if _, err := dst.Write(table); err != nil {
		return fail("整文件重写失败：" + err.Error())
	}
	if err := writeInt32LE(dst, newTailRaw); err != nil {
		return fail("整文件重写失败：" + err.Error())
	}
	if err := writeInt32LE(dst, layout.TailSecond); err != nil {
		return fail("整文件重写失败：" + err.Error())
	}
	for _, b := range orig {
		if b.Name == ParamBlockName {
			if _, err := dst.Write(newBlock); err != nil {
				return fail("整文件重写失败：" + err.Error())
			}
			continue
		}
		if err := copyFileRange(src, dst, layout.PayloadStart+b.Pos, b.Size); err != nil {
			return fail("整文件重写失败：" + err.Error())
		}
	}
	if err := dst.Sync(); err != nil {
		return fail("整文件重写失败：" + err.Error())
	}
	dst.Close()
	src.Close()
	if err := os.Rename(tmp, path); err != nil {
		if _, statErr := os.Stat(tmp); statErr == nil {
			_ = os.Remove(tmp)
		}
		return "整文件重写失败（换名）" + FileBusyHint(err)
	}
	return ""
}

// ArchiveCard 把原文件复制进留档目录（原版留在软件内部）；失败返回空串（调用方出声）。
func ArchiveCard(path, archiveDir string, stamp time.Time) string {
	if err := os.MkdirAll(archiveDir, 0o755); err != nil {
		fmt.Println("[卡片编辑] 留档失败：" + err.Error())
		return ""
	}
	base := filepath.Base(path)
	ext := filepath.Ext(base)
	stem := base[:len(base)-len(ext)]
	target := filepath.Join(archiveDir, stamp.Format("20060102_150405_000")+"_"+stem+ext)
	src, err := os.Open(path)
	if err != nil {
		fmt.Println("[卡片编辑] 留档失败：" + err.Error())
		return ""
	}
	defer src.Close()
	dst, err := os.OpenFile(target, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0o644)
	if err != nil {
		fmt.Println("[卡片编辑] 留档失败：" + err.Error())
		return ""
	}
	if _, err := io.Copy(dst, src); err != nil {
		dst.Close()
		fmt.Println("[卡片编辑] 留档失败：" + err.Error())
		return ""
	}
	if err := dst.Close(); err != nil {
		fmt.Println("[卡片编辑] 留档失败：" + err.Error())
		return ""
	}
	return target
}

// addTextReplace 把字符串字段加进替换清单（值没变 / 未指定则不加）。
func addTextReplace(reps *[]cardValueReplace, label, oldValue string, newValue *string, valueAt int64, valueLen int, blockStart int64, changes *[]string) {
	if newValue == nil {
		return
	}
	if oldValue == *newValue {
		return
	}
	*reps = append(*reps, cardValueReplace{At: valueAt - blockStart, Len: valueLen, Bytes: msgPackStr(*newValue)})
	*changes = append(*changes, label+" "+showText(oldValue)+" → "+showText(*newValue))
}

// encodeCardTable 编码块表（与游戏同形：{"lstInfo":[{name,version,pos,size}…]}）——值未变时与原表逐字节相同。
func encodeCardTable(blocks []CardEditBlock) []byte {
	out := make([]byte, 0, 64+len(blocks)*48)
	out = append(out, 0x81)
	out = appendMsgStr(out, "lstInfo")
	out = appendArrayHeader(out, len(blocks))
	for _, b := range blocks {
		out = append(out, 0x84)
		out = appendMsgStr(out, "name")
		out = appendMsgStr(out, b.Name)
		out = appendMsgStr(out, "version")
		out = appendMsgStr(out, b.Version)
		out = appendMsgStr(out, "pos")
		out = append(out, msgPackInt(b.Pos)...)
		out = appendMsgStr(out, "size")
		out = append(out, msgPackInt(b.Size)...)
	}
	return out
}

// appendArrayHeader 写数组头（≤15 用 fixarray，其余 array16）。
func appendArrayHeader(out []byte, count int) []byte {
	if count <= 15 {
		return append(out, byte(0x90|count))
	}
	return append(out, 0xDC, byte(count>>8), byte(count))
}

// appendMsgStr 写 MessagePack 字符串（最短形态）。
func appendMsgStr(out []byte, s string) []byte {
	body := []byte(s)
	switch {
	case len(body) <= 31:
		out = append(out, byte(0xA0|len(body)))
	case len(body) <= 255:
		out = append(out, 0xD9, byte(len(body)))
	case len(body) <= 65535:
		out = append(out, 0xDA, byte(len(body)>>8), byte(len(body)))
	default:
		out = append(out, 0xDB, byte(len(body)>>24), byte(len(body)>>16), byte(len(body)>>8), byte(len(body)))
	}
	return append(out, body...)
}

// msgPackStr 把字符串编成 MessagePack（最短形态——与游戏写卡片时一致）。
func msgPackStr(s string) []byte { return appendMsgStr(nil, s) }

// msgPackInt 把整数编成 MessagePack（最短形态——与游戏写卡片时一致）。
func msgPackInt(v int64) []byte {
	if v >= 0 {
		switch {
		case v <= 127:
			return []byte{byte(v)}
		case v <= 255:
			return []byte{0xCC, byte(v)}
		case v <= 65535:
			return []byte{0xCD, byte(v >> 8), byte(v)}
		case v <= 4294967295:
			return []byte{0xCE, byte(v >> 24), byte(v >> 16), byte(v >> 8), byte(v)}
		default:
			return []byte{0xCF, byte(v >> 56), byte(v >> 48), byte(v >> 40), byte(v >> 32), byte(v >> 24), byte(v >> 16), byte(v >> 8), byte(v)}
		}
	}
	switch {
	case v >= -32:
		return []byte{byte(0x100 + v)}
	case v >= -128:
		return []byte{0xD0, byte(v)}
	case v >= -32768:
		return []byte{0xD1, byte(v >> 8), byte(v)}
	case v >= -2147483648:
		return []byte{0xD2, byte(v >> 24), byte(v >> 16), byte(v >> 8), byte(v)}
	default:
		return []byte{0xD3, byte(v >> 56), byte(v >> 48), byte(v >> 40), byte(v >> 32), byte(v >> 24), byte(v >> 16), byte(v >> 8), byte(v)}
	}
}

// msgPackBool 把布尔编成 MessagePack（true = 0xC3 / false = 0xC2）。
func msgPackBool(v bool) []byte {
	if v {
		return []byte{0xC3}
	}
	return []byte{0xC2}
}

// writeInt32LE 写小端 32 位整数（卡片头段与尾部字段的字节序）。
func writeInt32LE(w io.Writer, value int64) error {
	var buf [4]byte
	binary.LittleEndian.PutUint32(buf[:], uint32(value))
	_, err := w.Write(buf[:])
	return err
}

// copyFileRange 从源文件搬一段到目标流（按 1 MB 分块）。
func copyFileRange(src *os.File, dst io.Writer, at, count int64) error {
	if at < 0 || count < 0 {
		return fmt.Errorf("区间非法 @%d +%d", at, count)
	}
	if _, err := src.Seek(at, io.SeekStart); err != nil {
		return err
	}
	var left = count
	buf := make([]byte, 1<<20)
	for left > 0 {
		take := int64(len(buf))
		if left < take {
			take = left
		}
		n, err := src.Read(buf[:take])
		if n > 0 {
			if _, werr := dst.Write(buf[:n]); werr != nil {
				return werr
			}
			left -= int64(n)
		}
		if err != nil {
			if err == io.EOF && left == 0 {
				break
			}
			return fmt.Errorf("读取源文件失败 @%d 剩 %d 字节", at+count-left, left)
		}
		if n == 0 {
			return fmt.Errorf("读取源文件失败 @%d 剩 %d 字节", at+count-left, left)
		}
	}
	return nil
}

// archiveDirEmpty 留档目录参数是否为空（空 = 不留档）。
func archiveDirEmpty(dir string) bool {
	return len(trimSpace(dir)) == 0
}

// showText 字段值显示文本（空值标注，避免「空 vs 未改」看不出来）。
func showText(s string) string {
	if s == "" {
		return "<空>"
	}
	return "「" + s + "」"
}

// showBoolText 布尔字段的显示文本（是 / 否）。
func showBoolText(v bool) string {
	if v {
		return "是"
	}
	return "否"
}

// ShowPersonalityID 性格显示文本（名称 + ID；对齐 C# CardEdit.ShowPersonality）。
func ShowPersonalityID(id int) string {
	name, known := PersonalityNameOf(id)
	tag := itoaSmall(id)
	if !known || name == "" {
		return "未收录（ID " + tag + "）"
	}
	return name + "（ID " + tag + "）"
}

// showWeakPointName 敏感带显示文本（名称 + ID；表外显示为未命名）。
func showWeakPointName(id int) string {
	name, known := WeakPointNameOf(id)
	if !known || name == "" {
		return "未命名（值 " + itoaSmall(id) + "）"
	}
	return name
}
