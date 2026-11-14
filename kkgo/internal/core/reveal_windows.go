//go:build windows

// 资源管理器定位（Windows）——explorer /select 打开并选中文件。
package core

import "os/exec"

// RevealInExplorer 在资源管理器里定位并选中该文件。
func RevealInExplorer(path string) error {
	cmd := exec.Command("explorer.exe", "/select,"+path)
	return cmd.Start()
}
