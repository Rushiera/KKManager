"""kkgo ↔ C# 对拍工具（G 段验收：同输入同参数，输出数据字段级一致）。

判据（design-kkm-go.md §五）：
  - 退出码相等；
  - 行数相等；
  - 每行剔除非 ASCII 后逐字相同（C# 控制台中文是 GBK 输出被按 UTF-8 读，属编码伪差异，
    故两侧都按各自真实编码解码后再做 ASCII 投影）；
  - thumb 例外：只比尺寸 / 卡类型 / 图片区（编码器不同，字节数不作判据）。

用法：
  py compare.py all                        # 批量跑 CASES 里全部命令 + card-analysis 落库对拍
  py compare.py card-detail <card.png>     # 单命令对拍
  py compare.py thumb                      # 缩略图专项（Go 与 C# 各出 jpg 再比尺寸）
  py compare.py timeline                   # timeline 轨道专项（自动挑关键帧最多的场景卡）
  py compare.py card-analysis [card.png]   # 分项分析落库 JSON 对拍（C# 侧无同名命令——见下）

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

GO = r"D:\Mau\WorkSpace\KKManager\kkgo\kkgo.exe"
CS = r"D:\Mau\WorkSpace\KKManager\dist\KMR_A.exe"
SAMPLE_CARD = r"D:\SteamLibrary\Koikatu\UserData\chara\female\100065348_p6.png"
SAMPLE_SCENE = r"D:\SteamLibrary\Koikatu\UserData\Studio\scene\2021_0403_1423_03_837.png"
SAMPLE_MOD = r"D:\SteamLibrary\Koikatu\Mods\Cissoia.zipmod"
SCENE_DIR = r"D:\SteamLibrary\Koikatu\UserData\Studio\scene"
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
]

# 扫描计划全集顺序（临时库准备时用——内置顺序，见 C# ScanPlanCatalog.Defs）
ORDER_ALL = "row,modrow,head,refs,name,timeline,coord,thumb,detail,scene,composition,u3d"
# 分项分析组（可选组——全开才生效：coord + detail + scene）
ORDER_ANALYZE = "coord,detail,scene"


def projection(raw, encoding):
    lines = raw.decode(encoding, "replace").splitlines()
    return ["".join(ch if ord(ch) < 128 else "." for ch in ln) for ln in lines]


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
    print("合计 %d 项 · 不一致 %d 项" % (len(CASES) + 2, bad))
    return 0 if bad == 0 else 1


def cmd_single(cmd, args):
    ok, rgo, rcs, ngo, ncs, diff = compare_one(cmd, args)
    print("命令 %s 附加 %s" % (cmd, args[1:]))
    print("exit go=%d cs=%d · 行 %d/%d · 差异 %d" % (rgo, rcs, ngo, ncs, diff))
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
    return cmd_single(mode, argv[1:])


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
