// Windows 侧的面板辅助（打开浏览器）。
package web

import (
	"os/exec"
	"syscall"
)

// execStart 用系统默认程序打开 URL（对齐 C# Process.Start + UseShellExecute）。
func execStart(url string) error {
	cmd := exec.Command("rundll32", "url.dll,FileProtocolHandler", url)
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	return cmd.Start()
}
