"""kkgo ↔ C# 对拍工具（G 段验收：同输入同参数，输出数据字段级一致）。

判据（design-kkm-go.md §五）：
  - 退出码相等；
  - 行数相等；
  - 每行剔除非 ASCII 后逐字相同（C# 控制台中文是 GBK 输出被按 UTF-8 读，属编码伪差异，
    故两侧都按各自真实编码解码后再做 ASCII 投影）；
  - thumb 例外：只比尺寸 / 卡类型 / 图片区（编码器不同，字节数不作判据）。

用法：
  py compare.py all                        # 批量跑 CASES 里全部命令
  py compare.py card-detail <card.png>     # 单命令对拍
  py compare.py thumb                      # 缩略图专项（Go 与 C# 各出 jpg 再比尺寸）
  py compare.py timeline                   # timeline 轨道专项（自动挑关键帧最多的场景卡）

样本路径是本机现值，可在文件顶部 SAMPLE_* 里改，或用第三参显式给。
"""
import os
import re
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
    print("合计 %d 项 · 不一致 %d 项" % (len(CASES), bad))
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
    return cmd_single(mode, argv[1:])


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
