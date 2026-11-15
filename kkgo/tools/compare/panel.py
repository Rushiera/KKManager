"""kkgo ↔ C# 面板端点走查（G7 验收：同库同参数，响应字段级一致）。

判据（design-kkm-go.md §五）：
  - 端点存在性：C# 有 → Go 有，两侧状态码同档（200 / 404 / 400 / 405）；
  - 响应 JSON：键集 / 键序 / 类型 / 值一致（归一化：临时库路径、时间戳、耗时）；
  - 二进制端点：thumb / image / u3dtex 比 PNG 尺寸（编码器不同，字节不作判据）；
    card-src / entry 是直搬字节，比 SHA256。

用法（cwd 任意）：
  py tools\\compare\\panel.py            # 全量走查（造样本 → 两侧扫库 → 起两侧 serve → 逐端点比）
  py tools\\compare\\panel.py --probe    # 只打印关键响应原文（结构侦察，不比对）
  py tools\\compare\\panel.py --keep     # 保留临时目录

样本与库准备复用 compare.py 的辅助（_prepare_scanall_sample / _prepare_plugin_sample /
_trim_roots_for_scanall），保证与命令面对拍同源。
"""
import hashlib
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import compare as C  # noqa: E402

GO, CS = C.GO, C.CS
GO_PORT, CS_PORT = 18611, 18612
TMP = os.path.join(HERE, "_panel_tmp")

ID_KEYS = ("cardId", "id", "card_id")
LIB_KEYS = ("lib", "libId", "lib_id")


# ---------- 基础通道 ----------

def wait_port(port, timeout=40.0):
    """等端口可连。"""
    t0 = time.time()
    while time.time() - t0 < timeout:
        try:
            with socket.create_connection(("127.0.0.1", port), 0.5):
                return True
        except OSError:
            time.sleep(0.2)
    return False


def http(port, path, method="GET", body=None, timeout=180):
    """发一条请求，返回 (status, body bytes, content-type)。"""
    url = "http://127.0.0.1:%d%s" % (port, path)
    data = None
    headers = {}
    if body is not None:
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, r.read(), r.headers.get("Content-Type", "")
    except urllib.error.HTTPError as e:
        return e.code, e.read(), e.headers.get("Content-Type", "")
    except Exception as exc:  # noqa: BLE001 - 连接失败也要落成可比的回执
        return -1, ("%s" % exc).encode(), ""


def json_of(raw):
    """字节 → JSON；非 JSON 返回 None。"""
    try:
        return json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        return None


# ---------- 归一化 ----------

TS_RE = re.compile(r"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?")
MS_RE = re.compile(r"\d+(?:\.\d+)?\s*(?:ms|毫秒)\b")


def norm_text(s, side_dir):
    for p in (side_dir, side_dir.replace("\\", "/"), side_dir.replace("/", "\\")):
        s = s.replace(p, "<DBDIR>")
    s = TS_RE.sub("<TS>", s)
    s = MS_RE.sub("<MS>", s)
    return s


def norm_value(v, side_dir):
    if isinstance(v, dict):
        # 键序不是 HTTP JSON 契约判据（C# 按属性声明序、Go 按字母序）——统一排序后再比键集/值
        return {k: norm_value(v[k], side_dir) for k in sorted(v.keys())}
    if isinstance(v, list):
        return [norm_value(x, side_dir) for x in v]
    if isinstance(v, str):
        return norm_text(v, side_dir)
    return v


def find_first(obj, keys, default=None):
    """递归找第一个命中的键值（面板响应层级不固定时用）。"""
    if isinstance(obj, dict):
        for k in keys:
            if k in obj and obj[k] not in (None, "", 0):
                return obj[k]
        for v in obj.values():
            got = find_first(v, keys)
            if got is not None:
                return got
    elif isinstance(obj, list):
        for v in obj:
            got = find_first(v, keys)
            if got is not None:
                return got
    return default


def find_all(obj, keys, out=None):
    """递归收集所有命中键的值。"""
    out = [] if out is None else out
    if isinstance(obj, dict):
        for k, v in obj.items():
            if k in keys and v not in (None, "", 0):
                out.append(v)
            find_all(v, keys, out)
    elif isinstance(obj, list):
        for v in obj:
            find_all(v, keys, out)
    return out


def sha(raw):
    return hashlib.sha256(raw).hexdigest()[:16]


def png_dims(raw):
    """PNG / JPEG 尺寸；其它返回 None。"""
    if len(raw) >= 24 and raw[:8] == b"\x89PNG\r\n\x1a\n":
        import struct
        w, h = struct.unpack(">II", raw[16:24])
        return (w, h)
    if len(raw) >= 4 and raw[:2] == b"\xff\xd8":
        i = 2
        while i + 9 < len(raw):
            if raw[i] != 0xFF:
                i += 1
                continue
            marker = raw[i + 1]
            if marker in (0xD8, 0xD9) or 0xD0 <= marker <= 0xD7:
                i += 2
                continue
            seglen = (raw[i + 2] << 8) | raw[i + 3]
            if 0xC0 <= marker <= 0xCF and marker not in (0xC4, 0xC8, 0xCC):
                h = (raw[i + 5] << 8) | raw[i + 6]
                w = (raw[i + 7] << 8) | raw[i + 8]
                return (w, h)
            i += 2 + seglen
    return None


# ---------- 样本与两侧服务 ----------

def prepare_dbs():
    """造样本 + 两侧各建库各扫一轮（同参数），返回两侧库目录。"""
    if os.path.isdir(TMP):
        shutil.rmtree(TMP)
    os.makedirs(TMP)
    sample = C._prepare_scanall_sample(TMP)
    plugin = C._prepare_plugin_sample(TMP)
    if not sample:
        raise SystemExit("样本构造失败")
    dirs = {}
    for tag, exe, enc in (("cs", CS, "gbk"), ("go", GO, "utf-8")):
        d = os.path.join(TMP, tag)
        os.makedirs(d)
        db = os.path.join(d, "kkmanager.db")
        p = subprocess.run([exe, "roots-list", "--db", db], capture_output=True)
        if p.returncode != 0 or not os.path.exists(db):
            raise SystemExit("%s 建库失败：exit %d %s" % (tag, p.returncode, p.stdout.decode(enc, "replace")[:300]))
        C._trim_roots_for_scanall(db, sample, 4, plugin_root=plugin["root"])
        dirs[tag] = d
    env = dict(os.environ)
    env["KKM_SCAN_WORKERS"] = "1"
    for tag, exe, enc in (("cs", CS, "gbk"), ("go", GO, "utf-8")):
        db = os.path.join(dirs[tag], "kkmanager.db")
        p = subprocess.run([exe, "scan-main", "--db", db], capture_output=True, env=env, timeout=600)
        if p.returncode != 0:
            raise SystemExit("%s 扫库失败：exit %d %s" % (tag, p.returncode, p.stderr.decode(enc, "replace")[:300]))
    return dirs


def start_server(tag, exe, db_dir, port):
    db = os.path.join(db_dir, "kkmanager.db")
    p = subprocess.Popen([exe, "serve", "--port", str(port), "--db", db],
                         stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    if not wait_port(port):
        p.kill()
        raise SystemExit("%s serve 未就绪（端口 %d）" % (tag, port))
    return p


# ---------- 端点矩阵 ----------

STATIC = [
    ("GET", "/api/settings", None),
    ("GET", "/api/stats", None),
    ("GET", "/api/folders", None),
    ("GET", "/api/authors", None),
    ("GET", "/api/todos", None),
    ("GET", "/api/noncards", None),
    ("GET", "/api/dup", None),
    ("GET", "/api/scan/status", None),
    ("GET", "/api/scan/plan", None),
    ("GET", "/api/plugins", None),
    ("GET", "/api/plugins/match?keys=KKAPI", None),
    ("GET", "/api/plugins/log", None),
    ("GET", "/api/missing?per=50", None),
    ("GET", "/api/personalities", None),
    ("GET", "/api/weak-points", None),
    ("GET", "/api/sort/roots", None),
    ("GET", "/api/sort/status", None),
    ("GET", "/api/sort/plan", None),
    # 搬移端点（交接件 §三-2）：只走无副作用路径——status 初始态 / stop 幂等 / move 空清单拒绝；
    # 真搬运会动两侧共用的样本目录，不做进走查（功能面另用独立临时库手工验一次）
    ("GET", "/api/move-unused/status", None),
    ("POST", "/api/move-unused/stop", None),
    ("POST", "/api/move", {"guids": []}),
    ("GET", "/api/roots/recommend?gameRoot=D%3A%5CSteamLibrary%5CKoikatu", None),
    ("GET", "/api/cards?page=1&size=50&order=mtime&desc=true", None),
    ("GET", "/api/cards?page=1&size=50&order=chara", None),
    ("GET", "/api/mods?page=1&size=50", None),
]


def dynamic_endpoints(cards_json, mods_json, plugins_json):
    """取样本库里的卡片 id / lib（动态端点参数）；mod guid 在 build_entries 里另取。"""
    cid = find_first(cards_json, ID_KEYS)
    clib = find_first(cards_json, LIB_KEYS, 1)
    return cid, clib


def run_one(tag, method, path, body, mode, side_dir, port):
    """跑一个端点，返回可比的投影。"""
    st, raw, ctype = http(port, path, method, body)
    if mode == "png":
        dims = png_dims(raw)
        if dims:
            return st, {"kind": "png", "dims": dims}
        obj = json_of(raw)
        return st, {"kind": "other", "json": norm_value(obj, side_dir) if obj is not None else raw[:200].decode("utf-8", "replace")}
    if mode == "sha":
        return st, {"kind": "bytes", "sha": sha(raw), "len": len(raw)}
    obj = json_of(raw)
    if obj is None:
        return st, {"kind": "text", "text": norm_text(raw[:400].decode("utf-8", "replace"), side_dir), "ctype": ctype}
    return st, {"kind": "json", "json": norm_value(obj, side_dir)}


def _normalize_known(path, proj):
    """已拍板的已知不稳面归一（不改契约判据）：

    - /api/scan/plan 的 on 是 C# HashSet（字符串哈希按进程随机化，枚举序不稳）→ 按集合比；
    - /api/plugins 的 items / nonPluginItems 来自 `ORDER BY ..., guid, file_name` 而样本里
      file_name 全同（KKAPI.dll）——同键行的物理序取决于插入序（两侧扫描枚举序不同，C# 自身两次跑
      也可能不同）→ 按 filePath 归一后比（交接件 §四 拍板）。
    """
    if proj.get("kind") != "json":
        return proj
    obj = proj.get("json")
    if path.startswith("/api/scan/plan") and isinstance(obj, dict) and isinstance(obj.get("on"), list):
        obj["on"] = sorted(obj["on"])
    if path == "/api/plugins" and isinstance(obj, dict):
        items = obj.get("items")
        if isinstance(items, list):
            items.sort(key=lambda it: (str(it.get("filePath", "")), str(it.get("guid", ""))))
        non_plugins = obj.get("nonPluginItems")
        if isinstance(non_plugins, list):
            non_plugins.sort(key=lambda it: str(it.get("filePath", "")))
    return proj


def compare_endpoints(entries, side_dirs):
    """逐端点两侧比对；entries = [(label, method, path, body, mode)]。"""
    bad = []
    for label, method, path, body, mode in entries:
        st_cs, p_cs = run_one("cs", method, path, body, mode, side_dirs["cs"], CS_PORT)
        st_go, p_go = run_one("go", method, path, body, mode, side_dirs["go"], GO_PORT)
        p_cs = _normalize_known(path, p_cs)
        p_go = _normalize_known(path, p_go)
        # 版本串是 G9 才注入的（Go 现为 0.1.0-dev）——不属端点契约判据
        if path == "/api/settings":
            for proj in (p_cs, p_go):
                if proj.get("kind") == "json" and isinstance(proj["json"], dict) and "version" in proj["json"]:
                    proj["json"]["version"] = "<VER>"
        diffs = []
        if st_cs != st_go:
            diffs.append("状态码 %d vs %d" % (st_cs, st_go))
        diffs += C.diff_json(p_go, p_cs)
        if diffs:
            bad.append((label, diffs))
            print("  ✗ %-58s %s" % (label, "；".join(diffs[:3])))
        else:
            print("  ✓ %-58s %d" % (label, st_cs))
    return bad


def build_entries(side_dirs):
    """静态 + 动态端点清单（动态项以 C# 侧响应为准取 id / guid——两侧库同内容）。"""
    entries = [("%s %s" % (m, p), m, p, b, "json") for m, p, b in STATIC]
    st, raw, _ = http(CS_PORT, "/api/cards?page=1&size=5")
    cards_json = json_of(raw) or {}
    st, raw, _ = http(CS_PORT, "/api/mods?page=1&size=5")
    mods_json = json_of(raw) or {}
    st, raw, _ = http(CS_PORT, "/api/plugins")
    plugins_json = json_of(raw) or {}
    cid, clib = dynamic_endpoints(cards_json, mods_json, plugins_json)
    if cid:
        eps = [
            ("/api/card/%s?lib=%s" % (cid, clib), "GET", "/api/card/%s?lib=%s" % (cid, clib), None, "json"),
            ("/api/card/%s/refs" % cid, "GET", "/api/card/%s/refs?tier=0&lib=%s" % (cid, clib), None, "json"),
            ("/api/card/%s/structure" % cid, "GET", "/api/card/%s/structure?lib=%s" % (cid, clib), None, "json"),
            ("/api/card/%s/timeline" % cid, "GET", "/api/card/%s/timeline?lib=%s" % (cid, clib), None, "json"),
            ("/api/card/%s/params" % cid, "GET", "/api/card/%s/params?lib=%s" % (cid, clib), None, "json"),
            ("/api/card/%s/archives" % cid, "GET", "/api/card/%s/archives?lib=%s" % (cid, clib), None, "json"),
            ("/api/thumb/%s" % cid, "GET", "/api/thumb/%s?lib=%s" % (cid, clib), None, "png"),
            ("/api/card-src/%s" % cid, "GET", "/api/card-src/%s?lib=%s" % (cid, clib), None, "sha"),
        ]
        entries += eps
    # 卡片 image：从 structure 里挑第一个块（PNG 数据块），offset/size 原样传
    if cid:
        st, raw, _ = http(CS_PORT, "/api/card/%s/structure?lib=%s" % (cid, clib))
        stj = json_of(raw) or {}
        chunks = stj.get("chunks") or []
        pick = None
        for ch in chunks:
            if isinstance(ch, dict) and ch.get("type") == "IDAT" and (ch.get("length") or 0) > 0:
                pick = ch
                break
        if pick:
            p = ("/api/card/%s/image?lib=%s&offset=%s&size=%s&w=96"
                 % (cid, clib, pick.get("offset"), pick.get("length")))
            entries.append((p, "GET", p, None, "png"))
    # mod 条目面：composition 里挑一个图片条目 + 一个 unity3d 条目
    guid = find_first(mods_json, ("guid",))
    if guid:
        st, raw, _ = http(CS_PORT, "/api/mod/%s/composition" % guid)
        comp = json_of(raw) or {}
        files = find_all(comp, ("path",))
        img = next((f for f in files if isinstance(f, str) and f.lower().endswith((".png", ".jpg", ".bmp", ".gif"))), None)
        u3d = next((f for f in files if isinstance(f, str) and f.lower().endswith(".unity3d")), None)
        if img:
            p = "/api/mod/%s/entry?path=%s" % (guid, urllib.parse.quote(img))
            entries.append((p, "GET", p, None, "sha"))
        if u3d:
            p = "/api/mod/%s/u3d?path=%s" % (guid, urllib.parse.quote(u3d))
            entries.append((p, "GET", p, None, "json"))
    # 非卡缩略图：取第一条非卡登记（无图 → 两侧同 404）
    st, raw, _ = http(CS_PORT, "/api/noncards")
    nc = json_of(raw) or {}
    nid = find_first(nc, ID_KEYS)
    nlib = find_first(nc, LIB_KEYS, 1)
    if nid:
        p = "/api/noncard/thumb/%s?lib=%s" % (nid, nlib)
        entries.append((p, "GET", p, None, "png"))
    return entries, cid, clib, guid


def probe(ports):
    """结构侦察：打印关键响应原文（含状态码，截断）。"""
    for path in ("/api/cards?page=1&size=50&order=mtime&desc=1",
                 "/api/thumb/1?lib=2000000",
                 "/api/card-src/1?lib=2000000", "/api/noncard/thumb/1?lib=2000001",
                 "/api/settings", "/api/stats"):
        print("=" * 20, path)
        for tag, port in ports.items():
            st, raw, ctype = http(port, path)
            txt = raw.decode("utf-8", "replace")
            print("[%s] %d %s | %s" % (tag, st, ctype, txt[:400].replace("\n", " ")))


def main(argv):
    keep = "--keep" in argv
    is_probe = "--probe" in argv
    only = ""
    for i, a in enumerate(argv):
        if a == "--only" and i + 1 < len(argv):
            only = argv[i + 1]
    print("—— 面板走查（Go :%d ↔ C# :%d）" % (GO_PORT, CS_PORT))
    dirs = prepare_dbs()
    procs = []
    try:
        procs.append(start_server("cs", CS, dirs["cs"], CS_PORT))
        procs.append(start_server("go", GO, dirs["go"], GO_PORT))
        if is_probe:
            probe({"cs": CS_PORT, "go": GO_PORT})
            return 0
        entries, cid, clib, guid = build_entries(dirs)
        if only:
            entries = [e for e in entries if only in e[0]]
        print("  样本：card id=%s lib=%s · mod guid=%s" % (cid, clib, guid))
        bad = compare_endpoints(entries, dirs)
        print("判定：%d 项 · 一致 %d · 差异 %d" % (len(entries), len(entries) - len(bad), len(bad)))
        if bad:
            print("—— 差异明细")
            for label, diffs in bad:
                print("  %s" % label)
                for d in diffs[:10]:
                    print("    %s" % d)
        return 0 if not bad else 1
    finally:
        for p in procs:
            p.terminate()
            try:
                p.wait(timeout=10)
            except subprocess.TimeoutExpired:
                p.kill()
        if not keep:
            shutil.rmtree(TMP, ignore_errors=True)
        else:
            print("临时目录保留：%s" % TMP)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
