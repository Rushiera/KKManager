// 分片库预热——把「新建分片库的 WAL 生命周期」代价挪到页面加载后的后台空闲时段。
//
// 实测（C# · 2026-10-03 · 真库 · D 卷 exFAT）：新建分片库的一个写入周期 = 写时创建 -wal（≈0.66 s/片）
// + 关闭时 checkpoint 并删边车（≈1.66 s/片）；老库同一操作 ≈0，原因是它的 -wal 一直存在、从未被
// checkpoint 与删除。⇒ 代价只与「-wal 是否常驻」有关，因此预热 = 打开每个分片库、写一次、**保持连接打开**。
// 关掉就 checkpoint + 删边车，等于没预热。
//
// 纪律：静默 · 不占请求串行门 · 不碰共享连接 · 扫描进行中整段让路。
// 例外：库文件将被删除时（彻底删除数据）必须先释放对应库的常驻连接——占用中的文件删不掉。
package store

// WarmSentinelPath 预热哨兵行的路径——不是合法的 Windows 文件名（含尖括号），不可能与真实文件相撞。
const WarmSentinelPath = "<warmup>"

// OpenWarmLib 打开一片分片库做预热（缺文件先从内嵌模板释放，与扫描同源）——
// 返回的连接由调用方常驻（关闭即 checkpoint + 删边车，等于没预热）。
func OpenWarmLib(path, corePath string) (*DB, error) {
	if !fileExists(path) {
		ReleaseTemplate(path)
	}
	return openLib(path, corePath)
}

// WarmShard 预热一片分片库——用一次真实写入事务（插一行哨兵再删）把「新建库文件的首次写入」
// 代价先付掉；哨兵与删除在同一事务内完成，提交后不留痕（页已写过即达到预热目的）。
// 对齐 C# Store.WarmShard。
func (d *DB) WarmShard() error {
	if err := d.Begin(); err != nil {
		return err
	}
	if _, err := d.execer().Exec(`INSERT OR REPLACE INTO scan_state(file_path,step,size,mtime,done_at)
		VALUES(?,?,0,'',?)`, WarmSentinelPath, "warmup", Now()); err != nil {
		d.Rollback()
		return err
	}
	if _, err := d.execer().Exec("DELETE FROM scan_state WHERE file_path=?", WarmSentinelPath); err != nil {
		d.Rollback()
		return err
	}
	return d.Commit()
}
