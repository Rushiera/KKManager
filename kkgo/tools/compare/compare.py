"""kkgo ↔ C# 对拍工具（G 段验收：同输入同参数，输出数据字段级一致）。

判据（design-kkm-go.md §五）：
  - 退出码相等；
  - 行数相等；
  - 每行剔除非 ASCII 后逐字相同（C# 控制台中文是 GBK 输出被按 UTF-8 读，属编码伪差异，
    故两侧都按各自真实编码解码后再做 ASCII 投影）；
  - thumb 例外：只比尺寸 / 卡类型 / 图片区（编码器不同，字节数不作判据）。

用法：
  py compare.py all                        # 批量跑 CASES 里全部命令 + card-analysis / roots / libmap / composition 专项
  py compare.py card-detail <card.png>     # 单命令对拍
  py compare.py thumb                      # 缩略图专项（Go 与 C# 各出 jpg 再比尺寸）
  py compare.py timeline                   # timeline 轨道专项（自动挑关键帧最多的场景卡）
  py compare.py card-analysis [card.png]   # 分项分析落库 JSON 对拍（C# 侧无同名命令——见下）
  py compare.py libmap                     # 分片库读面对拍（Python 按 C# 规则直读库算副本清单 vs Go modfiles）
  py compare.py composition                # 组成档案对拍（真库复制两份，C# / Go 各自建档→输出行 + 落库字段）

card-analysis 的判据（C# 命令面没有 card-analysis，只能对产物）：
  1) 临时库：把样本卡复制到临时目录，C# 侧 roots-add + scan-plan set（开 coord/detail/scene）+ scan-cards；
  2) 取 C# 落库的 card_analysis.data（System.Text.Json 产物）；
  3) 与 Go 的 card-analysis 输出逐分项做字段级比对（键集 / 键序 / 值 / 类型）。

样本路径是本机现值，可在文件顶部 SAMPLE_* 里改，或用第三参显式给。
"""
import glob
import json
import os
import re
import shutil
import sqlite3
import subprocess
import sys
import zipfile

# 输出统一 UTF-8：Windows 中文控制台默认 cp936，回执端按 UTF-8 解码会成乱码（只影响本进程 stdout，
# 不影响两侧子进程的真实编码解码——那两个在 projection 里各自显式处理）。
try:
    sys.stdout.reconfigure(encoding="utf-8")
except AttributeError:
    pass

GO = r"D:\Mau\WorkSpace\KKManager\dist\KMR.exe"
# 对拍基准 = C# 0.21.9 归档产物（2026-11-17 从 dist 双槽移入 raw/dist-slots；A 槽 0.21.6 为更早回退件）。
# 0.21.6 的 exe 缺 refs-read / card-sort / index-import / idx / sort / u3d-db 等命令——新命令对拍必须用 0.21.9。
CS = r"D:\Mau\WorkSpace\KKManager\raw\dist-slots\KMR_B.exe"
SAMPLE_CARD = r"D:\SteamLibrary\Koikatu\UserData\chara\female\100065348_p6.png"
SAMPLE_SCENE = r"D:\SteamLibrary\Koikatu\UserData\Studio\scene\2021_0403_1423_03_837.png"
SAMPLE_MOD = r"D:\SteamLibrary\Koikatu\Mods\Cissoia.zipmod"
SAMPLE_PLUGIN = r"D:\SteamLibrary\Koikatu\BepInEx\plugins\KKAPI.dll"
SAMPLE_NONPLUGIN = r"D:\SteamLibrary\Koikatu\BepInEx\core\0Harmony.dll"
REAL_DB = r"D:\Mau\WorkSpace\KKManager\dist\data\kkmanager.db"
SCENE_DIR = r"D:\SteamLibrary\Koikatu\UserData\Studio\scene"
MOD_LIB_SRC = r"D:\SteamLibrary\Koikatu\Mods"
OUT_DIR = os.path.dirname(os.path.abspath(__file__))

CASES = [
    ("modinfo", [SAMPLE_MOD]),
    ("cardinfo", [SAMPLE_CARD]),
    ("card-structure", [SAMPLE_CARD]),
    ("card-detail", [SAMPLE_CARD]),
    ("card-coord", [SAMPLE_CARD]),
    ("card-coord", [SAMPLE_CARD, "--all"]),
    ("card-params", [SAMPLE_CARD]),
    ("card-scene", [SAMPLE_SCENE]),
    ("card-timeline", [SAMPLE_SCENE]),
    ("card-timeline", [SAMPLE_SCENE, "tracks"]),
    ("plugin-info", [SAMPLE_PLUGIN]),
    ("plugin-info", [SAMPLE_NONPLUGIN]),
    ("plugins", ["--db", REAL_DB]),
    ("plugins", ["--db", REAL_DB, "--unnoted"]),
    ("plugin-config", ["--db", REAL_DB]),
    ("plugin-config", ["BepInEx.EnableResize", "--db", REAL_DB]),
    ("plugin-match", ["vnge_sss", "kkpe", "timeline", "materialeditor", "does_not_exist_key", "--db", REAL_DB]),
]

# 扫描计划全集顺序（临时库准备时用——内置顺序，见 C# ScanPlanCatalog.Defs）
ORDER_ALL = "row,modrow,head,refs,name,timeline,coord,thumb,detail,scene,composition,u3d"
# 分项分析组（可选组——全开才生效：coord + detail + scene）
ORDER_ANALYZE = "coord,detail,scene"


def projection(raw, encoding):
    lines = raw.decode(encoding, "replace").splitlines()
    # 非 ASCII 统一映射为 `?`：C# 控制台是 GBK（不可表示字符编码为 ?），Go 是 UTF-8——
    # 两侧都落到同一占位符，编码伪差异被剔除，而「一侧有字符、另一侧无」仍能显示为差异。
    return ["".join(ch if ord(ch) < 128 else "?" for ch in ln) for ln in lines]


def compare_one(cmd, args):
    p_go = subprocess.run([GO, cmd] + args, capture_output=True)
    p_cs = subprocess.run([CS, cmd] + args, capture_output=True)
    a = projection(p_go.stdout, "utf-8")
    b = projection(p_cs.stdout, "gbk")
    diff = 0
    for i in range(max(len(a), len(b))):
        la = a[i] if i < len(a) else "<缺失>"
        lb = b[i] if i < len(b) else "<缺失>"
        if la != lb:
            diff += 1
    ok = diff == 0 and len(a) == len(b) and p_go.returncode == p_cs.returncode
    return ok, p_go.returncode, p_cs.returncode, len(a), len(b), diff


def cmd_all():
    bad = 0
    for cmd, args in CASES:
        ok, rgo, rcs, ngo, ncs, diff = compare_one(cmd, args)
        if not ok:
            bad += 1
        tail = args[-1] if len(args) > 1 else ""
        print("%-14s %-12s exit %d/%d 行 %d/%d 差异 %d → %s"
              % (cmd, tail, rgo, rcs, ngo, ncs, diff, "一致" if ok else "不一致"))
    if cmd_cardanalysis(SAMPLE_CARD) != 0:
        bad += 1
    if cmd_roots() != 0:
        bad += 1
    if cmd_libmap() != 0:
        bad += 1
    if cmd_composition() != 0:
        bad += 1
    if cmd_pluginlog() != 0:
        bad += 1
    if cmd_stats() != 0:
        bad += 1
    if cmd_scanmods() != 0:
        bad += 1
    if cmd_scancards() != 0:
        bad += 1
    if cmd_scanall() != 0:
        bad += 1
    if cmd_scancardsanalysis() != 0:
        bad += 1
    if cmd_scanallplugins() != 0:
        bad += 1
    if cmd_scanplugins() != 0:
        bad += 1
    if cmd_query() != 0:
        bad += 1
    if cmd_dup_new() != 0:
        bad += 1
    if cmd_refs() != 0:
        bad += 1
    if cmd_index() != 0:
        bad += 1
    if cmd_sort() != 0:
        bad += 1
    if cmd_cardsort() != 0:
        bad += 1
    if cmd_cardsort2() != 0:
        bad += 1
    if cmd_cardedit() != 0:
        bad += 1
    if cmd_u3d() != 0:
        bad += 1
    print("合计 %d 项 · 不一致 %d 项" % (len(CASES) + 22, bad))
    return 0 if bad == 0 else 1


def cmd_single(cmd, args):
    ok, rgo, rcs, ngo, ncs, diff = compare_one(cmd, args)
    print("命令 %s 附加 %s" % (cmd, args[1:]))
    print("exit go=%d cs=%d · 行 %d/%d · 差异 %d" % (rgo, rcs, ngo, ncs, diff))
    if diff:
        a = projection(subprocess.run([GO, cmd] + args, capture_output=True).stdout, "utf-8")
        b = projection(subprocess.run([CS, cmd] + args, capture_output=True).stdout, "gbk")
        shown = 0
        for i in range(max(len(a), len(b))):
            la = a[i] if i < len(a) else "<缺失>"
            lb = b[i] if i < len(b) else "<缺失>"
            if la != lb:
                print("  [%d] Go %r" % (i, la))
                print("      C# %r" % (lb,))
                shown += 1
                if shown >= 8:
                    break
    print("判定：%s" % ("一致" if ok else "不一致"))
    return 0 if ok else 1


RE_AREA = re.compile(r"图片区\s*:\s*([\d,]+) 字节")
RE_SIZE = re.compile(r"缩略图\s*:\s*([\d,]+) 字节\s+(\S+)")
RE_TYPE = re.compile(r"卡类型\s*:\s*(\S+)")


def cmd_thumb():
    out_go = os.path.join(OUT_DIR, "thumb-go.jpg")
    out_cs = os.path.join(OUT_DIR, "thumb-cs.jpg")
    p_go = subprocess.run([GO, "thumb", SAMPLE_CARD, out_go], capture_output=True)
    p_cs = subprocess.run([CS, "thumb", SAMPLE_CARD, out_cs], capture_output=True)
    t_go = p_go.stdout.decode("utf-8", "replace") or p_go.stderr.decode("utf-8", "replace")
    t_cs = p_cs.stdout.decode("gbk", "replace") or p_cs.stderr.decode("gbk", "replace")
    ok = p_go.returncode == p_cs.returncode
    for name, rx, gi in (("卡类型", RE_TYPE, 1), ("图片区", RE_AREA, 1), ("尺寸", RE_SIZE, 2)):
        mv, mc = rx.search(t_go), rx.search(t_cs)
        gv = mv.group(gi) if mv else None
        cv = mc.group(gi) if mc else None
        if gv != cv:
            ok = False
        print("%s go=%r cs=%r %s" % (name, gv, cv, "同" if gv == cv else "异"))
    print("判定：%s（字节数差异属编码器不同，不作判据）" % ("一致" if ok else "不一致"))
    return 0 if ok else 1


def cmd_timeline():
    rx = re.compile(r"关键帧\s*:\s*([\d,]+) 个")
    best, best_kf = None, -1
    for name in sorted(os.listdir(SCENE_DIR)):
        if not name.lower().endswith(".png"):
            continue
        path = os.path.join(SCENE_DIR, name)
        p = subprocess.run([GO, "card-timeline", path], capture_output=True)
        m = rx.search(p.stdout.decode("utf-8", "replace"))
        if not m:
            continue
        kf = int(m.group(1).replace(",", ""))
        if kf > best_kf:
            best, best_kf = path, kf
    if not best:
        print("没有找到非空 timeline 的场景卡")
        return 2
    print("样本 %s（关键帧 %d）" % (best, best_kf))
    return cmd_single("card-timeline", [best, "tracks"])


def diff_json(a, b, path="$", out=None):
    """逐层比对两份 JSON 值：键集 / 键序 / 类型 / 值（C# 按属性声明序输出，键序也是判据）。"""
    out = [] if out is None else out
    if len(out) > 20:
        return out
    if isinstance(a, bool) != isinstance(b, bool):
        out.append("%s 类型 %s vs %s" % (path, type(a).__name__, type(b).__name__))
    elif isinstance(a, dict) and isinstance(b, dict):
        ka, kb = list(a.keys()), list(b.keys())
        only_a = [k for k in ka if k not in kb]
        only_b = [k for k in kb if k not in ka]
        if only_a or only_b:
            out.append("%s 键差异 仅Go=%s 仅C#=%s" % (path, only_a[:10], only_b[:10]))
        elif ka != kb:
            out.append("%s 键序差异 %s vs %s" % (path, ka[:12], kb[:12]))
        for k in ka:
            if k in b:
                diff_json(a[k], b[k], path + "." + k, out)
                if len(out) > 20:
                    return out
    elif isinstance(a, list) and isinstance(b, list):
        if len(a) != len(b):
            out.append("%s 长度 %d vs %d" % (path, len(a), len(b)))
        for i in range(min(len(a), len(b))):
            diff_json(a[i], b[i], "%s[%d]" % (path, i), out)
            if len(out) > 20:
                return out
    elif a != b:
        out.append("%s 值 %r vs %r" % (path, a, b))
    return out


def read_analysis_rows(dbdir):
    """读临时库里全部 card_analysis 行（分片库里，按 kind 取 data）。"""
    rows = {}
    for p in glob.glob(os.path.join(dbdir, "*.db")):
        try:
            con = sqlite3.connect("file:" + p.replace("\\", "/") + "?mode=ro", uri=True)
            cur = con.cursor()
            cur.execute("select kind, data from card_analysis")
            for kind, data in cur.fetchall():
                rows[kind] = data or ""
            con.close()
        except sqlite3.Error:
            continue
    return rows


def run_lines(exe, cmd, args, encoding):
    """跑一条命令，返回 ASCII 投影后的行（编码伪差异剔除）。"""
    p = subprocess.run([exe, cmd] + args, capture_output=True)
    return projection(p.stdout, encoding), p.returncode


def write_both(exe, cmd, args_go, args_cs):
    """两侧各跑一遍写操作（同一套语义参数）。"""
    pa = subprocess.run([exe, cmd] + args_go, capture_output=True)
    return pa.returncode, pa.stdout.decode("gbk", "replace") + pa.stderr.decode("gbk", "replace")


def cmd_roots():
    """库根交互相：C# 建库 → 同一操作分别由 C# / Go 写到两份库 → 四向读回比对。"""
    tmp = os.path.join(OUT_DIR, "_roots_tmp")
    if os.path.isdir(tmp):
        shutil.rmtree(tmp)
    os.makedirs(tmp)
    base = os.path.join(tmp, "base.db")
    a_db = os.path.join(tmp, "a.db")
    b_db = os.path.join(tmp, "b.db")
    sub_extra = os.path.join(tmp, "extra")
    sub_cold = os.path.join(tmp, "cold")
    sub_cache = os.path.join(tmp, "mycache")
    game_root = r"X:\KKTest\Game"
    # C# 建库（从内嵌模板释放全量 schema）
    subprocess.run([CS, "roots-list", "--db", base], capture_output=True)
    if not os.path.exists(base):
        print("C# 侧建库失败：%s 不存在" % base)
        return 3
    shutil.copy2(base, a_db)
    shutil.copy2(base, b_db)
    ops = [
        ("roots-add", ["cards", "1", sub_extra, "仅本目录"]),
        ("roots-add", ["mods", "3", sub_cold, "只读"]),
        ("set-game-root", [game_root]),
        ("roots-add", ["mods", "2", sub_cache]),
        ("roots-clear", ["cards"]),
        ("scan-plan", ["set", ORDER_ALL, ORDER_ANALYZE]),
    ]
    for cmd, args in ops:
        rc_cs, _ = write_both(CS, cmd, args + ["--db", a_db], args)
        rc_go = subprocess.run([GO, cmd] + args + ["--db", b_db], capture_output=True).returncode
        if rc_cs != rc_go:
            print("写操作退出码不一致：%s C#=%d Go=%d" % (cmd, rc_cs, rc_go))
            return 1
    ok = True
    for title, db in (("C# 写入后（Go 读 C# 的 JSON）", a_db), ("Go 写入后（C# 读 Go 的 JSON）", b_db)):
        lines_cs, rc_cs = run_lines(CS, "roots-list", ["--db", db], "gbk")
        lines_go, rc_go = run_lines(GO, "roots-list", ["--db", db], "utf-8")
        diff = [i for i in range(max(len(lines_cs), len(lines_go)))
                if (lines_cs[i] if i < len(lines_cs) else "<缺失>") != (lines_go[i] if i < len(lines_go) else "<缺失>")]
        print("%s：exit %d/%d · 行 %d/%d · 差异 %d → %s"
              % (title, rc_cs, rc_go, len(lines_cs), len(lines_go), len(diff),
                 "一致" if not diff else "不一致"))
        if diff:
            ok = False
            for i in diff[:6]:
                print("    [%d] C# %r" % (i, lines_cs[i] if i < len(lines_cs) else "<缺失>"))
                print("         Go %r" % (lines_go[i] if i < len(lines_go) else "<缺失>"))
    a_cs, _ = run_lines(CS, "roots-list", ["--db", a_db], "gbk")
    b_cs, _ = run_lines(CS, "roots-list", ["--db", b_db], "gbk")
    # 首行是「库: <db 路径>」——两份库文件名本就不同，不比
    same = a_cs[1:] == b_cs[1:]
    if not same:
        ok = False
        print("两份库的 C# 读面不同：C# 写 vs Go 写")
        for i in range(max(len(a_cs), len(b_cs))):
            la = a_cs[i] if i < len(a_cs) else "<缺失>"
            lb = b_cs[i] if i < len(b_cs) else "<缺失>"
            if la != lb:
                print("    [%d] C#写 %r / Go写 %r" % (i, la, lb))
    else:
        print("C# 写与 Go 写的结果等价：%d 行全同（首行库路径除外）" % len(a_cs))
    for title, db in (("scan-plan · Go 读 C# 的 JSON", a_db), ("scan-plan · C# 读 Go 的 JSON", b_db)):
        lines_cs, rc_cs = run_lines(CS, "scan-plan", ["--db", db], "gbk")
        lines_go, rc_go = run_lines(GO, "scan-plan", ["--db", db], "utf-8")
        diff = [i for i in range(max(len(lines_cs), len(lines_go)))
                if (lines_cs[i] if i < len(lines_cs) else "<缺失>") != (lines_go[i] if i < len(lines_go) else "<缺失>")]
        print("%s：exit %d/%d · 行 %d/%d · 差异 %d → %s"
              % (title, rc_cs, rc_go, len(lines_cs), len(lines_go), len(diff),
                 "一致" if not diff else "不一致"))
        if diff:
            ok = False
            for i in diff[:6]:
                print("    [%d] C# %r" % (i, lines_cs[i] if i < len(lines_cs) else "<缺失>"))
                print("         Go %r" % (lines_go[i] if i < len(lines_go) else "<缺失>"))
    shutil.rmtree(tmp, ignore_errors=True)
    print("判定：%s" % ("一致" if ok else "不一致"))
    return 0 if ok else 1


def lib_db_file_name(lib):
    return "lib_%d_%d.db" % (lib // 1000, lib % 1000)


def lib_of_shard(base_lib, shard):
    return base_lib * 1000 + shard


def ro_connect(path):
    return sqlite3.connect("file:" + path.replace("\\", "/") + "?mode=ro", uri=True)


def expected_modfiles(db_path, guid):
    """按 C# 规则（libdb: / libshards: 设置键 + roots JSON，StoreHub.AllStores）直读库算副本清单——真相源侧实现。"""
    data_dir = os.path.dirname(db_path)
    con = ro_connect(db_path)
    settings = {}
    for key, value in con.execute("select key, value from setting"):
        settings[key.lower()] = value or ""
    con.close()
    roots = json.loads(settings.get("roots") or "{}")

    def lib_of(path):
        return int(settings.get("libdb:" + (path or "").strip().lower(), "0") or 0)

    def shard_count(lib):
        return int(settings.get("libshards:%d" % lib, "0") or 0)

    files = [db_path]
    for e in roots.get("modRoots") or []:
        if e.get("locked") or e.get("tier") == 2:
            continue
        lib = lib_of(e.get("path"))
        if lib <= 0:
            continue
        p = os.path.join(data_dir, lib_db_file_name(lib))
        if os.path.exists(p):
            files.append(p)
    for e in roots.get("cardRoots") or []:
        lib = lib_of(e.get("path"))
        if lib <= 0:
            continue
        n = shard_count(lib) or 1
        for k in range(n):
            p = os.path.join(data_dir, lib_db_file_name(lib_of_shard(lib, k)))
            if os.path.exists(p):
                files.append(p)
    rows = []
    for p in files:
        try:
            c = ro_connect(p)
            rows.extend(c.execute(
                "select guid, tier, root_path, file_path, file_name from mod_file where guid=? order by tier",
                (guid,)).fetchall())
            c.close()
        except sqlite3.Error:
            continue
    rows.sort(key=lambda r: r[1])
    return rows


def cmd_libmap():
    """分片库读面对拍：C# 侧无命令面——Python 按 C# 规则直读库算副本清单，与 Go modfiles 输出逐行比。"""
    db = os.path.join(os.path.dirname(os.path.dirname(GO)), "dist", "data", "kkmanager.db")
    if not os.path.exists(db):
        print("真库不存在：%s" % db)
        return 3
    con = ro_connect(db)
    core_guids = [r[0] for r in con.execute("select distinct guid from mod_file order by guid limit 2")]
    all_core = set(r[0] for r in con.execute("select distinct guid from mod_file"))
    con.close()
    kkc = os.path.join(os.path.dirname(db), "lib_5_0.db")
    only_kkc = []
    if os.path.exists(kkc):
        c = ro_connect(kkc)
        only_kkc = [r[0] for r in c.execute("select distinct guid from mod_file order by guid")
                    if r[0] not in all_core][:3]
        c.close()
    samples = list(dict.fromkeys(core_guids + only_kkc))
    ok = True
    print("样本 %d 个（真库直读）：" % len(samples))
    for guid in samples:
        exp = expected_modfiles(db, guid)
        p = subprocess.run([GO, "modfiles", guid, "--db", db], capture_output=True)
        got = []
        for line in p.stdout.decode("utf-8", "replace").splitlines():
            parts = line.split("\t")
            if len(parts) != 4 or not parts[0].isdigit():
                continue
            got.append((int(parts[0]), parts[1], parts[2], parts[3]))
        exp_n = [(r[1], r[2] or "", r[3], r[4] or "") for r in exp]
        same = exp_n == got
        print("  %-28s 期望 %d 份 / Go %d 份 → %s" % (guid, len(exp_n), len(got), "一致" if same else "不一致"))
        if not same:
            ok = False
            for i in range(max(len(exp_n), len(got))):
                a = exp_n[i] if i < len(exp_n) else "<缺失>"
                b = got[i] if i < len(got) else "<缺失>"
                if a != b:
                    print("    [%d] 期望 %r / Go %r" % (i, a, b))
    print("判定：%s" % ("一致" if ok else "不一致"))
    return 0 if ok else 1


def trim_roots_for_comp(db):
    """composition 对拍裁剪：只留走主库的 mod 库根（mods 锁定 + mods_cache 槽位），
    避免 C# EnsureDatabases 在临时目录预建分片库文件；样本 guid 的副本都在主库。"""
    con = sqlite3.connect(db)
    raw = con.execute("select value from setting where key='roots'").fetchone()[0]
    roots = json.loads(raw)
    roots["modRoots"] = [e for e in roots.get("modRoots") or [] if e.get("locked") or e.get("tier") == 2]
    roots["cardRoots"] = []
    roots["pluginRoots"] = []
    con.execute("update setting set value=? where key='roots'", (json.dumps(roots, ensure_ascii=False),))
    con.commit()
    con.close()


def read_composition(db, guid):
    con = ro_connect(db)
    row = con.execute(
        "select guid,file_path,file_name,size,mtime,entry_count,total_size,total_compressed,entries,texts,analyzed_at"
        " from mod_composition where guid=?", (guid,)).fetchone()
    con.close()
    return row


def cmd_composition():
    """composition 对拍：真库复制两份 → 各自 C# / Go 跑一次（读容器 + 建档）→ 输出行与落库字段比对。"""
    src = os.path.join(os.path.dirname(os.path.dirname(GO)), "dist", "data", "kkmanager.db")
    if not os.path.exists(src):
        print("真库不存在：%s" % src)
        return 3
    tmp = os.path.join(OUT_DIR, "_comp_tmp")
    if os.path.isdir(tmp):
        shutil.rmtree(tmp)
    os.makedirs(tmp)
    a = os.path.join(tmp, "a.db")
    b = os.path.join(tmp, "b.db")
    shutil.copy2(src, a)
    shutil.copy2(src, b)
    trim_roots_for_comp(a)
    trim_roots_for_comp(b)
    guid = "Ktan_Fumika_3_dress"
    pa = subprocess.run([CS, "composition", guid, "--db", a], capture_output=True)
    pb = subprocess.run([GO, "composition", guid, "--db", b], capture_output=True)

    def norm(raw, enc):
        out = []
        for ln in raw.decode(enc, "replace").splitlines():
            ln = re.sub(r"(建档于\s*:\s*)\S+", r"\1<TS>", ln)
            out.append("".join(ch if ord(ch) < 128 else "?" for ch in ln))
        return out

    la = norm(pa.stdout, "gbk")
    lb = norm(pb.stdout, "utf-8")
    ok = pa.returncode == pb.returncode and len(la) == len(lb)
    diff = [i for i in range(max(len(la), len(lb)))
            if (la[i] if i < len(la) else "<缺失>") != (lb[i] if i < len(lb) else "<缺失>")]
    print("输出：exit %d/%d · 行 %d/%d · 差异 %d → %s"
          % (pa.returncode, pb.returncode, len(la), len(lb), len(diff), "一致" if not diff else "不一致"))
    if diff:
        ok = False
        for i in diff[:8]:
            print("    [%d] C# %r" % (i, la[i] if i < len(la) else "<缺失>"))
            print("         Go %r" % (lb[i] if i < len(lb) else "<缺失>"))
    row_a = read_composition(a, guid)
    row_b = read_composition(b, guid)
    if row_a is None or row_b is None:
        print("落库：C# %s / Go %s → 不一致" % ("有" if row_a else "无", "有" if row_b else "无"))
        ok = False
    else:
        names = ["guid", "file_path", "file_name", "size", "mtime", "entry_count", "total_size",
                 "total_compressed", "entries", "texts"]
        bad = []
        for i, name in enumerate(names):
            if row_a[i] != row_b[i]:
                bad.append("%s 期望 %r vs Go %r" % (name, row_a[i] if name != "entries" and name != "texts"
                                                   else str(row_a[i])[:120], row_b[i] if name != "entries" and name != "texts"
                                                   else str(row_b[i])[:120]))
        if bad:
            ok = False
            print("落库字段差异 %d 处：" % len(bad))
            for d in bad[:8]:
                print("    " + d)
        else:
            print("落库字段：10 项全同（analyzed_at 按设计不同，不比）")
    shutil.rmtree(tmp, ignore_errors=True)
    print("判定：%s" % ("一致" if ok else "不一致"))
    return 0 if ok else 1


def _snapshot_db(src, dst, plugin_root):
    """把真库做成一致快照（sqlite backup API——含未 checkpoint 的 WAL）并把 pluginRoots 指向临时目录。"""
    s = sqlite3.connect(src)
    d = sqlite3.connect(dst)
    s.backup(d)
    s.close()
    row = d.execute("SELECT value FROM setting WHERE key='roots'").fetchone()
    cfg = json.loads(row[0]) if row and row[0] else {}
    cfg["pluginRoots"] = [{"tier": 1, "path": plugin_root, "recurse": True,
                           "readOnly": False, "locked": False, "offline": False,
                           "note": "", "replacedFrom": ""}]
    d.execute("UPDATE setting SET value=? WHERE key='roots'",
              (json.dumps(cfg, ensure_ascii=False),))
    d.commit()
    d.close()


def cmd_pluginlog():
    """plugin-log 对拍：真日志被运行中的游戏独占（C# 侧 FileShare.Read 打不开），
    故走「临时库 + 日志副本」——真库快照各复制一份（C# / Go 各一）→ 两份库的 pluginRoots
    都指向同一个临时 BepInEx 目录 → 两侧同日志跑 plugin-log → 输出逐行比。
    第二例：临时目录不放日志 → 两侧都应「解析失败」+ exit 1（缺失分支）。"""
    print("—— plugin-log 专项（临时库 + 日志副本）")
    log_src = r"D:\SteamLibrary\Koikatu\BepInEx\LogOutput.log"
    if not os.path.exists(log_src):
        print("真日志不存在：%s" % log_src)
        return 3
    tmp = os.path.join(OUT_DIR, "_log_tmp")
    if os.path.isdir(tmp):
        shutil.rmtree(tmp)
    os.makedirs(tmp)
    root = os.path.join(tmp, "BepInEx")
    os.makedirs(root, exist_ok=True)
    go_ok = True
    for label, with_log in (("有日志", True), ("日志缺失", False)):
        db_cs = os.path.join(tmp, "cs-%s.db" % ("on" if with_log else "off"))
        db_go = os.path.join(tmp, "go-%s.db" % ("on" if with_log else "off"))
        _snapshot_db(REAL_DB, db_cs, root)
        _snapshot_db(REAL_DB, db_go, root)
        log_path = os.path.join(root, "LogOutput.log")
        if with_log:
            shutil.copy2(log_src, log_path)
        elif os.path.exists(log_path):
            os.remove(log_path)
        p_go = subprocess.run([GO, "plugin-log", "--db", db_go], capture_output=True)
        p_cs = subprocess.run([CS, "plugin-log", "--db", db_cs], capture_output=True)
        a = projection(p_go.stdout, "utf-8")
        b = projection(p_cs.stdout, "gbk")
        diff = 0
        for i in range(max(len(a), len(b))):
            la = a[i] if i < len(a) else "<缺失>"
            lb = b[i] if i < len(b) else "<缺失>"
            if la != lb:
                diff += 1
                if diff <= 6:
                    print("  [%d] Go %r" % (i, la))
                    print("      C# %r" % (lb,))
        one = diff == 0 and len(a) == len(b) and p_go.returncode == p_cs.returncode
        if not one:
            go_ok = False
        print("%-8s exit %d/%d 行 %d/%d 差异 %d → %s"
              % (label, p_go.returncode, p_cs.returncode, len(a), len(b), diff,
                 "一致" if one else "不一致"))
    shutil.rmtree(tmp, ignore_errors=True)
    print("判定：%s" % ("一致" if go_ok else "不一致"))
    return 0 if go_ok else 1


def cmd_stats():
    """stats 对拍（两段）：
    ① 裁剪临时库——roots 只留预置条目（mods / mods_cache / chara\\female）+ 复制 7000 系列卡片分片，
       两侧 AllStores 组成一致 → 逐行必须全同（四色聚合 / 缺失排行 / 未被引用都真实跑）。
    ② 真库全量——KMR_A.exe 是 0.21.6 产物，mod 侧库路由在 0.21.8 修过（0.21.6 的 AllStores
       不含 mod 侧非预置库：kkc 的 lib_5_0.db 423 行不算；0.21.9 源码算）→「mod 文件副本」一行
       是已知代际差异，其余行必须全同。差异行逐条打印两侧原文备查。"""
    print("—— stats 专项（裁剪临时库逐行 + 真库已知代际差异）")
    src_dir = os.path.dirname(REAL_DB)
    tmp = os.path.join(OUT_DIR, "_stats_tmp")
    if os.path.isdir(tmp):
        shutil.rmtree(tmp)
    os.makedirs(tmp)
    ok = True
    # ① 裁剪临时库
    for tag in ("cs", "go"):
        d = os.path.join(tmp, tag)
        os.makedirs(d)
        src = sqlite3.connect(REAL_DB)
        dst = sqlite3.connect(os.path.join(d, "kkmanager.db"))
        src.backup(dst)
        src.close()
        row = dst.execute("SELECT value FROM setting WHERE key='roots'").fetchone()
        cfg = json.loads(row[0]) if row and row[0] else {}
        cfg["modRoots"] = [e for e in (cfg.get("modRoots") or [])
                           if e.get("locked") or e.get("tier") == 2]
        cfg["cardRoots"] = [e for e in (cfg.get("cardRoots") or [])
                            if (e.get("path") or "").lower().endswith("chara\\female")]
        dst.execute("UPDATE setting SET value=? WHERE key='roots'",
                    (json.dumps(cfg, ensure_ascii=False),))
        dst.commit()
        dst.close()
        copied = 0
        for name in os.listdir(src_dir):
            if name.startswith("lib_7000_") and name.endswith(".db"):
                shutil.copy2(os.path.join(src_dir, name), os.path.join(d, name))
                copied += 1
        print("  %s 复制分片 %d 个" % (tag, copied))
    p_go = subprocess.run([GO, "stats", "--db", os.path.join(tmp, "go", "kkmanager.db")], capture_output=True)
    p_cs = subprocess.run([CS, "stats", "--db", os.path.join(tmp, "cs", "kkmanager.db")], capture_output=True)

    def norm_db(raw, enc):
        out = []
        for ln in raw.decode(enc, "replace").splitlines():
            if ln.startswith("库:"):
                ln = "库: <DB>"
            out.append("".join(ch if ord(ch) < 128 else "?" for ch in ln))
        return out

    a = norm_db(p_go.stdout, "utf-8")
    b = norm_db(p_cs.stdout, "gbk")
    diff = 0
    for i in range(max(len(a), len(b))):
        la = a[i] if i < len(a) else "<缺失>"
        lb = b[i] if i < len(b) else "<缺失>"
        if la != lb:
            diff += 1
            if diff <= 8:
                print("  [%d] Go %r" % (i, la))
                print("      C# %r" % (lb,))
    one = diff == 0 and len(a) == len(b) and p_go.returncode == p_cs.returncode
    if not one:
        ok = False
    print("裁剪库   exit %d/%d 行 %d/%d 差异 %d → %s"
          % (p_go.returncode, p_cs.returncode, len(a), len(b), diff, "一致" if one else "不一致"))
    # ② 真库全量（已知代际差异一行）
    p_go = subprocess.run([GO, "stats", "--db", REAL_DB], capture_output=True)
    p_cs = subprocess.run([CS, "stats", "--db", REAL_DB], capture_output=True)
    raw_go = p_go.stdout.decode("utf-8", "replace").splitlines()
    raw_cs = p_cs.stdout.decode("gbk", "replace").splitlines()
    a = projection(p_go.stdout, "utf-8")
    b = projection(p_cs.stdout, "gbk")
    allowed = set(i for i, ln in enumerate(raw_cs) if ln.startswith("mod 文件副本"))
    unknown = 0
    known = 0
    for i in range(max(len(a), len(b))):
        la = a[i] if i < len(a) else "<缺失>"
        lb = b[i] if i < len(b) else "<缺失>"
        if la == lb:
            continue
        if i in allowed:
            known += 1
            print("  [已知代际差异] C# %r" % (raw_cs[i] if i < len(raw_cs) else "<缺失>",))
            print("                 Go %r" % (raw_go[i] if i < len(raw_go) else "<缺失>",))
            continue
        unknown += 1
        if unknown <= 8:
            print("  [%d] Go %r" % (i, la))
            print("      C# %r" % (lb,))
    two = unknown == 0 and len(a) == len(b) and p_go.returncode == p_cs.returncode
    if not two:
        ok = False
    print("真库     exit %d/%d 行 %d/%d 已知差异 %d 行 · 其他差异 %d → %s"
          % (p_go.returncode, p_cs.returncode, len(a), len(b), known, unknown,
             "一致" if two else "不一致"))
    shutil.rmtree(tmp, ignore_errors=True)
    print("判定：%s" % ("一致" if ok else "不一致"))
    return 0 if ok else 1


def cmd_cardanalysis(sample):
    """分项分析对拍：C# 侧只能靠落库产物（无命令行面），故先跑一次单卡扫描再比 JSON。"""
    tmp = os.path.join(OUT_DIR, "_ca_tmp")
    if os.path.isdir(tmp):
        shutil.rmtree(tmp)
    os.makedirs(tmp)
    card = os.path.join(tmp, "card.png")
    shutil.copy2(sample, card)
    db = os.path.join(tmp, "t.db")
    steps = [
        ("roots-add", ["cards", "1", tmp, "仅本目录", "--db", db]),
        ("scan-plan", ["set", ORDER_ALL, ORDER_ANALYZE, "--db", db]),
        ("scan-cards", ["--db", db]),
    ]
    for cmd, args in steps:
        p = subprocess.run([CS, cmd] + args, capture_output=True)
        if p.returncode != 0:
            print("C# 侧准备失败：%s exit %d" % (cmd, p.returncode))
            print(p.stderr.decode("gbk", "replace")[:800])
            return 3
    cs = read_analysis_rows(tmp)

    p_go = subprocess.run([GO, "card-analysis", card], capture_output=True)
    go = {}
    for line in p_go.stdout.decode("utf-8", "replace").splitlines():
        if "\t" not in line:
            continue
        kind, doc = line.split("\t", 1)
        go[kind] = doc

    ok = True
    print("样本 %s" % sample)
    print("C# 落库分项 %s · Go 产出分项 %s" % (sorted(cs), sorted(go)))
    if sorted(cs) != sorted(go):
        ok = False
        print("  分项集合不一致")
    for kind in sorted(set(cs) & set(go)):
        a, b = go[kind], cs[kind]
        try:
            ja, jb = json.loads(a), json.loads(b)
        except ValueError as e:
            ok = False
            print("  %-8s JSON 解析失败：%s" % (kind, e))
            continue
        diffs = diff_json(ja, jb)
        if diffs:
            ok = False
            print("  %-8s 字段差异 %d 处（列前 6 条）：" % (kind, len(diffs)))
            for d in diffs[:6]:
                print("    " + d)
        else:
            same = a.strip() == b.strip()
            print("  %-8s 字段级一致（%d 字符；字节%s）"
                  % (kind, len(b), "相同" if same else "不同——仅转义 / 空白"))
    if p_go.returncode != 0:
        ok = False
        print("  Go 退出码 %d：%s" % (p_go.returncode, p_go.stderr.decode("utf-8", "replace")[:400]))
    shutil.rmtree(tmp, ignore_errors=True)
    print("判定：%s" % ("一致" if ok else "不一致"))
    return 0 if ok else 1


def _project_text(text):
    """把已解码文本按 ASCII 投影（非 ASCII 折 ?）——与 projection 同一口径，供文本级归一后使用。"""
    return ["".join(ch if ord(ch) < 128 else "?" for ch in ln) for ln in text.splitlines()]


def _norm_db_line(raw, enc):
    """两侧输出归一：库路径行折成 <DB>，非 ASCII 折成 ?（编码伪差异剔除）。"""
    out = []
    for ln in raw.decode(enc, "replace").splitlines():
        if ln.startswith("库:"):
            ln = "库: <DB>"
        out.append("".join(ch if ord(ch) < 128 else "?" for ch in ln))
    return out


def _both(cmd, args, cwd=None):
    """两侧跑同命令同参数，返回 (go 行, go 退出码, cs 行, cs 退出码)。"""
    env = dict(os.environ)
    p_go = subprocess.run([GO, cmd] + args, capture_output=True, cwd=cwd, env=env)
    p_cs = subprocess.run([CS, cmd] + args, capture_output=True, cwd=cwd, env=env)
    return (projection(p_go.stdout, "utf-8"), p_go.returncode,
            projection(p_cs.stdout, "gbk"), p_cs.returncode)


def _report(label, a, rgo, b, rcs):
    """比两组投影行 + 退出码；打印结论并返回是否一致（不一致列出前 6 处）。"""
    diff = [i for i in range(max(len(a), len(b)))
            if (a[i] if i < len(a) else "<缺失>") != (b[i] if i < len(b) else "<缺失>")]
    ok = not diff and len(a) == len(b) and rgo == rcs
    print("  %-40s exit %d/%d 行 %d/%d 差异 %d → %s"
          % (label, rgo, rcs, len(a), len(b), len(diff), "一致" if ok else "不一致"))
    for i in diff[:6]:
        print("      [%d] Go %r" % (i, a[i] if i < len(a) else "<缺失>"))
        print("          C# %r" % (b[i] if i < len(b) else "<缺失>"))
    return ok


# 读面四族的参数面（真库只读）：排序键全走一遍 + 关键词过滤 + 含已搬走清单。
QUERY_CASES = [
    ("authors", []),
    ("cards", ["--limit", "5"]),
    ("cards", ["--order", "chara", "--limit", "8"]),
    ("cards", ["--order", "miss", "--limit", "8"]),
    ("cards", ["--order", "size", "--desc", "--limit", "8"]),
    ("cards", ["--order", "file", "--limit", "8"]),
    ("cards", ["--order", "mtime", "--q", "1", "--limit", "8"]),
    ("todos", []),
    ("noncards", []),
    ("noncards", ["--moved"]),
]


def _norm_noncards_order(lines):
    """非卡清单的并列行归一：`  #<id> ...` 行按 id 升序重排。

    理由：C# 侧 SQL 是 `ORDER BY size DESC`（无兜底键），同体积行的相对次序由 SQLite
    引擎排序实现决定——两套 provider（C# SQLitePCLRaw / Go modernc）对同值行不保证同序。
    语义判据是「按体积降序」，并列次序无定义，故归一后再逐字比（行内容本身仍逐字比）。"""
    head = [ln for ln in lines if not ln.startswith("  #")]
    body = [ln for ln in lines if ln.startswith("  #")]
    body.sort(key=lambda s: int(re.match(r"  #(\d+)", s).group(1)))
    return head + body


def cmd_query():
    """读面四族对拍（真库只读）：authors / cards（全排序键 + 关键词）/ todos / noncards。
    判据：输出逐行 ASCII 投影一致 + 退出码相等（非卡清单的并列行按 id 归一，见 _norm_noncards_order）。"""
    print("—— 读面四族对拍（真库只读：authors / cards / todos / noncards）")
    ok = True
    for cmd, args in QUERY_CASES:
        a, rgo, b, rcs = _both(cmd, args + ["--db", REAL_DB])
        if cmd == "noncards":
            a, b = _norm_noncards_order(a), _norm_noncards_order(b)
        if not _report(cmd + " " + " ".join(args), a, rgo, b, rcs):
            ok = False
    print("判定：%s" % ("一致" if ok else "不一致"))
    return 0 if ok else 1


def _read_table_rows(dbdir, sql):
    """读主库 + 全部库文件的某张表（跨文件合并排序；忽略 id 与时间戳由调用方归一）。"""
    rows = []
    targets = [os.path.join(dbdir, "kkmanager.db")] + sorted(glob.glob(os.path.join(dbdir, "lib_*.db")))
    for p in targets:
        try:
            c = ro_connect(p)
            rows.extend(c.execute(sql).fetchall())
            c.close()
        except sqlite3.Error:
            continue
    return rows


def cmd_dup_new():
    """dup 对拍：临时库 + scan 三根样本（main + cache 同 guid 跨根副本）。
    两轮：首跑（算 MD5 落档案）/ 二跑（命中档案，新增 0）。
    判据：输出逐行（库路径归一）+ mod_hash 表行（路径折相对、MD5 逐字）+ 退出码。"""
    print("—— dup 专项（临时库 + 同 guid 跨根副本；首跑算 MD5 / 二跑命中档案）")
    tmp = os.path.join(OUT_DIR, "_dup_tmp")
    if os.path.isdir(tmp):
        shutil.rmtree(tmp)
    os.makedirs(tmp)
    sample = _prepare_scan_sample(tmp)
    if not sample:
        return 3
    dirs = {}
    for tag, exe in (("cs", CS), ("go", GO)):
        d = os.path.join(tmp, tag)
        os.makedirs(d)
        db = os.path.join(d, "kkmanager.db")
        p = subprocess.run([exe, "roots-list", "--db", db], capture_output=True)
        if p.returncode != 0 or not os.path.exists(db):
            print("  %s 建库失败：exit %d" % (tag, p.returncode))
            return 3
        _trim_roots_for_scan(db, sample)
        dirs[tag] = db
    ok = True
    for label in ("首跑", "二跑"):
        outs = {}
        for tag, exe, enc in (("cs", CS, "gbk"), ("go", GO, "utf-8")):
            if label == "首跑":
                subprocess.run([exe, "scan-mods", "--db", dirs[tag]], capture_output=True)
            p = subprocess.run([exe, "dup", "--db", dirs[tag]], capture_output=True)
            outs[tag] = (p.returncode, _norm_db_line(p.stdout, enc))
        if not _report(label, outs["go"][1], outs["go"][0], outs["cs"][1], outs["cs"][0]):
            ok = False
        rows = {}
        for tag in ("cs", "go"):
            got = _read_table_rows(os.path.join(tmp, tag),
                                   "select file_path, md5 from mod_hash order by file_path")
            rows[tag] = [(r[0].replace(os.path.join(tmp, tag) + os.sep, ""), r[1]) for r in got]
        same = rows["cs"] == rows["go"]
        print("  %-40s mod_hash 行 %d/%d → %s"
              % (label + " 档案", len(rows["cs"]), len(rows["go"]), "一致" if same else "不一致"))
        if not same:
            ok = False
            for i in range(max(len(rows["cs"]), len(rows["go"]))):
                va = rows["cs"][i] if i < len(rows["cs"]) else "<缺失>"
                vb = rows["go"][i] if i < len(rows["go"]) else "<缺失>"
                if va != vb:
                    print("      C# %r" % (va,))
                    print("      Go %r" % (vb,))
                    break
    shutil.rmtree(tmp, ignore_errors=True)
    print("判定：%s" % ("一致" if ok else "不一致"))
    return 0 if ok else 1


def cmd_refs():
    """refs-read 对拍：临时库 + 卡片样本（扫描时关掉 refs 步 → 引用缺失）→ 两侧各补读。
    三轮：① 全量补读 ② 再跑全量（幂等）③ --only-pending（无待补）。
    判据：输出逐行（库路径归一）+ card_mod 表行（card_id / mod_guid）+ 退出码。"""
    print("—— refs-read 专项（临时库 + 卡片样本；补读 / 幂等 / --only-pending）")
    tmp = os.path.join(OUT_DIR, "_refs_tmp")
    if os.path.isdir(tmp):
        shutil.rmtree(tmp)
    os.makedirs(tmp)
    sample = _prepare_card_sample(tmp)
    if not sample:
        return 3
    dirs = {}
    for tag, exe in (("cs", CS), ("go", GO)):
        d = os.path.join(tmp, tag)
        os.makedirs(d)
        db = os.path.join(d, "kkmanager.db")
        p = subprocess.run([exe, "roots-list", "--db", db], capture_output=True)
        if p.returncode != 0 or not os.path.exists(db):
            print("  %s 建库失败：exit %d" % (tag, p.returncode))
            return 3
        _trim_roots_for_cards(db, sample["cards"], 4)
        subprocess.run([exe, "scan-plan", "set", "row,head,name,timeline", "", "--db", db],
                       capture_output=True)
        p = subprocess.run([exe, "scan-cards", "--db", db], capture_output=True)
        if p.returncode != 0:
            print("  %s 扫描准备失败：exit %d" % (tag, p.returncode))
            return 3
        dirs[tag] = db
    ok = True
    rounds = ("补读", "幂等复跑", "--only-pending")
    for label in rounds:
        args = ["--root", sample["cards"], "--db"]
        outs = {}
        for tag, exe, enc in (("cs", CS, "gbk"), ("go", GO, "utf-8")):
            extra = ["--only-pending"] if label == "--only-pending" else []
            p = subprocess.run([exe, "refs-read"] + args + [dirs[tag]] + extra, capture_output=True)
            outs[tag] = (p.returncode, _norm_db_line(p.stdout, enc))
        if not _report(label, outs["go"][1], outs["go"][0], outs["cs"][1], outs["cs"][0]):
            ok = False
        rows = {}
        for tag in ("cs", "go"):
            rows[tag] = _read_table_rows(os.path.join(tmp, tag),
                                         "select card_id, mod_guid from card_mod order by card_id, mod_guid")
        same = rows["cs"] == rows["go"]
        print("  %-40s card_mod 行 %d/%d → %s"
              % (label + " 表", len(rows["cs"]), len(rows["go"]), "一致" if same else "不一致"))
        if not same:
            ok = False
            for i in range(max(len(rows["cs"]), len(rows["go"]))):
                va = rows["cs"][i] if i < len(rows["cs"]) else "<缺失>"
                vb = rows["go"][i] if i < len(rows["go"]) else "<缺失>"
                if va != vb:
                    print("      C# %r" % (va,))
                    print("      Go %r" % (vb,))
                    break
    shutil.rmtree(tmp, ignore_errors=True)
    print("判定：%s" % ("一致" if ok else "不一致"))
    return 0 if ok else 1


def _table_rows_equal(dir_cs, dir_go, sql, label, ok):
    """比两侧主库 + 全部库文件的某条查询结果（跨文件合并）——返回 (ok, 两侧行数)。"""
    rows = {}
    for tag, d in (("cs", dir_cs), ("go", dir_go)):
        rows[tag] = _read_table_rows(d, sql)
    same = rows["cs"] == rows["go"]
    print("  %-40s 行 %d/%d → %s" % (label, len(rows["cs"]), len(rows["go"]), "一致" if same else "不一致"))
    if not same:
        ok = False
        for i in range(max(len(rows["cs"]), len(rows["go"]))):
            va = rows["cs"][i] if i < len(rows["cs"]) else "<缺失>"
            vb = rows["go"][i] if i < len(rows["go"]) else "<缺失>"
            if va != vb:
                print("      C# %r" % (va,))
                print("      Go %r" % (vb,))
                break
    return ok


IDX_CASES = [
    ["--limit", "10"],
    ["--kind", "卡", "--limit", "10"],
    ["--q", "1", "--limit", "10"],
    ["--wide", "--limit", "5"],
    ["--limit", "10", "--root", "KKCROOT"],
]


def cmd_index():
    """index-import / idx 对拍：两侧临时库（模板建库 + roots-add kkc 库根）→ 各跑一次导入。
    判据：导入输出逐行（目录折 <IDX>）+ card / card_mod / index_source 三表行 + idx 五组查询逐行。"""
    print("—— index-import / idx 专项（两侧临时库 + 真 kkc 索引只读导入）")
    kkc_root = os.path.join(os.path.dirname(REAL_DB), "kkc")
    index_dir = os.path.join(kkc_root, "整理", "index")
    if not os.path.isdir(index_dir):
        print("  kkc 索引目录不存在：%s" % index_dir)
        return 3
    tmp = os.path.join(OUT_DIR, "_idx_tmp")
    if os.path.isdir(tmp):
        shutil.rmtree(tmp)
    os.makedirs(tmp)
    dirs = {}
    for tag, exe in (("cs", CS), ("go", GO)):
        d = os.path.join(tmp, tag)
        os.makedirs(d)
        db = os.path.join(d, "kkmanager.db")
        p = subprocess.run([exe, "roots-list", "--db", db], capture_output=True)
        if p.returncode != 0 or not os.path.exists(db):
            print("  %s 建库失败：exit %d" % (tag, p.returncode))
            return 3
        p = subprocess.run([exe, "roots-add", "cards", "2", kkc_root, "含子目录", "--db", db],
                           capture_output=True)
        if p.returncode != 0:
            print("  %s roots-add 失败：exit %d" % (tag, p.returncode))
            return 3
        dirs[tag] = db
    ok = True
    # 导入前的库根状态（roots-add 之后）——两侧必须同源：库号分配顺序 / 卡片分片数都从这里派生
    p_go = subprocess.run([GO, "roots-list", "--db", dirs["go"]], capture_output=True)
    p_cs = subprocess.run([CS, "roots-list", "--db", dirs["cs"]], capture_output=True)
    a = [ln.replace(dirs["go"], "<DB>") for ln in projection(p_go.stdout, "utf-8")]
    b = [ln.replace(dirs["cs"], "<DB>") for ln in projection(p_cs.stdout, "gbk")]
    if not _report("roots-list（导入前）", a, p_go.returncode, b, p_cs.returncode):
        ok = False
    outs = {}
    for tag, exe, enc in (("cs", CS, "gbk"), ("go", GO, "utf-8")):
        p = subprocess.run([exe, "index-import", "--root", kkc_root, "--index", index_dir,
                            "--db", dirs[tag]], capture_output=True)
        raw = p.stdout.decode(enc, "replace")
        # 预建库文件的耗时是真实时钟差（每次不同）——归一后再投影（中文归一必须在投影前做）
        raw = re.sub(r"(预建库文件 \d+ 个) · \d+ ms", r"\1 · <MS>", raw)
        raw = raw.replace(index_dir, "<IDX>")
        lines = _project_text(raw)
        outs[tag] = (p.returncode, lines)
        if p.returncode != 0:
            print("  %s stderr：%s" % (tag, p.stderr.decode(enc, "replace")[:300]))
    if not _report("index-import", outs["go"][1], outs["go"][0], outs["cs"][1], outs["cs"][0]):
        ok = False
    ok = _table_rows_equal(os.path.join(tmp, "cs"), os.path.join(tmp, "go"),
                           "select file_path, card_type, chara_name from card order by file_path",
                           "card 表", ok)
    ok = _table_rows_equal(os.path.join(tmp, "cs"), os.path.join(tmp, "go"),
                           "select card_id, mod_guid from card_mod order by card_id, mod_guid",
                           "card_mod 表", ok)
    ok = _table_rows_equal(os.path.join(tmp, "cs"), os.path.join(tmp, "go"),
                           "select root_path, index_dir, format, rows, cards, mods, others, digest from index_source",
                           "index_source 表", ok)
    for args in IDX_CASES:
        args = [kkc_root if a == "KKCROOT" else a for a in args]
        p_go = subprocess.run([GO, "idx"] + args + ["--db", dirs["go"]], capture_output=True)
        p_cs = subprocess.run([CS, "idx"] + args + ["--db", dirs["cs"]], capture_output=True)
        a = [ln.replace(dirs["go"], "<DB>") for ln in projection(p_go.stdout, "utf-8")]
        b = [ln.replace(dirs["cs"], "<DB>") for ln in projection(p_cs.stdout, "gbk")]
        if not _report("idx " + " ".join(args), a, p_go.returncode, b, p_cs.returncode):
            ok = False
    shutil.rmtree(tmp, ignore_errors=True)
    print("判定：%s" % ("一致" if ok else "不一致"))
    return 0 if ok else 1


def _make_sort_sample(tmp):
    """造一份「按作者整理」样本：main 根（a + b + sub/a，sub/a 与 a 同 guid 同文件名 → 目标撞车）+ cache 根（a 的跨根副本）。"""
    dirs = {"main": os.path.join(tmp, "mods_main"), "cache": os.path.join(tmp, "mods_cache")}
    for d in dirs.values():
        os.makedirs(d)
    cands = []
    for name in os.listdir(MOD_LIB_SRC):
        p = os.path.join(MOD_LIB_SRC, name)
        if os.path.isfile(p) and os.path.getsize(p) < 4 * 1024 * 1024:
            cands.append((os.path.getsize(p), name))
    cands.sort()
    if len(cands) < 2:
        return None
    a, b = cands[0][1], cands[1][1]
    sub = os.path.join(dirs["main"], "sub")
    os.makedirs(sub)
    shutil.copy2(os.path.join(MOD_LIB_SRC, a), os.path.join(dirs["main"], a))
    shutil.copy2(os.path.join(MOD_LIB_SRC, b), os.path.join(dirs["main"], b))
    shutil.copy2(os.path.join(MOD_LIB_SRC, a), os.path.join(sub, a))
    shutil.copy2(os.path.join(MOD_LIB_SRC, a), os.path.join(dirs["cache"], a))
    return {"main": dirs["main"], "cache": dirs["cache"], "dupName": a}


def _trim_roots_sort(db, main, cache):
    """sort 样本的库根裁剪：main 锁定 tier1 + cache tier2（不预建卡片分片、不放别的根）。"""
    cfg = {
        "gameRoot": "",
        "modRoots": [
            {"tier": 1, "path": main, "recurse": True, "readOnly": False, "locked": True,
             "offline": False, "note": "", "replacedFrom": ""},
            {"tier": 2, "path": cache, "recurse": True, "readOnly": False, "locked": False,
             "offline": False, "note": "", "replacedFrom": ""},
        ],
        "cardRoots": [],
        "pluginRoots": [],
        "seeded": True,
    }
    con = sqlite3.connect(db)
    con.execute("INSERT INTO setting(key,value) VALUES('roots',?) "
                "ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                (json.dumps(cfg, ensure_ascii=False),))
    con.commit()
    con.close()


RE_TS = re.compile(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}")


def _norm_sort_lines(raw, enc, sample):
    """sort 输出归一：样本根折 <MAIN>/<CACHE>、时间戳折 <TS>、非 ASCII 折 ?。"""
    lines = []
    for ln in raw.decode(enc, "replace").splitlines():
        ln = ln.replace(sample["main"], "<MAIN>").replace(sample["cache"], "<CACHE>")
        ln = RE_TS.sub("<TS>", ln)
        lines.append("".join(ch if ord(ch) < 128 else "?" for ch in ln))
    return lines


def _tree_snapshot(base):
    """目录树快照（相对路径 + 字节数，排序）——搬运类命令的终态判据。
    库文件（*.db / 边车）不比字节：SQLite 页面分配属实现细节，数据面另由表行比较覆盖。"""
    out = []
    for root, _dirs, files in os.walk(base):
        for name in files:
            if name.endswith(".db") or name.endswith(".db-wal") or name.endswith(".db-shm"):
                continue
            p = os.path.join(root, name)
            out.append((os.path.relpath(p, base).replace("\\", "/"), os.path.getsize(p)))
    out.sort()
    return out


def cmd_sort():
    """sort 专项（按作者整理全链）：两侧各造同一形状样本 → plan / show / conflict / keep / show / exec。
    判据：每步输出逐行（样本根与时间戳归一）+ mod_sort_plan_item / mod_old / mod_file 表行 + 终态文件树。"""
    print("—— sort 专项（按作者整理全链：plan / show / conflict / keep / exec）")
    tmp = os.path.join(OUT_DIR, "_sort_tmp")
    if os.path.isdir(tmp):
        shutil.rmtree(tmp)
    os.makedirs(tmp)
    sides = {}
    for tag, exe in (("cs", CS), ("go", GO)):
        d = os.path.join(tmp, tag)
        os.makedirs(d)
        sample = _make_sort_sample(d)
        if sample is None:
            print("  真实 mod 库可用样本不足（<2 个小 zipmod）")
            return 3
        db = os.path.join(d, "kkmanager.db")
        p = subprocess.run([exe, "roots-list", "--db", db], capture_output=True)
        if p.returncode != 0 or not os.path.exists(db):
            print("  %s 建库失败：exit %d" % (tag, p.returncode))
            return 3
        _trim_roots_sort(db, sample["main"], sample["cache"])
        p = subprocess.run([exe, "scan-mods", "--db", db], capture_output=True)
        if p.returncode != 0:
            print("  %s scan-mods 失败：exit %d" % (tag, p.returncode))
            return 3
        sample["db"] = db
        sides[tag] = sample
    ok = True

    def step(label, args, normalize=True):
        nonlocal ok
        outs = {}
        for tag, exe, enc in (("cs", CS, "gbk"), ("go", GO, "utf-8")):
            p = subprocess.run([exe, "sort"] + args + ["--db", sides[tag]["db"]], capture_output=True)
            lines = (_norm_sort_lines(p.stdout, enc, sides[tag]) if normalize
                     else projection(p.stdout, enc))
            outs[tag] = (p.returncode, lines)
            if p.returncode != 0 and p.stderr.strip():
                print("      %s stderr：%s" % (tag, _safe(p.stderr.decode(enc, "replace"))[:300]))
        if not _report(label, outs["go"][1], outs["go"][0], outs["cs"][1], outs["cs"][0]):
            ok = False
        return outs["go"][1]

    step("plan", ["plan"])
    show_lines = step("show（冲突未解）", ["show"])
    seqs = [int(m.group(1)) for ln in show_lines for m in [re.search(r"seq (\d+)", ln)] if m]
    if not seqs:
        print("  样本没有造出冲突——后续 conflict / keep 步跳过（对照面变窄）")
        ok = False
    else:
        seq = seqs[0]
        step("conflict seq %d" % seq, ["conflict", str(seq)])
        step("keep seq %d" % seq, ["keep", str(seq)])
        step("show（冲突已解）", ["show"])
    step("exec --yes", ["exec", "--yes"])
    for table, sql in (
        ("mod_sort_plan_item", "select seq, src_path, dest_path, author, folder, state, note from mod_sort_plan_item order by seq"),
        ("mod_old", "select old_path, old_name, new_name from mod_old order by old_path"),
        ("mod_file", "select file_path, guid, tier, file_name from mod_file order by file_path"),
    ):
        rows = {}
        for tag in ("cs", "go"):
            got = _read_table_rows(os.path.join(tmp, tag), sql)
            rows[tag] = [tuple((str(v).replace(sides[tag]["main"], "<MAIN>").replace(sides[tag]["cache"], "<CACHE>")
                                if v is not None else None) for v in r) for r in got]
        same = rows["cs"] == rows["go"]
        print("  %-40s 行 %d/%d → %s" % (table, len(rows["cs"]), len(rows["go"]), "一致" if same else "不一致"))
        if not same:
            ok = False
            for i in range(max(len(rows["cs"]), len(rows["go"]))):
                va = rows["cs"][i] if i < len(rows["cs"]) else "<缺失>"
                vb = rows["go"][i] if i < len(rows["go"]) else "<缺失>"
                if va != vb:
                    print("      C# %r" % (va,))
                    print("      Go %r" % (vb,))
                    break
    tree = {tag: _tree_snapshot(os.path.join(tmp, tag)) for tag in ("cs", "go")}
    same = tree["cs"] == tree["go"]
    print("  %-40s 文件 %d/%d → %s" % ("终态文件树", len(tree["cs"]), len(tree["go"]), "一致" if same else "不一致"))
    if not same:
        ok = False
        for i in range(max(len(tree["cs"]), len(tree["go"]))):
            va = tree["cs"][i] if i < len(tree["cs"]) else "<缺失>"
            vb = tree["go"][i] if i < len(tree["go"]) else "<缺失>"
            if va != vb:
                print("      C# %r" % (va,))
                print("      Go %r" % (vb,))
                break
    shutil.rmtree(tmp, ignore_errors=True)
    print("判定：%s" % ("一致" if ok else "不一致"))
    return 0 if ok else 1


def _make_cardsort_sample(tmp):
    """card-sort 样本：源目录（人物卡 / 服装卡 / 场景卡各 1 + 1 个 zipmod + 1 个文本）+ 整理根。"""
    src = os.path.join(tmp, "src")
    dest = os.path.join(tmp, "dest")
    os.makedirs(src)
    os.makedirs(dest)
    picked = []
    for sub, n in ((FEMALE_DIR, 1), (COORD_DIR, 1), (SCENE_DIR, 1)):
        for name in _pick_small_cards(sub, n):
            shutil.copy2(os.path.join(sub, name), os.path.join(src, name))
            picked.append(name)
    if not picked:
        return None
    cands = []
    for name in os.listdir(MOD_LIB_SRC):
        p = os.path.join(MOD_LIB_SRC, name)
        if os.path.isfile(p) and os.path.getsize(p) < 4 * 1024 * 1024:
            cands.append((os.path.getsize(p), name))
    cands.sort()
    if not cands:
        return None
    shutil.copy2(os.path.join(MOD_LIB_SRC, cands[0][1]), os.path.join(src, cands[0][1]))
    with open(os.path.join(src, "notes.txt"), "w", encoding="utf-8") as f:
        f.write("not a card or mod\n")
    return {"src": src, "dest": dest, "cards": picked, "mod": cands[0][1]}


def _norm_cs_lines(raw, enc, sample):
    """card-sort 输出归一：源 / 整理根 / 计划文件路径折 <SRC>/<DEST>/<PLAN>，时间戳折 <TS>。"""
    lines = []
    for ln in raw.decode(enc, "replace").splitlines():
        ln = ln.replace(sample["src"], "<SRC>").replace(sample["dest"], "<DEST>")
        ln = ln.replace(sample["plan"], "<PLAN>").replace(sample["rules"], "<RULES>")
        ln = ln.replace(sample["db"], "<DB>").replace(sample["refs"], "<REFS>")
        ln = ln.replace(os.path.dirname(sample["db"]), "<DBDIR>")
        ln = re.sub(r"(预建库文件 \d+ 个) · \d+ ms", r"\1 · <MS>", ln)
        ln = RE_TS.sub("<TS>", ln)
        lines.append("".join(ch if ord(ch) < 128 else "?" for ch in ln))
    return lines


def cmd_cardsort():
    """card-sort 专项（整理链核心）：两侧各造同一形状样本 → rules 列表 / 导出 / plan / show / exec /
    moves / rollback / refs / delete / delete-list。
    判据：每步输出逐行（路径与时间戳归一）+ card / card_rule / card_move / card_delete 表行 + 文件树。"""
    print("—— card-sort 专项（整理链核心：rules / plan / show / exec / moves / rollback / refs / delete）")
    tmp = os.path.join(OUT_DIR, "_cardsort_tmp")
    if os.path.isdir(tmp):
        shutil.rmtree(tmp)
    os.makedirs(tmp)
    sides = {}
    for tag, exe in (("cs", CS), ("go", GO)):
        d = os.path.join(tmp, tag)
        os.makedirs(d)
        sample = _make_cardsort_sample(d)
        if sample is None:
            print("  卡片 / mod 样本不足")
            return 3
        sample["plan"] = os.path.join(d, "plan.tsv")
        sample["rules"] = os.path.join(d, "rules.tsv")
        sample["refs"] = os.path.join(d, "refs.tsv")
        db = os.path.join(d, "kkmanager.db")
        p = subprocess.run([exe, "roots-list", "--db", db], capture_output=True)
        if p.returncode != 0 or not os.path.exists(db):
            print("  %s 建库失败：exit %d" % (tag, p.returncode))
            return 3
        sample["db"] = db
        sides[tag] = sample
    ok = True

    def run(tag, args):
        """按侧替换路径占位后跑一条 card-sort 子命令，返回 (退出码, 归一后行, stderr)。"""
        s = sides[tag]
        exe, enc = (CS, "gbk") if tag == "cs" else (GO, "utf-8")
        real = [a.replace("<SRC>", s["src"]).replace("<DEST>", s["dest"])
                 .replace("<PLAN>", s["plan"]).replace("<RULES>", s["rules"])
                 .replace("<REFS>", s["refs"]) for a in args]
        p = subprocess.run([exe, "card-sort"] + real + ["--db", s["db"]], capture_output=True)
        return p.returncode, _norm_cs_lines(p.stdout, enc, s), p.stderr.decode(enc, "replace")

    def step(label, args):
        nonlocal ok
        outs = {}
        for tag in ("cs", "go"):
            rc, lines, err = run(tag, args)
            outs[tag] = (rc, lines)
            if rc != 0 and err.strip():
                print("      %s stderr：%s" % (tag, _safe(err)[:300]))
        if not _report(label, outs["go"][1], outs["go"][0], outs["cs"][1], outs["cs"][0]):
            ok = False
        return outs["go"][1]

    def file_equal(label, key):
        """比两侧各自导出的同名产物（规则 / 引用表 / 计划 TSV）——按文本行比。"""
        nonlocal ok
        texts = {}
        for tag in ("cs", "go"):
            try:
                with open(sides[tag][key], "r", encoding="utf-8-sig") as f:
                    texts[tag] = [ln.replace(sides[tag]["src"], "<SRC>").replace(sides[tag]["dest"], "<DEST>")
                                  for ln in f.read().splitlines()]
            except OSError as exc:
                texts[tag] = ["<读不到> %s" % exc]
        same = texts["cs"] == texts["go"]
        print("  %-40s 行 %d/%d → %s" % (label, len(texts["cs"]), len(texts["go"]), "一致" if same else "不一致"))
        if not same:
            ok = False
            for i in range(max(len(texts["cs"]), len(texts["go"]))):
                va = texts["cs"][i] if i < len(texts["cs"]) else "<缺失>"
                vb = texts["go"][i] if i < len(texts["go"]) else "<缺失>"
                if va != vb:
                    print("      C# %r" % (va,))
                    print("      Go %r" % (vb,))
                    break

    # 规则库先播种（两侧同一份规则文件）：导入 → 列表 → 导出 → 比导出物
    rule_text = "kw\t测试作品\t测试作品\tmanual\t0\t0\n"
    for tag in ("cs", "go"):
        with open(sides[tag]["rules"], "w", encoding="utf-8") as f:
            f.write(rule_text)
    step("rules --import", ["rules", "--import", "<RULES>"])
    step("rules（导入后列表）", ["rules"])
    step("rules --write（导出）", ["rules", "--write", "<RULES>"])
    file_equal("规则 TSV", "rules")
    step("plan", ["plan", "<SRC>", "--dest", "<DEST>", "--out", "<PLAN>"])
    file_equal("计划 TSV", "plan")
    step("show", ["show", "--out", "<PLAN>"])
    step("exec --yes", ["exec", "--out", "<PLAN>", "--yes", "--root", "<DEST>"])
    step("moves", ["moves", "--all"])
    step("refs", ["refs", "--root", "<DEST>", "--out", "<REFS>"])
    file_equal("引用表 TSV", "refs")
    step("rollback --yes", ["rollback", "--out", "<PLAN>", "--yes", "--root", "<DEST>"])
    step("moves（回滚后）", ["moves", "--all"])
    step("delete", ["delete", os.path.join("<DEST>", "05-其它", "notes.txt"), "--root", "<DEST>", "--note", "对拍样本"])
    step("delete-list", ["delete-list"])
    for table, sql in (
        ("card", "select file_path, card_type, chara_name from card order by file_path"),
        ("card_rule", "select kind, match, value, origin, enabled from card_rule order by kind, match"),
        ("card_move", "select from_path, to_path, work, card_type, state, consumed from card_move order by from_path"),
        ("card_delete", "select from_path, to_path, size, state, note from card_delete order by from_path"),
    ):
        rows = {}
        for tag in ("cs", "go"):
            got = _read_table_rows(os.path.join(tmp, tag), sql)
            rows[tag] = [tuple((str(v).replace(sides[tag]["src"], "<SRC>").replace(sides[tag]["dest"], "<DEST>")
                                if v is not None else None) for v in r) for r in got]
        same = rows["cs"] == rows["go"]
        print("  %-40s 行 %d/%d → %s" % (table, len(rows["cs"]), len(rows["go"]), "一致" if same else "不一致"))
        if not same:
            ok = False
            for i in range(max(len(rows["cs"]), len(rows["go"]))):
                va = rows["cs"][i] if i < len(rows["cs"]) else "<缺失>"
                vb = rows["go"][i] if i < len(rows["go"]) else "<缺失>"
                if va != vb:
                    print("      C# %r" % (va,))
                    print("      Go %r" % (vb,))
                    break
    tree = {}
    for tag in ("cs", "go"):
        tree[tag] = _tree_snapshot(sides[tag]["src"]) + [("dest/" + rel, size) for rel, size in _tree_snapshot(sides[tag]["dest"])]
    same = tree["cs"] == tree["go"]
    print("  %-40s 条目 %d/%d → %s" % ("终态文件树（src+dest）", len(tree["cs"]), len(tree["go"]), "一致" if same else "不一致"))
    if not same:
        ok = False
        for i in range(max(len(tree["cs"]), len(tree["go"]))):
            va = tree["cs"][i] if i < len(tree["cs"]) else "<缺失>"
            vb = tree["go"][i] if i < len(tree["go"]) else "<缺失>"
            if va != vb:
                print("      C# %r" % (va,))
                print("      Go %r" % (vb,))
                break
    shutil.rmtree(tmp, ignore_errors=True)
    print("判定：%s" % ("一致" if ok else "不一致"))
    return 0 if ok else 1


def _sha256(path):
    import hashlib
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def _first_diff(a, b):
    """两份文件的首个差异偏移（-1 = 相同或长度不同先按长度判）。"""
    with open(a, "rb") as fa, open(b, "rb") as fb:
        off = 0
        while True:
            ba = fa.read(1 << 16)
            bb = fb.read(1 << 16)
            if ba != bb:
                for i in range(min(len(ba), len(bb))):
                    if ba[i] != bb[i]:
                        return off + i
                return off + min(len(ba), len(bb))
            if not ba:
                return -1
            off += len(ba)


def cmd_cardedit():
    """card-edit 对拍（最小改动写面）：两侧各一份卡片副本 → 同样字段编辑两轮 → 比输出 + 编辑后文件哈希 + 留档。
    判据：输出逐行（路径 / 时间戳归一）+ 编辑后文件 SHA256 相同（同输入同算法应逐字节相同）+ 归档件数一致。"""
    print("—— card-edit 专项（最小改动写面：字段编辑两轮 / 文件字节 / 留档）")
    tmp = os.path.join(OUT_DIR, "_cardedit_tmp")
    if os.path.isdir(tmp):
        shutil.rmtree(tmp)
    os.makedirs(tmp)
    sides = {}
    for tag in ("cs", "go"):
        d = os.path.join(tmp, tag)
        os.makedirs(d)
        card = os.path.join(d, "card.png")
        shutil.copy2(SAMPLE_CARD, card)
        arch = os.path.join(d, "archive")
        os.makedirs(arch)
        sides[tag] = {"dir": d, "card": card, "arch": arch}
    ok = True

    def norm(raw, enc, s):
        lines = []
        for ln in raw.decode(enc, "replace").splitlines():
            ln = ln.replace(s["card"], "<CARD>").replace(s["arch"], "<ARCH>")
            ln = RE_TS.sub("<TS>", ln)
            ln = re.sub(r"\d{8}_\d{6}_\d{3}", "<STAMP>", ln)
            lines.append("".join(ch if ord(ch) < 128 else "?" for ch in ln))
        return lines

    rounds = [
        ["--last", "测试姓", "--first", "测试名", "--nick", "对拍一", "--personality", "5",
         "--weak-point", "1", "--denial", "kiss=no", "--denial", "aibu=yes"],
        ["--last", "二次", "--first", "改名", "--nick", "对拍二", "--personality", "31",
         "--weak-point", "3", "--denial", "kiss=yes", "--denial", "anal=no"],
    ]
    for idx, args in enumerate(rounds, 1):
        outs = {}
        for tag, exe, enc in (("cs", CS, "gbk"), ("go", GO, "utf-8")):
            s = sides[tag]
            p = subprocess.run([exe, "card-edit", s["card"]] + args + ["--archive", s["arch"]],
                               capture_output=True)
            outs[tag] = (p.returncode, norm(p.stdout, enc, s), p.stderr.decode(enc, "replace"))
            if p.returncode != 0 and p.stderr.strip():
                print("      %s stderr：%s" % (tag, _safe(p.stderr.decode(enc, "replace"))[:300]))
        if not _report("第 %d 轮编辑" % idx, outs["go"][1], outs["go"][0], outs["cs"][1], outs["cs"][0]):
            ok = False
        ha, hb = _sha256(sides["cs"]["card"]), _sha256(sides["go"]["card"])
        same = ha == hb
        print("  %-40s %s / %s → %s" % ("第 %d 轮编辑后文件 SHA256" % idx, ha[:16], hb[:16],
                                        "一致" if same else "不一致"))
        if not same:
            ok = False
            print("      首个差异偏移 %d（C# vs Go）" % _first_diff(sides["cs"]["card"], sides["go"]["card"]))
    for tag in ("cs", "go"):
        s = sides[tag]
        files = sorted(os.listdir(s["arch"]))
        s["archFiles"] = [(re.sub(r"^\d{8}_\d{6}_\d{3}_", "<STAMP>_", name), _sha256(os.path.join(s["arch"], name)))
                          for name in files]
    same = sides["cs"]["archFiles"] == sides["go"]["archFiles"]
    print("  %-40s 件 %d/%d → %s" % ("留档件（名归一 + 内容哈希）", len(sides["cs"]["archFiles"]),
                                    len(sides["go"]["archFiles"]), "一致" if same else "不一致"))
    if not same:
        ok = False
        for i in range(max(len(sides["cs"]["archFiles"]), len(sides["go"]["archFiles"]))):
            va = sides["cs"]["archFiles"][i] if i < len(sides["cs"]["archFiles"]) else "<缺失>"
            vb = sides["go"]["archFiles"][i] if i < len(sides["go"]["archFiles"]) else "<缺失>"
            if va != vb:
                print("      C# %r" % (va,))
                print("      Go %r" % (vb,))
                break
    shutil.rmtree(tmp, ignore_errors=True)
    print("判定：%s" % ("一致" if ok else "不一致"))
    return 0 if ok else 1


U3D_SAMPLE = r"D:\SteamLibrary\Koikatu\Mods\ForCharactersLoading\Augh\[Augh] Very Slight Anime Shadows v1.0.0.zipmod"
U3D_ENTRY = "abdata/chara/Augh/very_slight_anime.unity3d"


def cmd_u3d():
    """u3d / u3d-db 对拍：文件级列表（含 --tex 导出）+ 库级落档（首建 / 命中已有档案）。
    判据：输出逐行（路径 / 时间戳归一）+ 导出 PNG 的尺寸与张数 + mod_u3d 表行（贴图清单逐字）。"""
    print("—— u3d 专项（文件级列表 + 库级落档：首建 / 命中）")
    if not os.path.exists(U3D_SAMPLE):
        print("  样本不存在：%s" % U3D_SAMPLE)
        return 3
    tmp = os.path.join(OUT_DIR, "_u3d_tmp")
    if os.path.isdir(tmp):
        shutil.rmtree(tmp)
    os.makedirs(tmp)
    ok = True

    def norm(raw, enc, extra=()):
        lines = []
        for ln in raw.decode(enc, "replace").splitlines():
            for a, b in extra:
                ln = ln.replace(a, b)
            ln = RE_TS.sub("<TS>", ln)
            lines.append("".join(ch if ord(ch) < 128 else "?" for ch in ln))
        return lines

    # ① 文件级：列表（无导出）
    outs = {}
    for tag, exe, enc in (("cs", CS, "gbk"), ("go", GO, "utf-8")):
        p = subprocess.run([exe, "u3d", U3D_SAMPLE], capture_output=True)
        outs[tag] = (p.returncode, norm(p.stdout, enc))
        if p.returncode != 0 and p.stderr.strip():
            print("      %s stderr：%s" % (tag, _safe(p.stderr.decode(enc, "replace"))[:300]))
    if not _report("u3d 列表", outs["go"][1], outs["go"][0], outs["cs"][1], outs["cs"][0]):
        ok = False
    # ② 文件级：--tex 导出（编码器不同——只比导出件数与尺寸）
    tex = {}
    for tag, exe, enc in (("cs", CS, "gbk"), ("go", GO, "utf-8")):
        d = os.path.join(tmp, "tex-" + tag)
        p = subprocess.run([exe, "u3d", U3D_SAMPLE, "--tex", d], capture_output=True)
        files = []
        if os.path.isdir(d):
            for name in sorted(os.listdir(d)):
                png = os.path.join(d, name)
                size = os.path.getsize(png)
                dims = _png_dims(png)
                files.append((re.sub(r"u3d_\d+\.png", "u3d_<ID>.png", name), size > 0, dims))
        tex[tag] = (p.returncode, norm(p.stdout, enc, [(d, "<TEX>")]), files)
    if not _report("u3d --tex 输出", tex["go"][1], tex["go"][0], tex["cs"][1], tex["cs"][0]):
        ok = False
    same = tex["cs"][2] == tex["go"][2]
    print("  %-40s %d/%d → %s" % ("导出 PNG（名归一 / 尺寸）", len(tex["cs"][2]), len(tex["go"][2]),
                                  "一致" if same else "不一致"))
    if not same:
        ok = False
        print("      C# %r" % (tex["cs"][2],))
        print("      Go %r" % (tex["go"][2],))

    # ③ 库级：临时库 + mod 库根 → scan-mods → u3d-db 首建 / 命中
    sides = {}
    for tag, exe in (("cs", CS), ("go", GO)):
        d = os.path.join(tmp, tag)
        os.makedirs(d)
        lib = os.path.join(d, "mods_main")
        os.makedirs(lib)
        cache = os.path.join(d, "mods_cache")
        os.makedirs(cache)
        shutil.copy2(U3D_SAMPLE, os.path.join(lib, os.path.basename(U3D_SAMPLE)))
        # 缓存库放一份副本：C# 的 scan-mods 遇到「空库根」会走离线标记 → 触发 Store.AddTodo 的
        # ON CONFLICT 崩溃（0.21.9 既有缺陷，已登记在 tools/compare/README.md 已知差异）——样本规避它。
        shutil.copy2(U3D_SAMPLE, os.path.join(cache, os.path.basename(U3D_SAMPLE)))
        db = os.path.join(d, "kkmanager.db")
        p = subprocess.run([exe, "roots-list", "--db", db], capture_output=True)
        if p.returncode != 0 or not os.path.exists(db):
            print("  %s 建库失败：exit %d" % (tag, p.returncode))
            return 3
        _trim_roots_sort(db, lib, cache)
        p = subprocess.run([exe, "scan-mods", "--db", db], capture_output=True)
        if p.returncode != 0:
            print("  %s scan-mods 失败：exit %d" % (tag, p.returncode))
            return 3
        con = sqlite3.connect("file:" + db.replace("\\", "/") + "?mode=ro", uri=True)
        row = con.execute("select guid from mod order by guid limit 1").fetchone()
        con.close()
        if not row:
            print("  %s 库里没有 mod 行" % tag)
            return 3
        sides[tag] = {"db": db, "lib": lib, "guid": row[0]}
    for label in ("u3d-db 首建", "u3d-db 命中档案"):
        outs = {}
        for tag, exe, enc in (("cs", CS, "gbk"), ("go", GO, "utf-8")):
            s = sides[tag]
            p = subprocess.run([exe, "u3d-db", s["guid"], U3D_ENTRY, "--db", s["db"]], capture_output=True)
            outs[tag] = (p.returncode, norm(p.stdout, enc, [(s["lib"], "<LIB>")]))
            if p.returncode != 0 and p.stderr.strip():
                print("      %s stderr：%s" % (tag, _safe(p.stderr.decode(enc, "replace"))[:300]))
        if not _report(label, outs["go"][1], outs["go"][0], outs["cs"][1], outs["cs"][0]):
            ok = False
    rows = {}
    for tag in ("cs", "go"):
        got = _read_table_rows(os.path.join(tmp, tag),
                               "select guid, entry_path, size, texture_count, textures from mod_u3d order by entry_path")
        rows[tag] = [tuple((str(v).replace(sides[tag]["lib"], "<LIB>") if v is not None else None) for v in r)
                     for r in got]
    same = rows["cs"] == rows["go"]
    print("  %-40s 行 %d/%d → %s" % ("mod_u3d 表", len(rows["cs"]), len(rows["go"]), "一致" if same else "不一致"))
    if not same:
        ok = False
        for i in range(max(len(rows["cs"]), len(rows["go"]))):
            va = rows["cs"][i] if i < len(rows["cs"]) else "<缺失>"
            vb = rows["go"][i] if i < len(rows["go"]) else "<缺失>"
            if va != vb:
                print("      C# %r" % (va,))
                print("      Go %r" % (vb,))
                break
    shutil.rmtree(tmp, ignore_errors=True)
    print("判定：%s" % ("一致" if ok else "不一致"))
    return 0 if ok else 1


def _png_dims(path):
    """读 PNG 头拿宽高（不解码整图）——导出件尺寸判据。"""
    try:
        with open(path, "rb") as f:
            head = f.read(24)
        if len(head) < 24 or head[:8] != b"\x89PNG\r\n\x1a\n":
            return None
        return (int.from_bytes(head[16:20], "big"), int.from_bytes(head[20:24], "big"))
    except OSError:
        return None


def main(argv):
    if not argv:
        print(__doc__)
        return 2
    mode = argv[0]
    if mode == "all":
        return cmd_all()
    if mode == "thumb":
        return cmd_thumb()
    if mode == "timeline":
        return cmd_timeline()
    if mode == "card-analysis":
        return cmd_cardanalysis(argv[1] if len(argv) > 1 else SAMPLE_CARD)
    if mode == "roots":
        return cmd_roots()
    if mode == "libmap":
        return cmd_libmap()
    if mode == "composition":
        return cmd_composition()
    if mode == "pluginlog":
        return cmd_pluginlog()
    if mode == "stats":
        return cmd_stats()
    if mode == "scanmods":
        return cmd_scanmods()
    if mode == "scancards":
        return cmd_scancards()
    if mode == "scancardsanalysis":
        return cmd_scancardsanalysis()
    if mode == "scanall":
        return cmd_scanall()
    if mode == "scanallplugins":
        return cmd_scanallplugins()
    if mode == "scanplugins":
        return cmd_scanplugins()
    if mode == "query":
        return cmd_query()
    if mode == "dup":
        return cmd_dup_new()
    if mode == "refs":
        return cmd_refs()
    if mode == "index":
        return cmd_index()
    if mode == "sort":
        return cmd_sort()
    if mode == "cardsort":
        return cmd_cardsort()
    if mode == "cardsort2":
        return cmd_cardsort2()
    if mode == "cardedit":
        return cmd_cardedit()
    if mode == "u3d":
        return cmd_u3d()
    return cmd_single(mode, argv[1:])


def _prepare_scan_sample(tmp):
    """样本：三条 mod 库根覆盖三条写入路径——main（预置锁定 tier1 · 走主库）/
    cache（tier2 · 走主库）/ cold（tier3 · 自有库文件）。
    文件：真实 zipmod（含一份同 guid 跨根副本 → dup_count、一份在子目录）+
    非 mod 文本 + 坏 zip（不是容器）+ 无 manifest 容器。"""
    dirs = {name: os.path.join(tmp, "mods_" + name) for name in ("main", "cache", "cold")}
    for d in dirs.values():
        os.makedirs(d)
    cands = []
    for name in os.listdir(MOD_LIB_SRC):
        p = os.path.join(MOD_LIB_SRC, name)
        if os.path.isfile(p) and os.path.getsize(p) < 4 * 1024 * 1024:
            cands.append((os.path.getsize(p), name))
    cands.sort()
    if len(cands) < 6:
        print("  真实 mod 库可用样本不足（<6 个小 zipmod）")
        return None
    a, b, c, d1, d2 = [name for _, name in cands[:5]]
    for name in (a, b, c):
        shutil.copy2(os.path.join(MOD_LIB_SRC, name), os.path.join(dirs["main"], name))
    shutil.copy2(os.path.join(MOD_LIB_SRC, a), os.path.join(dirs["cache"], a))
    shutil.copy2(os.path.join(MOD_LIB_SRC, d1), os.path.join(dirs["cold"], d1))
    sub = os.path.join(dirs["cold"], "sub")
    os.makedirs(sub)
    shutil.copy2(os.path.join(MOD_LIB_SRC, d2), os.path.join(sub, d2))
    with open(os.path.join(dirs["main"], "readme.txt"), "w", encoding="utf-8") as f:
        f.write("not a mod\n")
    with open(os.path.join(dirs["main"], "broken.zipmod"), "wb") as f:
        f.write(b"not a zip at all")
    with zipfile.ZipFile(os.path.join(dirs["main"], "noentry.zipmod"), "w") as z:
        z.writestr("readme/abc.txt", "hello")
    return {"main": dirs["main"], "cache": dirs["cache"], "cold": dirs["cold"], "del_name": b}


def _trim_roots_for_scan(db, sample):
    """把临时库的 roots 裁成样本三条 mod 库根（main 锁定 tier1 / cache tier2 / cold tier3）——
    避免预建 96 个卡片分片库文件，并覆盖主库与库文件两条写入路径。"""
    def entry(tier, path, locked):
        return {"tier": tier, "path": path, "recurse": True, "readOnly": tier == 3,
                "locked": locked, "offline": False, "note": "", "replacedFrom": ""}

    cfg = {
        "gameRoot": "",
        "modRoots": [entry(1, sample["main"], True),
                     entry(2, sample["cache"], False),
                     entry(3, sample["cold"], False)],
        "cardRoots": [],
        "pluginRoots": [],
        "seeded": True,
    }
    con = sqlite3.connect(db)
    con.execute("INSERT INTO setting(key,value) VALUES('roots',?) "
                "ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                (json.dumps(cfg, ensure_ascii=False),))
    con.commit()
    con.close()


def _read_scan_state(dbdir):
    """读扫描结果：主库 + 全部库文件的 mod / mod_file / non_card / scan_state（忽略时间戳与 id）。
    主库持 mod 主表与走主库的副本行；库文件持走库文件的副本行。"""
    out = {"mod": [], "mod_file": [], "non_card": [], "scan_state": []}
    targets = [os.path.join(dbdir, "kkmanager.db")] + sorted(glob.glob(os.path.join(dbdir, "lib_*.db")))
    for idx, p in enumerate(targets):
        c = ro_connect(p)
        if idx == 0:
            try:
                out["mod"] = c.execute(
                    "select guid,name,version,author,website,description,schema_ver,tier,root_path,file_path,"
                    "file_name,size,mtime,entry_count,dup_count,error from mod order by guid").fetchall()
            except sqlite3.Error as exc:
                print("   读主库 mod 失败：%s" % exc)
        for name, sql in (
            ("mod_file", "select file_path,guid,tier,root_path,file_name,size,mtime from mod_file order by file_path"),
            ("non_card", "select file_path,root_path,side,reason,size,mtime from non_card order by file_path"),
            ("scan_state", "select file_path,step,size,mtime from scan_state order by file_path,step"),
        ):
            try:
                out[name].extend(c.execute(sql).fetchall())
            except sqlite3.Error as exc:
                print("   读 %s 的 %s 失败：%s" % (os.path.basename(p), name, exc))
        c.close()
    for name in out:
        out[name].sort()
    return out


def _norm_scan_line(ln):
    ln = re.sub(r"^库: .*$", "库: <DB>", ln)
    ln = re.sub(r"(mod 扫描完成：)[\d.]+ 秒", r"\1<TS> 秒", ln)
    ln = re.sub(r"^\[库数据\] 预建库文件 (\d+) 个.*$", r"[库数据] 预建库文件 \1 个", ln)
    return "".join(ch if ord(ch) < 128 else "?" for ch in ln)


def _safe(text):
    """把非 ASCII 折成 ?——控制台编码不稳时保证差异行仍可读。"""
    return "".join(ch if ord(ch) < 128 else "?" for ch in str(text))


def cmd_scanmods():
    """scan-mods 对拍：三根样本（main 锁定 tier1 走主库 / cache tier2 走主库 / cold tier3 走库文件）
    跑三轮——首扫 / 增量复扫 / 删文件后复扫。
    判据：① stdout 逐行（库路径 / 耗时 / 预建行归一）② mod / mod_file / non_card / scan_state 逐行
    （忽略 scan_time / done_at / id）③ 退出码。"""
    print("—— scan-mods 专项（临时库 + 三根样本；首扫 / 增量复扫 / 删文件后复扫）")
    tmp = os.path.join(OUT_DIR, "_scan_tmp")
    if os.path.isdir(tmp):
        shutil.rmtree(tmp)
    os.makedirs(tmp)
    sample = _prepare_scan_sample(tmp)
    if not sample:
        return 3
    print("  样本：main 3 个 zipmod + cache 1 个同 guid 副本 + cold 2 个（含 sub/ 子目录一份）"
          "+ 非 mod 文本 + 坏 zip + 无 manifest 容器")
    dirs = {}
    for tag, exe, enc in (("cs", CS, "gbk"), ("go", GO, "utf-8")):
        d = os.path.join(tmp, tag)
        os.makedirs(d)
        db = os.path.join(d, "kkmanager.db")
        p = subprocess.run([exe, "roots-list", "--db", db], capture_output=True)
        if p.returncode != 0 or not os.path.exists(db):
            print("  %s 建库失败：exit %d %s" % (tag, p.returncode, p.stdout.decode(enc, "replace")[:300]))
            return 3
        _trim_roots_for_scan(db, sample)
        dirs[tag] = db
    ok = True
    for label, delete in (("首扫", False), ("增量复扫", False), ("删文件后复扫", True)):
        if delete:
            victim = os.path.join(sample["main"], sample["del_name"])
            if os.path.exists(victim):
                os.remove(victim)
        outs = {}
        for tag, exe, enc in (("cs", CS, "gbk"), ("go", GO, "utf-8")):
            p = subprocess.run([exe, "scan-mods", "--db", dirs[tag]], capture_output=True)
            outs[tag] = (p.returncode,
                         [_norm_scan_line(ln) for ln in p.stdout.decode(enc, "replace").splitlines()],
                         p.stderr.decode(enc, "replace"))
        rc_cs, lines_cs, err_cs = outs["cs"]
        rc_go, lines_go, err_go = outs["go"]
        diff = [i for i in range(max(len(lines_cs), len(lines_go)))
                if (lines_cs[i] if i < len(lines_cs) else "<缺失>") != (lines_go[i] if i < len(lines_go) else "<缺失>")]
        state_cs = _read_scan_state(os.path.join(tmp, "cs"))
        state_go = _read_scan_state(os.path.join(tmp, "go"))
        table_bad = []
        for table in ("mod", "mod_file", "non_card", "scan_state"):
            if state_cs[table] != state_go[table]:
                table_bad.append(table)
        one = (not diff) and (not table_bad) and rc_cs == rc_go
        if not one:
            ok = False
        print("  %-10s 输出 exit %d/%d · 行 %d/%d · 差异 %d ｜ mod %d · mod_file %d · 非 mod %d · 戳 %d → %s"
              % (label, rc_cs, rc_go, len(lines_cs), len(lines_go), len(diff),
                 len(state_go["mod"]), len(state_go["mod_file"]), len(state_go["non_card"]),
                 len(state_go["scan_state"]), "一致" if one else "不一致"))
        if diff:
            for i in diff[:8]:
                print("    [%d] C# %s" % (i, _safe(lines_cs[i] if i < len(lines_cs) else "<缺失>")))
                print("         Go %s" % (_safe(lines_go[i] if i < len(lines_go) else "<缺失>"),))
            if err_cs.strip():
                print("    C# stderr：%s" % _safe(err_cs[:300]))
            if err_go.strip():
                print("    Go stderr：%s" % _safe(err_go[:300]))
        for table in table_bad:
            a, b = state_cs[table], state_go[table]
            print("    %s 行 %d/%d 不一致，首条差异：" % (table, len(a), len(b)))
            for i in range(max(len(a), len(b))):
                va = a[i] if i < len(a) else "<缺失>"
                vb = b[i] if i < len(b) else "<缺失>"
                if va != vb:
                    print("      C# %s" % _safe(va))
                    print("      Go %s" % _safe(vb))
                    break
    shutil.rmtree(tmp, ignore_errors=True)
    print("判定：%s" % ("一致" if ok else "不一致"))
    return 0 if ok else 1

FEMALE_DIR = r"D:\SteamLibrary\Koikatu\UserData\chara\female"
COORD_DIR = r"D:\SteamLibrary\Koikatu\UserData\coordinate"


def _pick_small_cards(src_dir, count, max_size=8 * 1024 * 1024):
    """从真卡片库挑最小的若干张（限体积——缩略图步耗时与卡片体积成正比）。"""
    cands = []
    if not os.path.isdir(src_dir):
        return cands
    for name in os.listdir(src_dir):
        p = os.path.join(src_dir, name)
        if os.path.isfile(p) and name.lower().endswith(".png") and os.path.getsize(p) <= max_size:
            cands.append((os.path.getsize(p), name))
    cands.sort()
    return [name for _, name in cands[:count]]


def _png_image_end(path):
    """解析 PNG 块链，返回 IEND 结束偏移（找不到 -1）。"""
    with open(path, "rb") as f:
        data = f.read()
    if len(data) < 8 or data[:4] != b"\x89PNG":
        return -1
    pos = 8
    while pos + 8 <= len(data):
        clen = int.from_bytes(data[pos:pos + 4], "big")
        typ = data[pos + 4:pos + 8]
        if typ == b"IEND":
            return pos + 12
        pos += 12 + clen
    return -1


def _prepare_card_sample(tmp):
    """样本：人物卡 ×2 + 服装卡 ×1 + 场景卡 ×1 + 纯图片 PNG + 截断 PNG + 非 PNG（.png 扩展名）。"""
    cardlib = os.path.join(tmp, "cards")
    os.makedirs(cardlib)
    picked = []
    for src, n in ((FEMALE_DIR, 2), (COORD_DIR, 1), (SCENE_DIR, 1)):
        for name in _pick_small_cards(src, n):
            shutil.copy2(os.path.join(src, name), os.path.join(cardlib, name))
            picked.append(name)
    if not picked:
        print("  真卡片库没有可用样本")
        return None
    first = os.path.join(cardlib, picked[0])
    end = _png_image_end(first)
    if end > 0:
        with open(first, "rb") as f:
            head = f.read(end)
        with open(os.path.join(cardlib, "pure-image.png"), "wb") as f:
            f.write(head)
    with open(first, "rb") as f:
        head = f.read(2048)
    with open(os.path.join(cardlib, "truncated.png"), "wb") as f:
        f.write(head)
    with open(os.path.join(cardlib, "notpng.png"), "wb") as f:
        f.write(b"this is not a png file\n")
    return {"cards": cardlib, "picked": picked, "del_name": picked[0] if len(picked) > 1 else None}


def _trim_roots_for_cards(db, cardlib, shards):
    """卡片侧 roots 裁剪：只留样本卡片库根（tier1 非锁定）+ 分片数写死（两侧同值）。"""
    cfg = {
        "gameRoot": "",
        "modRoots": [],
        "cardRoots": [{"tier": 1, "path": cardlib, "recurse": True, "readOnly": False,
                       "locked": False, "offline": False, "note": "", "replacedFrom": ""}],
        "pluginRoots": [],
        "seeded": True,
    }
    con = sqlite3.connect(db)
    con.execute("INSERT INTO setting(key,value) VALUES('roots',?) "
                "ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                (json.dumps(cfg, ensure_ascii=False),))
    con.execute("INSERT INTO setting(key,value) VALUES('scan_shards',?) "
                "ON CONFLICT(key) DO UPDATE SET value=excluded.value", (str(shards),))
    con.commit()
    con.close()


def _norm_card_line(ln):
    ln = re.sub(r"^库: .*$", "库: <DB>", ln)
    ln = re.sub(r"(扫描完成：)[\d.]+ 秒", r"\1<TS> 秒", ln)
    ln = re.sub(r"^\[库数据\] 预建库文件 (\d+) 个.*$", r"[库数据] 预建库文件 \1 个", ln)
    ln = re.sub(r"\[分片\] 首建 (\d+) 个分片库：.*$", r"[分片] 首建 \1 个分片库", ln)
    return "".join(ch if ord(ch) < 128 else "?" for ch in ln)


def cmd_scancards():
    """scan-cards 对拍：同一批卡片（人物 / 服装 / 场景 + 纯图片 + 截断 + 非 PNG）跑两轮
    （首扫 / 增量复扫）。判据：① stdout 逐行 ② card / card_mod / card_timeline / non_card /
    scan_state 逐行（thumb 只比「有无」，不比字节——编码器不同）③ 退出码。"""
    print("—— scan-cards 专项（临时库 + 卡片样本；首扫 / 增量复扫）")
    tmp = os.path.join(OUT_DIR, "_cards_tmp")
    if os.path.isdir(tmp):
        shutil.rmtree(tmp)
    os.makedirs(tmp)
    sample = _prepare_card_sample(tmp)
    if not sample:
        return 3
    shards = 4
    print("  样本 %d 张真实卡（人物 ×2 / 服装 ×1 / 场景 ×1）+ 纯图片 / 截断 / 非 PNG；分片数 %d"
          % (len(sample["picked"]), shards))
    dirs = {}
    for tag, exe, enc in (("cs", CS, "gbk"), ("go", GO, "utf-8")):
        d = os.path.join(tmp, tag)
        os.makedirs(d)
        db = os.path.join(d, "kkmanager.db")
        p = subprocess.run([exe, "roots-list", "--db", db], capture_output=True)
        if p.returncode != 0 or not os.path.exists(db):
            print("  %s 建库失败：exit %d %s" % (tag, p.returncode, p.stdout.decode(enc, "replace")[:300]))
            return 3
        _trim_roots_for_cards(db, sample["cards"], shards)
        dirs[tag] = db
    ok = True
    scan_env = dict(os.environ)
    # 并行 worker 段是 C# 的加速策略（Go 侧当前串行）——对拍固定 worker=1，避免「并行提示行」计入差异；
    # 该差异在 README「已知差异」里登记。
    scan_env["KKM_SCAN_WORKERS"] = "1"
    for label in ("首扫", "增量复扫"):
        outs = {}
        for tag, exe, enc in (("cs", CS, "gbk"), ("go", GO, "utf-8")):
            p = subprocess.run([exe, "scan-cards", "--db", dirs[tag]], capture_output=True, env=scan_env)
            outs[tag] = (p.returncode,
                         [_norm_card_line(ln) for ln in p.stdout.decode(enc, "replace").splitlines()],
                         p.stderr.decode(enc, "replace"))
        rc_cs, lines_cs, err_cs = outs["cs"]
        rc_go, lines_go, err_go = outs["go"]
        diff = [i for i in range(max(len(lines_cs), len(lines_go)))
                if (lines_cs[i] if i < len(lines_cs) else "<缺失>") != (lines_go[i] if i < len(lines_go) else "<缺失>")]
        state_cs = _read_card_state(os.path.join(tmp, "cs"))
        state_go = _read_card_state(os.path.join(tmp, "go"))
        table_bad = [t for t in ("card", "card_mod", "card_timeline", "non_card", "scan_state")
                     if state_cs[t] != state_go[t]]
        one = (not diff) and (not table_bad) and rc_cs == rc_go
        if not one:
            ok = False
        print("  %-8s 输出 exit %d/%d · 行 %d/%d · 差异 %d ｜ card %d · card_mod %d · timeline %d · 非卡 %d · 戳 %d → %s"
              % (label, rc_cs, rc_go, len(lines_cs), len(lines_go), len(diff),
                 len(state_go["card"]), len(state_go["card_mod"]), len(state_go["card_timeline"]),
                 len(state_go["non_card"]), len(state_go["scan_state"]), "一致" if one else "不一致"))
        if diff:
            for i in diff[:8]:
                print("    [%d] C# %s" % (i, _safe(lines_cs[i] if i < len(lines_cs) else "<缺失>")))
                print("         Go %s" % (_safe(lines_go[i] if i < len(lines_go) else "<缺失>"),))
            if err_cs.strip():
                print("    C# stderr：%s" % _safe(err_cs[:300]))
            if err_go.strip():
                print("    Go stderr：%s" % _safe(err_go[:300]))
        for table in table_bad:
            a, b = state_cs[table], state_go[table]
            print("    %s 行 %d/%d 不一致，首条差异：" % (table, len(a), len(b)))
            for i in range(max(len(a), len(b))):
                va = a[i] if i < len(a) else "<缺失>"
                vb = b[i] if i < len(b) else "<缺失>"
                if va != vb:
                    print("      C# %s" % _safe(va))
                    print("      Go %s" % _safe(vb))
                    break
    shutil.rmtree(tmp, ignore_errors=True)
    print("判定：%s" % ("一致" if ok else "不一致"))
    return 0 if ok else 1


def _prepare_scanall_sample(tmp):
    """四根合一：mod 主库（锁定 tier1）/ 缓存库（tier2）/ 冷冻库（tier3）+ 卡片主库（锁定）/ 附加库。
    插件根故意指向不存在的目录——两侧应报同一句「插件库根不存在」。"""
    mod_sample = _prepare_scan_sample(tmp)
    card_sample = _prepare_card_sample(tmp)
    if not mod_sample or not card_sample:
        return None
    extra = os.path.join(tmp, "cards_extra")
    os.makedirs(extra)
    picked = card_sample["picked"]
    if len(picked) > 1:
        shutil.copy2(os.path.join(card_sample["cards"], picked[1]), os.path.join(extra, picked[1]))
    return {
        "mod": mod_sample,
        "cards": card_sample["cards"],
        "cards_extra": extra,
        "plugin_missing": os.path.join(tmp, "no-such-plugin-root"),
    }


def _trim_roots_for_scanall(db, sample, shards, plugin_root=None):
    """五条库根（mod 三条 + 卡片两条）+ 插件根（缺省指向不存在目录——插件段错误行对拍用）；分片数写死两侧同值。"""
    def entry(tier, path, locked, readonly=False):
        return {"tier": tier, "path": path, "recurse": True, "readOnly": readonly,
                "locked": locked, "offline": False, "note": "", "replacedFrom": ""}

    mod = sample["mod"]
    cfg = {
        "gameRoot": "",
        "modRoots": [entry(1, mod["main"], True), entry(2, mod["cache"], False),
                     entry(3, mod["cold"], False, True)],
        "cardRoots": [entry(1, sample["cards"], True), entry(1, sample["cards_extra"], False)],
        "pluginRoots": [entry(1, plugin_root or sample["plugin_missing"], True)],
        "seeded": True,
    }
    con = sqlite3.connect(db)
    con.execute("INSERT INTO setting(key,value) VALUES('roots',?) "
                "ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                (json.dumps(cfg, ensure_ascii=False),))
    con.execute("INSERT INTO setting(key,value) VALUES('scan_shards',?) "
                "ON CONFLICT(key) DO UPDATE SET value=excluded.value", (str(shards),))
    con.commit()
    con.close()


def cmd_scanall():
    """scan-main / scan-extra 对拍：同一样本按两个范围各跑一轮（两轮共用库）。
    判据：① stdout 逐行 ② mod / mod_file / card / card_mod / non_card / scan_state 逐行 ③ 退出码。
    插件根故意缺失——两侧都应只在错误清单里报「插件库根不存在」。"""
    print("—— scan-main / scan-extra 专项（mod 三根 + 卡片两根 + 插件根缺失）")
    tmp = os.path.join(OUT_DIR, "_scanall_tmp")
    if os.path.isdir(tmp):
        shutil.rmtree(tmp)
    os.makedirs(tmp)
    sample = _prepare_scanall_sample(tmp)
    if not sample:
        return 3
    shards = 4
    dirs = {}
    for tag, exe, enc in (("cs", CS, "gbk"), ("go", GO, "utf-8")):
        d = os.path.join(tmp, tag)
        os.makedirs(d)
        db = os.path.join(d, "kkmanager.db")
        p = subprocess.run([exe, "roots-list", "--db", db], capture_output=True)
        if p.returncode != 0 or not os.path.exists(db):
            print("  %s 建库失败：exit %d %s" % (tag, p.returncode, p.stdout.decode(enc, "replace")[:300]))
            return 3
        _trim_roots_for_scanall(db, sample, shards)
        dirs[tag] = db
    scan_env = dict(os.environ)
    scan_env["KKM_SCAN_WORKERS"] = "1"
    ok = True
    for mode, label in (("scan-main", "主要库"), ("scan-extra", "追加库")):
        outs = {}
        for tag, exe, enc in (("cs", CS, "gbk"), ("go", GO, "utf-8")):
            p = subprocess.run([exe, mode, "--db", dirs[tag]], capture_output=True, env=scan_env)
            outs[tag] = (p.returncode,
                         [_norm_card_line(ln) for ln in p.stdout.decode(enc, "replace").splitlines()],
                         p.stderr.decode(enc, "replace"))
        rc_cs, lines_cs, err_cs = outs["cs"]
        rc_go, lines_go, err_go = outs["go"]
        diff = [i for i in range(max(len(lines_cs), len(lines_go)))
                if (lines_cs[i] if i < len(lines_cs) else "<缺失>") != (lines_go[i] if i < len(lines_go) else "<缺失>")]
        state_cs = _read_scanall_state(os.path.join(tmp, "cs"))
        state_go = _read_scanall_state(os.path.join(tmp, "go"))
        table_bad = [t for t in ("mod", "mod_file", "card", "card_mod", "card_timeline", "non_card", "scan_state")
                     if state_cs[t] != state_go[t]]
        one = (not diff) and (not table_bad) and rc_cs == rc_go
        if not one:
            ok = False
        print("  %-8s 输出 exit %d/%d · 行 %d/%d · 差异 %d ｜ mod %d · mod_file %d · card %d · card_mod %d · 非卡 %d · 戳 %d → %s"
              % (label, rc_cs, rc_go, len(lines_cs), len(lines_go), len(diff),
                 len(state_go["mod"]), len(state_go["mod_file"]), len(state_go["card"]),
                 len(state_go["card_mod"]), len(state_go["non_card"]), len(state_go["scan_state"]),
                 "一致" if one else "不一致"))
        if diff:
            for i in diff[:10]:
                print("    [%d] C# %s" % (i, _safe(lines_cs[i] if i < len(lines_cs) else "<缺失>")))
                print("         Go %s" % (_safe(lines_go[i] if i < len(lines_go) else "<缺失>"),))
            if err_cs.strip():
                print("    C# stderr：%s" % _safe(err_cs[:300]))
            if err_go.strip():
                print("    Go stderr：%s" % _safe(err_go[:300]))
        for table in table_bad:
            a, b = state_cs[table], state_go[table]
            print("    %s 行 %d/%d 不一致，首条差异：" % (table, len(a), len(b)))
            for i in range(max(len(a), len(b))):
                va = a[i] if i < len(a) else "<缺失>"
                vb = b[i] if i < len(b) else "<缺失>"
                if va != vb:
                    print("      C# %s" % _safe(va))
                    print("      Go %s" % _safe(vb))
                    break
    shutil.rmtree(tmp, ignore_errors=True)
    print("判定：%s" % ("一致" if ok else "不一致"))
    return 0 if ok else 1


def _prepare_plugin_sample(tmp):
    """样本：临时 BepInEx——plugins/ 下真插件 dll ×2（含子目录一份同 guid 副本）+ 非插件 dll ×1；
    config/ 下覆盖转义面（中文 / 引号 / < > & ' + / 反斜杠 / 制表符）+ 控制字符 + BOM + 空文件 + 子目录 cfg。
    判据面：插件两表逐行（sections 逐字节）+ stdout 逐行 + 三轮（首扫 / 增量复扫 / 删 dll + 改 cfg 后复扫）。"""
    root = os.path.join(tmp, "BepInEx")
    cfgdir = os.path.join(root, "config")
    plugdir = os.path.join(root, "plugins")
    sub = os.path.join(plugdir, "sub")
    os.makedirs(cfgdir)
    os.makedirs(sub)
    shutil.copy2(SAMPLE_PLUGIN, os.path.join(plugdir, "KKAPI.dll"))
    shutil.copy2(SAMPLE_PLUGIN, os.path.join(sub, "KKAPI.dll"))
    shutil.copy2(SAMPLE_NONPLUGIN, os.path.join(plugdir, "0Harmony.dll"))
    probe = (
        "## Settings file was created by plugin 测试插件 v1.2.3\n"
        "## Plugin GUID: probe.test.plugin\n"
        "\n"
        "[测试分节]\n"
        "## 中文注释：包含 <尖括号> & 和号 ' 单引号 + 加号 \"双引号\" 反斜杠 C:\\path\\file\n"
        "## 第二行注释\n"
        "# Setting type: Boolean\n"
        "# Default value: true\n"
        "# Acceptable values: true, false\n"
        "测试键 = 值123\n"
        "\n"
        "[第二分节]\n"
        "# Setting type: KeyboardShortcut\n"
        "# Default value: Tab + LeftShift\n"
        "shortcut = F9\n"
        "tab\tkey = a\tb\n"
    )
    with open(os.path.join(cfgdir, "zz-probe.cfg"), "w", encoding="utf-8", newline="\n") as f:
        f.write(probe)
    with open(os.path.join(cfgdir, "zz-ctrl.cfg"), "w", encoding="utf-8", newline="\n") as f:
        f.write("## Settings file was created by plugin CtrlTest\n# Setting type: String\nnoSectionKey = v\x01x\x1fy\n")
    with open(os.path.join(cfgdir, "zz-bom.cfg"), "w", encoding="utf-8-sig", newline="\n") as f:
        f.write("## Settings file was created by plugin BomTest v9\n[s]\nk = v\n")
    open(os.path.join(cfgdir, "zz-empty.cfg"), "w").close()
    subcfg = os.path.join(cfgdir, "zz-sub")
    os.makedirs(subcfg)
    with open(os.path.join(subcfg, "ignored.cfg"), "w", encoding="utf-8", newline="\n") as f:
        f.write("[s]\nk = v\n")
    return {"root": root, "plugins": plugdir, "config": cfgdir,
            "del_name": os.path.join(plugdir, "0Harmony.dll"),
            "cfg_name": os.path.join(cfgdir, "zz-probe.cfg")}


def _trim_roots_for_plugins(db, plugin_root):
    """插件侧 roots 裁剪：只留样本插件库根（tier1 非锁定）。"""
    cfg = {
        "gameRoot": "",
        "modRoots": [],
        "cardRoots": [],
        "pluginRoots": [{"tier": 1, "path": plugin_root, "recurse": True, "readOnly": False,
                         "locked": False, "offline": False, "note": "", "replacedFrom": ""}],
        "seeded": True,
    }
    con = sqlite3.connect(db)
    con.execute("INSERT INTO setting(key,value) VALUES('roots',?) "
                "ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                (json.dumps(cfg, ensure_ascii=False),))
    con.commit()
    con.close()


def _read_plugin_state(dbdir):
    """读插件库扫描结果：plugin_file / plugin_config 逐行（忽略 scan_time / read_at——写入时刻）。"""
    out = {"plugin_file": [], "plugin_config": []}
    p = os.path.join(dbdir, "kkmanager.db")
    c = ro_connect(p)
    try:
        out["plugin_file"] = c.execute(
            "select file_path,guid,root_path,file_name,name,version,processes,dependencies,is_ipa,size,mtime,"
            "title,description,company,copyright,product,file_version,target_framework,note "
            "from plugin_file order by file_path,guid").fetchall()
        out["plugin_config"] = c.execute(
            "select file_path,file_name,plugin_name,plugin_version,guid,size,mtime,sections,"
            "section_count,option_count,error from plugin_config order by file_path").fetchall()
    except sqlite3.Error as exc:
        print("   读 %s 失败：%s" % (os.path.basename(p), exc))
    c.close()
    return out


def _norm_plugin_line(ln):
    ln = re.sub(r"(插件库扫描完成：).*?秒", r"\1<TS> 秒", ln)
    return "".join(ch if ord(ch) < 128 else "?" for ch in ln)


def cmd_scanplugins():
    """scan-plugins 对拍：临时 BepInEx（插件 dll ×2 含子目录同 guid 副本 / 非插件 dll / 四个 cfg 覆盖转义面）
    跑三轮——首扫 / 增量复扫 / 删 dll + 改 cfg 后复扫。
    判据：① stdout 逐行（耗时归一）② plugin_file / plugin_config 逐行（sections 逐字节）③ 退出码。"""
    print("—— scan-plugins 专项（临时 BepInEx；首扫 / 增量复扫 / 删 dll + 改 cfg 后复扫）")
    tmp = os.path.join(OUT_DIR, "_plugins_tmp")
    if os.path.isdir(tmp):
        shutil.rmtree(tmp)
    os.makedirs(tmp)
    sample = _prepare_plugin_sample(tmp)
    print("  样本：plugins/ KKAPI ×2（含 sub/ 同 guid 副本）+ 0Harmony（非插件）｜config/ 4 个 cfg"
          "（转义面 / 控制字符 / BOM / 空）+ 子目录 1 个")
    dirs = {}
    for tag, exe, enc in (("cs", CS, "gbk"), ("go", GO, "utf-8")):
        d = os.path.join(tmp, tag)
        os.makedirs(d)
        db = os.path.join(d, "kkmanager.db")
        p = subprocess.run([exe, "roots-list", "--db", db], capture_output=True)
        if p.returncode != 0 or not os.path.exists(db):
            print("  %s 建库失败：exit %d %s" % (tag, p.returncode, p.stdout.decode(enc, "replace")[:300]))
            return 3
        _trim_roots_for_plugins(db, sample["root"])
        dirs[tag] = db
    ok = True
    for label, mutate in (("首扫", ""), ("增量复扫", ""), ("删 dll+改 cfg 后复扫", "mutate")):
        if mutate:
            if os.path.exists(sample["del_name"]):
                os.remove(sample["del_name"])
            with open(sample["cfg_name"], "a", encoding="utf-8", newline="\n") as f:
                f.write("追加键 = 追加值\n")
        outs = {}
        for tag, exe, enc in (("cs", CS, "gbk"), ("go", GO, "utf-8")):
            p = subprocess.run([exe, "scan-plugins", "--db", dirs[tag]], capture_output=True)
            outs[tag] = (p.returncode,
                         [_norm_plugin_line(ln) for ln in p.stdout.decode(enc, "replace").splitlines()],
                         [_norm_plugin_line(ln) for ln in p.stderr.decode(enc, "replace").splitlines()])
        rc_cs, lines_cs, err_cs = outs["cs"]
        rc_go, lines_go, err_go = outs["go"]
        diff = [i for i in range(max(len(lines_cs), len(lines_go)))
                if (lines_cs[i] if i < len(lines_cs) else "<缺失>") != (lines_go[i] if i < len(lines_go) else "<缺失>")]
        err_diff = [i for i in range(max(len(err_cs), len(err_go)))
                    if (err_cs[i] if i < len(err_cs) else "<缺失>") != (err_go[i] if i < len(err_go) else "<缺失>")]
        state_cs = _read_plugin_state(os.path.join(tmp, "cs"))
        state_go = _read_plugin_state(os.path.join(tmp, "go"))
        table_bad = [t for t in ("plugin_file", "plugin_config") if state_cs[t] != state_go[t]]
        one = (not diff) and (not err_diff) and (not table_bad) and rc_cs == rc_go
        if not one:
            ok = False
        print("  %-18s 输出 exit %d/%d · 行 %d/%d · 差异 %d ｜ 插件行 %d · 配置行 %d → %s"
              % (label, rc_cs, rc_go, len(lines_cs), len(lines_go), len(diff),
                 len(state_go["plugin_file"]), len(state_go["plugin_config"]),
                 "一致" if one else "不一致"))
        if diff:
            for i in diff[:8]:
                print("    [%d] C# %s" % (i, _safe(lines_cs[i] if i < len(lines_cs) else "<缺失>")))
                print("         Go %s" % (_safe(lines_go[i] if i < len(lines_go) else "<缺失>"),))
            if err_cs:
                print("    C# stderr：%s" % _safe(" | ".join(err_cs)[:300]))
            if err_go:
                print("    Go stderr：%s" % _safe(" | ".join(err_go)[:300]))
        for table in table_bad:
            a, b = state_cs[table], state_go[table]
            print("    %s 行 %d/%d 不一致，首条差异：" % (table, len(a), len(b)))
            for i in range(max(len(a), len(b))):
                va = a[i] if i < len(a) else "<缺失>"
                vb = b[i] if i < len(b) else "<缺失>"
                if va != vb:
                    print("      C# %s" % _safe(va))
                    print("      Go %s" % _safe(vb))
                    break
    shutil.rmtree(tmp, ignore_errors=True)
    print("判定：%s" % ("一致" if ok else "不一致"))
    return 0 if ok else 1


def _read_card_state(dbdir):
    """读卡片侧扫描结果：主库 + 全部库文件的 card / card_mod / card_timeline / card_analysis /
    non_card / scan_state（忽略时间戳与 id）。"""
    out = {"card": [], "card_mod": [], "card_timeline": [], "card_analysis": [], "non_card": [], "scan_state": []}
    targets = [os.path.join(dbdir, "kkmanager.db")] + sorted(glob.glob(os.path.join(dbdir, "lib_*.db")))
    for p in targets:
        c = ro_connect(p)
        try:
            out["card"].extend(c.execute(
                "select file_path,file_name,size,mtime,tier,root_path,folder,card_type,data_version,"
                "image_end,uar_blocks,mod_count,(thumb IS NOT NULL),chara_name from card").fetchall())
            out["card_mod"].extend(c.execute(
                "select c.file_path,cm.mod_guid,cm.property,cm.slot,cm.local_slot,cm.category_no,"
                "cm.rec_name,cm.rec_author,cm.rec_website from card_mod cm join card c on c.id=cm.card_id").fetchall())
            out["card_timeline"].extend(c.execute(
                "select c.file_path,t.has_entry,t.is_empty,t.duration,t.time_scale,t.keyframes,t.xml_length,"
                "t.hit_stage,t.error from card_timeline t join card c on c.id=t.card_id").fetchall())
            out["card_analysis"].extend(c.execute(
                "select c.file_path,a.kind,a.size,a.mtime,a.data from card_analysis a join card c on c.id=a.card_id").fetchall())
            out["non_card"].extend(c.execute(
                "select file_path,root_path,side,reason,size,mtime from non_card").fetchall())
            out["scan_state"].extend(c.execute(
                "select file_path,step,size,mtime from scan_state").fetchall())
        except sqlite3.Error as exc:
            print("   读 %s 失败：%s" % (os.path.basename(p), exc))
        c.close()
    for name in out:
        out[name].sort()
    return out


def cmd_scancardsanalysis():
    """scan-cards 开「卡片分析」组（coord / detail / scene）的落库对拍：同批卡片跑两轮（首扫 / 增量复扫）。
    判据：① stdout 逐行 ② card / card_mod / card_timeline / card_analysis / non_card / scan_state 逐行
    ③ card_analysis.data 逐字节（System.Text.Json 口径：转义 / 键序 / 浮点）④ 退出码。"""
    print("—— scan-cards 分析组专项（coord / detail / scene 落 card_analysis；首扫 / 增量复扫）")
    tmp = os.path.join(OUT_DIR, "_cardsan_tmp")
    if os.path.isdir(tmp):
        shutil.rmtree(tmp)
    os.makedirs(tmp)
    sample = _prepare_card_sample(tmp)
    if not sample:
        return 3
    shards = 4
    print("  样本 %d 张真实卡（人物 ×2 / 服装 ×1 / 场景 ×1）+ 纯图片 / 截断 / 非 PNG；分片数 %d"
          % (len(sample["picked"]), shards))
    dirs = {}
    for tag, exe, enc in (("cs", CS, "gbk"), ("go", GO, "utf-8")):
        d = os.path.join(tmp, tag)
        os.makedirs(d)
        db = os.path.join(d, "kkmanager.db")
        p = subprocess.run([exe, "roots-list", "--db", db], capture_output=True)
        if p.returncode != 0 or not os.path.exists(db):
            print("  %s 建库失败：exit %d %s" % (tag, p.returncode, p.stdout.decode(enc, "replace")[:300]))
            return 3
        _trim_roots_for_cards(db, sample["cards"], shards)
        p = subprocess.run([exe, "scan-plan", "set", ORDER_ALL, ORDER_ANALYZE, "--db", db], capture_output=True)
        if p.returncode != 0:
            print("  %s 扫描计划写入失败：exit %d" % (tag, p.returncode))
            return 3
        dirs[tag] = db
    ok = True
    scan_env = dict(os.environ)
    scan_env["KKM_SCAN_WORKERS"] = "1"
    for label in ("首扫", "增量复扫"):
        outs = {}
        for tag, exe, enc in (("cs", CS, "gbk"), ("go", GO, "utf-8")):
            p = subprocess.run([exe, "scan-cards", "--db", dirs[tag]], capture_output=True, env=scan_env,
                               timeout=600)
            outs[tag] = (p.returncode,
                         [_norm_card_line(ln) for ln in p.stdout.decode(enc, "replace").splitlines()],
                         p.stderr.decode(enc, "replace"))
        rc_cs, lines_cs, err_cs = outs["cs"]
        rc_go, lines_go, err_go = outs["go"]
        diff = [i for i in range(max(len(lines_cs), len(lines_go)))
                if (lines_cs[i] if i < len(lines_cs) else "<缺失>") != (lines_go[i] if i < len(lines_go) else "<缺失>")]
        state_cs = _read_card_state(os.path.join(tmp, "cs"))
        state_go = _read_card_state(os.path.join(tmp, "go"))
        table_bad = [t for t in ("card", "card_mod", "card_timeline", "card_analysis", "non_card", "scan_state")
                     if state_cs[t] != state_go[t]]
        one = (not diff) and (not table_bad) and rc_cs == rc_go
        if not one:
            ok = False
        print("  %-8s 输出 exit %d/%d · 行 %d/%d · 差异 %d ｜ card %d · 引用 %d · timeline %d · 分析 %d · 非卡 %d · 戳 %d → %s"
              % (label, rc_cs, rc_go, len(lines_cs), len(lines_go), len(diff),
                 len(state_go["card"]), len(state_go["card_mod"]), len(state_go["card_timeline"]),
                 len(state_go["card_analysis"]), len(state_go["non_card"]), len(state_go["scan_state"]),
                 "一致" if one else "不一致"))
        if diff:
            for i in diff[:10]:
                print("    [%d] C# %s" % (i, _safe(lines_cs[i] if i < len(lines_cs) else "<缺失>")))
                print("         Go %s" % (_safe(lines_go[i] if i < len(lines_go) else "<缺失>"),))
            if err_cs.strip():
                print("    C# stderr：%s" % _safe(err_cs[:300]))
            if err_go.strip():
                print("    Go stderr：%s" % _safe(err_go[:300]))
        for table in table_bad:
            a, b = state_cs[table], state_go[table]
            print("    %s 行 %d/%d 不一致，首条差异：" % (table, len(a), len(b)))
            for i in range(max(len(a), len(b))):
                va = a[i] if i < len(a) else "<缺失>"
                vb = b[i] if i < len(b) else "<缺失>"
                if va != vb:
                    print("      C# %s" % _safe(va))
                    print("      Go %s" % _safe(vb))
                    if table == "card_analysis" and i < len(a) and i < len(b):
                        # data 是 JSON——逐字节差异定位（去掉公共前缀）
                        sa, sb = str(a[i][4]), str(b[i][4])
                        cut = 0
                        while cut < min(len(sa), len(sb)) and sa[cut] == sb[cut]:
                            cut += 1
                        print("      首差字节偏移 %d" % cut)
                        print("      C# …%s" % _safe(sa[max(0, cut - 40):cut + 120]))
                        print("      Go …%s" % _safe(sb[max(0, cut - 40):cut + 120]))
                    break
    shutil.rmtree(tmp, ignore_errors=True)
    print("判定：%s" % ("一致" if ok else "不一致"))
    return 0 if ok else 1


def cmd_scanallplugins():
    """scan-main 插件段专项：主要库扫描（插件根存在）——验证 withPlugins 编排（步数 / 进度行 / 计数）。
    判据：① stdout / stderr 逐行 ② mod / mod_file / card / card_mod / card_timeline / non_card /
    plugin_file / plugin_config / scan_state 逐行 ③ 退出码；两轮（首扫 / 增量复扫）。"""
    print("—— scan-main 插件段专项（主要库 + 插件根存在；首扫 / 增量复扫）")
    tmp = os.path.join(OUT_DIR, "_scanallp_tmp")
    if os.path.isdir(tmp):
        shutil.rmtree(tmp)
    os.makedirs(tmp)
    sample = _prepare_scanall_sample(tmp)
    plugin_sample = _prepare_plugin_sample(tmp)
    if not sample:
        return 3
    shards = 4
    print("  样本：mod 三根 + 卡片两根 + BepInEx（2 dll 含 sub/ 同 guid 副本 + 4 cfg）；分片数 %d" % shards)
    dirs = {}
    for tag, exe, enc in (("cs", CS, "gbk"), ("go", GO, "utf-8")):
        d = os.path.join(tmp, tag)
        os.makedirs(d)
        db = os.path.join(d, "kkmanager.db")
        p = subprocess.run([exe, "roots-list", "--db", db], capture_output=True)
        if p.returncode != 0 or not os.path.exists(db):
            print("  %s 建库失败：exit %d %s" % (tag, p.returncode, p.stdout.decode(enc, "replace")[:300]))
            return 3
        _trim_roots_for_scanall(db, sample, shards, plugin_root=plugin_sample["root"])
        dirs[tag] = db
    scan_env = dict(os.environ)
    scan_env["KKM_SCAN_WORKERS"] = "1"
    ok = True
    for label in ("首扫", "增量复扫"):
        outs = {}
        for tag, exe, enc in (("cs", CS, "gbk"), ("go", GO, "utf-8")):
            p = subprocess.run([exe, "scan-main", "--db", dirs[tag]], capture_output=True, env=scan_env,
                               timeout=600)
            outs[tag] = (p.returncode,
                         [_norm_card_line(ln) for ln in p.stdout.decode(enc, "replace").splitlines()],
                         [_norm_card_line(ln) for ln in p.stderr.decode(enc, "replace").splitlines()])
        rc_cs, lines_cs, err_cs = outs["cs"]
        rc_go, lines_go, err_go = outs["go"]
        diff = [i for i in range(max(len(lines_cs), len(lines_go)))
                if (lines_cs[i] if i < len(lines_cs) else "<缺失>") != (lines_go[i] if i < len(lines_go) else "<缺失>")]
        state_cs = _read_scanall_state(os.path.join(tmp, "cs"))
        state_go = _read_scanall_state(os.path.join(tmp, "go"))
        state_cs.update(_read_plugin_state(os.path.join(tmp, "cs")))
        state_go.update(_read_plugin_state(os.path.join(tmp, "go")))
        table_bad = [t for t in ("mod", "mod_file", "card", "card_mod", "card_timeline", "non_card",
                                 "plugin_file", "plugin_config", "scan_state")
                     if state_cs[t] != state_go[t]]
        one = (not diff) and (not table_bad) and rc_cs == rc_go
        if not one:
            ok = False
        print("  %-8s 输出 exit %d/%d · 行 %d/%d · 差异 %d ｜ mod %d · mod_file %d · card %d · 引用 %d · 非卡 %d · 插件 %d · 配置 %d · 戳 %d → %s"
              % (label, rc_cs, rc_go, len(lines_cs), len(lines_go), len(diff),
                 len(state_go["mod"]), len(state_go["mod_file"]), len(state_go["card"]),
                 len(state_go["card_mod"]), len(state_go["non_card"]),
                 len(state_go["plugin_file"]), len(state_go["plugin_config"]), len(state_go["scan_state"]),
                 "一致" if one else "不一致"))
        if diff:
            for i in diff[:10]:
                print("    [%d] C# %s" % (i, _safe(lines_cs[i] if i < len(lines_cs) else "<缺失>")))
                print("         Go %s" % (_safe(lines_go[i] if i < len(lines_go) else "<缺失>"),))
            if err_cs:
                print("    C# stderr：%s" % _safe(" | ".join(err_cs)[:300]))
            if err_go:
                print("    Go stderr：%s" % _safe(" | ".join(err_go)[:300]))
        for table in table_bad:
            a, b = state_cs[table], state_go[table]
            print("    %s 行 %d/%d 不一致，首条差异：" % (table, len(a), len(b)))
            for i in range(max(len(a), len(b))):
                va = a[i] if i < len(a) else "<缺失>"
                vb = b[i] if i < len(b) else "<缺失>"
                if va != vb:
                    print("      C# %s" % _safe(va))
                    print("      Go %s" % _safe(vb))
                    break
    shutil.rmtree(tmp, ignore_errors=True)
    print("判定：%s" % ("一致" if ok else "不一致"))
    return 0 if ok else 1


def _read_scanall_state(dbdir):
    """读统一扫描结果：mod / mod_file / card / card_mod / card_timeline / non_card / scan_state（忽略时间戳与 id）。"""
    out = {"mod": [], "mod_file": [], "card": [], "card_mod": [], "card_timeline": [], "non_card": [], "scan_state": []}
    targets = [os.path.join(dbdir, "kkmanager.db")] + sorted(glob.glob(os.path.join(dbdir, "lib_*.db")))
    for idx, p in enumerate(targets):
        c = ro_connect(p)
        if idx == 0:
            try:
                out["mod"] = c.execute(
                    "select guid,name,version,author,website,description,schema_ver,tier,root_path,file_path,"
                    "file_name,size,mtime,entry_count,dup_count,error from mod order by guid").fetchall()
            except sqlite3.Error as exc:
                print("   读主库 mod 失败：%s" % exc)
        try:
            out["mod_file"].extend(c.execute(
                "select file_path,guid,tier,root_path,file_name,size,mtime from mod_file").fetchall())
            out["card"].extend(c.execute(
                "select file_path,file_name,size,mtime,tier,root_path,folder,card_type,data_version,"
                "image_end,uar_blocks,mod_count,(thumb IS NOT NULL),chara_name from card").fetchall())
            out["card_mod"].extend(c.execute(
                "select c.file_path,cm.mod_guid,cm.property,cm.slot,cm.local_slot,cm.category_no,"
                "cm.rec_name,cm.rec_author,cm.rec_website from card_mod cm join card c on c.id=cm.card_id").fetchall())
            out["card_timeline"].extend(c.execute(
                "select c.file_path,t.has_entry,t.is_empty,t.duration,t.time_scale,t.keyframes,t.xml_length,"
                "t.hit_stage,t.error from card_timeline t join card c on c.id=t.card_id").fetchall())
            out["non_card"].extend(c.execute(
                "select file_path,root_path,side,reason,size,mtime from non_card").fetchall())
            out["scan_state"].extend(c.execute(
                "select file_path,step,size,mtime from scan_state").fetchall())
        except sqlite3.Error as exc:
            print("   读 %s 失败：%s" % (os.path.basename(p), exc))
        c.close()
    for name in out:
        out[name].sort()
    return out


# （入口块在文件末尾——本文件后段还有函数定义，main 调用须在全部定义之后）


# ---------- card-sort 专项②：learn / layout ----------

def _make_cardsort2_sample(tmp):
    """learn/layout 样本：整理根结构（01-卡片/人物卡|服装卡/<作品>/<文件> · 02-mod/散件 · 05-其它）
    + 索引 TSV（表头按列名定位——learn 读「角色名/作品」，layout 读「现路径/类型/卡类型/作品/作者/大小」）。
    四个 by 值各用一份独立整理根（dest_<by>），互不干扰。"""
    picked = []
    for src_dir, n in ((FEMALE_DIR, 2), (COORD_DIR, 1)):
        picked += [os.path.join(src_dir, name) for name in _pick_small_cards(src_dir, n)]
    if len(picked) < 3:
        return None
    mods = sorted((os.path.getsize(os.path.join(MOD_LIB_SRC, nm)), nm)
                  for nm in os.listdir(MOD_LIB_SRC)
                  if os.path.isfile(os.path.join(MOD_LIB_SRC, nm))
                  and os.path.getsize(os.path.join(MOD_LIB_SRC, nm)) < 4 * 1024 * 1024)
    if not mods:
        return None
    a_name, b_name, c_name = os.path.basename(picked[0]), os.path.basename(picked[1]), os.path.basename(picked[2])
    rel_a = "01-卡片/人物卡/作品A/" + a_name
    rel_b = "01-卡片/人物卡/作品B/" + b_name
    rel_c = "01-卡片/服装卡/作品A/" + c_name
    rel_mod = "02-mod/散件/" + mods[0][1]
    dests = {}
    for by in ("work", "author", "chara", "flat"):
        d = os.path.join(tmp, "dest_" + by)
        dests[by] = d
        for rel, src in ((rel_a, picked[0]), (rel_b, picked[1]), (rel_c, picked[2]),
                         (rel_mod, os.path.join(MOD_LIB_SRC, mods[0][1]))):
            dst = os.path.join(d, rel.replace("/", os.sep))
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            shutil.copy2(src, dst)
        note = os.path.join(d, "05-其它", "notes.txt")
        os.makedirs(os.path.dirname(note), exist_ok=True)
        with open(note, "w", encoding="utf-8") as f:
            f.write("not a card\n")
    head = ["序号", "类型", "卡类型", "角色名", "作品", "作者", "来源", "包内路径", "大小", "现路径"]
    rows = [
        head,
        ["1", "卡", "人物卡", "角色甲", "作品A", "作者甲", "", "", "1", rel_a],
        ["2", "卡", "人物卡", "角色乙", "作品B", "作者乙", "", "", "2", rel_b],
        ["3", "卡", "服装卡", "", "作品A", "作者甲", "", "", "3", rel_c],
        ["4", "mod", "mod", "", "", "作者甲", "", "", "4", rel_mod],
        # 学习面（现路径以「（」开头——layout 跳过，learn 只读角色名 / 作品两列）
        ["5", "卡", "人物卡", "角色甲", "作品A", "作者甲", "", "", "1", "（学习样本）"],
        ["6", "卡", "人物卡", "角色甲", "作品B", "作者甲", "", "", "1", "（学习样本）"],
        ["7", "卡", "人物卡", "角色丙", "（待归包）", "作者丙", "", "", "1", "（学习样本）"],
        ["8", "卡", "人物卡", "", "作品D", "作者丁", "", "", "1", "（学习样本）"],
        # 源已不存在的卡（layout 应判 skipped：源已不在）
        ["9", "卡", "人物卡", "角色丁", "作品E", "作者丁", "", "", "1", "01-卡片/人物卡/作品E/not-exist.png"],
    ]
    idx = os.path.join(tmp, "index.tsv")
    with open(idx, "w", encoding="utf-8-sig", newline="\n") as f:
        for r in rows:
            f.write("\t".join(r) + "\n")
    return {
        "dest_work": dests["work"], "dest_author": dests["author"],
        "dest_chara": dests["chara"], "dest_flat": dests["flat"],
        "idx": idx,
        "plan_work": os.path.join(tmp, "plan-work.tsv"),
        "plan_author": os.path.join(tmp, "plan-author.tsv"),
        "plan_chara": os.path.join(tmp, "plan-chara.tsv"),
        "plan_flat": os.path.join(tmp, "plan-flat.tsv"),
        "rules": os.path.join(tmp, "rules-learn.tsv"),
        "db": os.path.join(tmp, "kkmanager.db"),
    }


def _norm_layout_lines(raw, enc, s, by):
    """layout/learn 输出归一：各自整理根 / 索引 / 计划 / 库路径折占位（保留中文原文——两侧各自解码）。"""
    dest = s["dest_" + by] if by else s["dest_work"]
    lines = []
    for ln in raw.decode(enc, "replace").splitlines():
        ln = ln.replace(s["plan_" + by] if by else s["plan_work"], "<PLAN>")
        ln = ln.replace(dest, "<DEST>").replace(s["idx"], "<IDX>")
        ln = ln.replace(s["rules"], "<RULES>").replace(s["db"], "<DB>")
        ln = ln.replace(os.path.dirname(s["db"]), "<DBDIR>")
        ln = RE_TS.sub("<TS>", ln)
        lines.append(ln)
    return lines


def cmd_cardsort2():
    """card-sort 专项②：learn（索引 → 角色名规则投票）+ layout（--by work|author|chara|flat 重排全链）。
    判据：每步输出逐行（路径 / 时间戳归一，保留中文）+ 计划 TSV + card_rule 表行 + 终态文件树。"""
    print("—— card-sort 专项②（learn / layout：四维重排 + 索引学习）")
    tmp = os.path.join(OUT_DIR, "_cardsort2_tmp")
    if os.path.isdir(tmp):
        shutil.rmtree(tmp)
    os.makedirs(tmp)
    sides = {}
    for tag, exe in (("cs", CS), ("go", GO)):
        d = os.path.join(tmp, tag)
        os.makedirs(d)
        s = _make_cardsort2_sample(d)
        if s is None:
            print("  卡片 / mod 样本不足")
            return 3
        p = subprocess.run([exe, "roots-list", "--db", s["db"]], capture_output=True)
        if p.returncode != 0 or not os.path.exists(s["db"]):
            print("  %s 建库失败：exit %d" % (tag, p.returncode))
            return 3
        sides[tag] = s
    ok = True

    def run(tag, args, by=None):
        s = sides[tag]
        exe, enc = (CS, "gbk") if tag == "cs" else (GO, "utf-8")
        real = []
        for a in args:
            a = a.replace("<DEST>", s["dest_" + by] if by else s["dest_work"])
            a = a.replace("<IDX>", s["idx"]).replace("<RULES>", s["rules"])
            a = a.replace("<PLAN>", s["plan_" + by] if by else s["plan_work"])
            real.append(a)
        p = subprocess.run([exe, "card-sort"] + real + ["--db", s["db"]], capture_output=True)
        return p.returncode, _norm_layout_lines(p.stdout, enc, s, by), p.stderr.decode(enc, "replace")

    def step(label, args, by=None):
        nonlocal ok
        outs = {}
        for tag in ("cs", "go"):
            rc, lines, err = run(tag, args, by)
            outs[tag] = (rc, lines)
            if rc != 0 and err.strip():
                print("      %s stderr：%s" % (tag, _safe(err)[:300]))
        if not _report(label, outs["go"][1], outs["go"][0], outs["cs"][1], outs["cs"][0]):
            ok = False

    def file_equal(label, path_of, by=None):
        nonlocal ok
        texts = {}
        for tag in ("cs", "go"):
            path = path_of(sides[tag], by)
            try:
                with open(path, "r", encoding="utf-8-sig") as f:
                    dest = sides[tag]["dest_" + by] if by else sides[tag]["dest_work"]
                    texts[tag] = [ln.replace(dest, "<DEST>").replace(sides[tag]["idx"], "<IDX>")
                                  for ln in f.read().splitlines()]
            except OSError as exc:
                texts[tag] = ["<读不到> %s" % exc]
        same = texts["cs"] == texts["go"]
        print("  %-44s 行 %d/%d → %s" % (label, len(texts["cs"]), len(texts["go"]), "一致" if same else "不一致"))
        if not same:
            ok = False
            for i in range(max(len(texts["cs"]), len(texts["go"]))):
                va = texts["cs"][i] if i < len(texts["cs"]) else "<缺失>"
                vb = texts["go"][i] if i < len(texts["go"]) else "<缺失>"
                if va != vb:
                    print("      C# %r" % (va,))
                    print("      Go %r" % (vb,))
                    break

    # ① learn：索引学习（投票 / 待归包跳过 / 空角色跳过）+ 导出规则
    step("learn --from（学规则）", ["learn", "--from", "<IDX>", "--write", "<RULES>"])
    file_equal("learn 导出规则 TSV", lambda s, by: s["rules"])
    rows = {}
    for tag in ("cs", "go"):
        got = _read_table_rows(os.path.dirname(sides[tag]["db"]),
                               "select kind, match, value, origin, votes, works, enabled from card_rule order by kind, match")
        rows[tag] = got
    same = rows["cs"] == rows["go"]
    print("  %-44s 行 %d/%d → %s" % ("card_rule（learn 后）", len(rows["cs"]), len(rows["go"]), "一致" if same else "不一致"))
    if not same:
        ok = False
        for i in range(max(len(rows["cs"]), len(rows["go"]))):
            va = rows["cs"][i] if i < len(rows["cs"]) else "<缺失>"
            vb = rows["go"][i] if i < len(rows["go"]) else "<缺失>"
            if va != vb:
                print("      C# %r" % (va,))
                print("      Go %r" % (vb,))
                break

    # ② layout：四个 by 各一轮（计划写出 → 输出 → 执行 → 空目录清理 → 状态回写）
    for by in ("work", "author", "chara", "flat"):
        step("layout --by %s（计划）" % by, ["layout", "--root", "<DEST>", "--index", "<IDX>", "--by", by, "--out", "<PLAN>"], by)
        file_equal("layout --by %s 计划 TSV" % by, lambda s, b: s["plan_" + b], by)
        step("layout --by %s --yes（执行）" % by, ["layout", "--root", "<DEST>", "--index", "<IDX>", "--by", by, "--out", "<PLAN>", "--yes"], by)
        file_equal("layout --by %s 回写 TSV" % by, lambda s, b: s["plan_" + b], by)
        tree = {}
        for tag in ("cs", "go"):
            tree[tag] = _tree_snapshot(sides[tag]["dest_" + by])
        same = tree["cs"] == tree["go"]
        print("  %-44s 条目 %d/%d → %s" % ("终态文件树（dest_%s）" % by, len(tree["cs"]), len(tree["go"]), "一致" if same else "不一致"))
        if not same:
            ok = False
            for i in range(max(len(tree["cs"]), len(tree["go"]))):
                va = tree["cs"][i] if i < len(tree["cs"]) else "<缺失>"
                vb = tree["go"][i] if i < len(tree["go"]) else "<缺失>"
                if va != vb:
                    print("      C# %r" % (va,))
                    print("      Go %r" % (vb,))
                    break

    # ③ 参数闸门：--by 非法 / 缺 --index（两侧同 exit 2 + 同措辞）
    step("layout --by 非法值", ["layout", "--root", "<DEST>", "--index", "<IDX>", "--by", "bogus"])
    step("layout 缺 --index", ["layout", "--root", "<DEST>", "--by", "work"])
    step("learn 缺 --from", ["learn"])

    shutil.rmtree(tmp, ignore_errors=True)
    print("判定：%s" % ("一致" if ok else "不一致"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
