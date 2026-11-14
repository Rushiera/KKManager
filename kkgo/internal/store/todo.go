package store

import (
	"database/sql"
	"strconv"
	"strings"
)

// TodoRow 待办行——对齐 C# Data/Store.cs §TodoRow。
type TodoRow struct {
	Id        int64
	Kind      string
	Key       string
	Text      string
	RefPath   string
	RefName   string
	CreatedAt string
}

// TodoTextLimit 待办正文长度上限（字符）——手输与卡片待办共用；超限在入口拒绝，不静默截断。
const TodoTextLimit = 200

// todoTable todo 表在本次连接里的引用名（表恒在主库）。
func (d *DB) todoTable() string {
	return d.coreTable("todo")
}

// coreTable 主库表在本次连接里的引用名（表恒在主库，库文件连接走 core 前缀）。
func (d *DB) coreTable(name string) string {
	if d.isCore {
		return name
	}
	return "core." + name
}

// ListTodoRows 全部待办（按登记顺序）——对齐 C# Store.ListTodos。
func (d *DB) ListTodoRows() ([]TodoRow, error) {
	rows, err := d.execer().Query("SELECT id, kind, key, text, ref_path, ref_name, created_at FROM " + d.todoTable() + " ORDER BY id")
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []TodoRow
	for rows.Next() {
		var t TodoRow
		var kind, key, text, refPath, refName, createdAt sql.NullString
		if err = rows.Scan(&t.Id, &kind, &key, &text, &refPath, &refName, &createdAt); err != nil {
			return nil, err
		}
		t.Kind, t.Key, t.Text = kind.String, key.String, text.String
		t.RefPath, t.RefName, t.CreatedAt = refPath.String, refName.String, createdAt.String
		list = append(list, t)
	}
	return list, rows.Err()
}

// AddTodo 登记一条待办——同类型 + 同键已存在则原样保留（重复自动离线不叠加消息）。
func (d *DB) AddTodo(kind, key, createdAt string) error {
	_, err := d.execer().Exec("INSERT INTO "+d.todoTable()+"(kind,key,created_at) VALUES(?,?,?) ON CONFLICT(kind,key) DO NOTHING", kind, key, createdAt)
	return err
}

// AddManualTodo 登记一条手输待办（正文去首尾空白后即关联键）——
// 同内容已存在则回错误文本、不落盘（重复出声，不静默吞）。返回空串 = 成功。
func (d *DB) AddManualTodo(text, createdAt string) (string, error) {
	body := strings.TrimSpace(text)
	if body == "" {
		return "待办内容不能为空", nil
	}
	if len([]rune(body)) > TodoTextLimit {
		return "待办内容太长（上限 " + strconv.Itoa(TodoTextLimit) + " 字）", nil
	}
	var dup int64
	if err := d.execer().QueryRow("SELECT COUNT(*) FROM "+d.todoTable()+" WHERE kind='manual' AND key=?", body).Scan(&dup); err != nil {
		return "", err
	}
	if dup > 0 {
		return "已经有一条一样的手输待办：" + body, nil
	}
	_, err := d.execer().Exec("INSERT INTO "+d.todoTable()+"(kind,key,text,created_at) VALUES('manual',?,?,?)", body, body, createdAt)
	return "", err
}

// AddCardTodo 登记一条卡片待办（卡片路径归一小写作关联键——一卡可多条，靠 id 区分）。
// 正文为空 / 超长回错误文本。返回空串 = 成功。
func (d *DB) AddCardTodo(cardPath, cardName, text, createdAt string) (string, error) {
	path := strings.TrimSpace(cardPath)
	body := strings.TrimSpace(text)
	if path == "" {
		return "缺少卡片路径", nil
	}
	if body == "" {
		return "待办内容不能为空", nil
	}
	if len([]rune(body)) > TodoTextLimit {
		return "待办内容太长（上限 " + strconv.Itoa(TodoTextLimit) + " 字）", nil
	}
	_, err := d.execer().Exec("INSERT INTO "+d.todoTable()+"(kind,key,text,ref_path,ref_name,created_at) VALUES('card',?,?,?,?,?)",
		strings.ToLower(path), body, path, cardName, createdAt)
	return "", err
}

// CloseTodo 关闭一条待办（按 id）——对齐 C# Store.CloseTodo。
func (d *DB) CloseTodo(id int64) error {
	_, err := d.execer().Exec("DELETE FROM "+d.todoTable()+" WHERE id=?", id)
	return err
}

// CloseTodoByKey 按类型 + 键关闭待办——库上线 / 被彻底删除时自动消解，不留痕。
func (d *DB) CloseTodoByKey(kind, key string) error {
	_, err := d.execer().Exec("DELETE FROM "+d.todoTable()+" WHERE kind=? AND key=?", kind, key)
	return err
}

// CountModOld 已登记旧版条数（dup 命令头部输出用）。
func (d *DB) CountModOld() (int64, error) {
	var n int64
	err := d.execer().QueryRow("SELECT COUNT(*) FROM " + d.coreTable("mod_old")).Scan(&n)
	return n, err
}
