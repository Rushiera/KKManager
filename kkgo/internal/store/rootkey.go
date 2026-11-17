// 库根口径（数据身份）——库序号与 root_path 列的唯一归并规则。
package store

import (
	"path/filepath"
	"strings"
)

// KkcOrganizeSubdir kkc 整理库的固定子目录名——接入开关登记的卡片根是 `<库根>\整理`，
// 而库序号与 root_path 一律用 `<库根>`（数据面）；两套路径不归并，同一个库会长出两批行
// （历史上 `…\kkc` = 5000 与 `…\kkc\整理` = 13000 就是这样分裂的）。
// 导出给 core（生成 kkc 运行态库根时同源，避免两处常量漂移）。
const KkcOrganizeSubdir = "整理"

// kkcDataSubdir 包内别名（RootKey 用）。
const kkcDataSubdir = KkcOrganizeSubdir

// RootKey 把路径归并到库根口径：`<x>\整理` → `<x>`，其余原样（去尾部空白与路径分隔符）。
// 判据：libdb 映射与 card/mod_file/non_card 的 root_path 只用库根；文件系统前缀过滤
// （scan_state / idx_meta 的 file_path LIKE）仍用真实遍历路径，不过这里。
func RootKey(path string) string {
	p := strings.TrimRight(strings.TrimSpace(path), `\/`)
	if p == "" {
		return ""
	}
	if strings.EqualFold(filepath.Base(p), kkcDataSubdir) {
		return strings.TrimRight(filepath.Dir(p), `\/`)
	}
	return p
}
