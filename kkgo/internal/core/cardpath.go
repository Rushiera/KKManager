// 卡片路径解析（面板共用）——对齐 C# WebApp.CardPathOf：库位 + 卡片 id → 磁盘路径。
package core

import (
	"kkgo/internal/store"
)

// CardPathOf 按库位 + 卡片 id 取卡片文件路径；失败返回空串与原因文本。
func CardPathOf(hub *store.Hub, cfg *RootsConfig, lib int, id int64) (string, string) {
	_ = cfg
	s, libErr := hub.StoreByLib(lib)
	if libErr != nil || s == nil {
		return "", "找不到库 " + itoaSmall(lib)
	}
	row, err := s.GetCardListRow(id)
	if err != nil {
		return "", "读卡片行失败：" + err.Error()
	}
	if row.FilePath == "" {
		return "", "卡片行没有文件路径（索引与磁盘不一致）"
	}
	if !fileExists(row.FilePath) {
		return "", "卡片文件不在磁盘上（需重扫）"
	}
	return row.FilePath, ""
}
