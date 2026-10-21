#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""校验产出的 .NET Framework 可执行文件结构是否符合 build.bat 的等价产物。"""
import sys, struct, os
import pefile, dnfile

path = sys.argv[1]
print("文件: %s" % path)
print("大小: %d 字节 (%.1f KB)" % (os.path.getsize(path), os.path.getsize(path) / 1024.0))

pe = pefile.PE(path, fast_load=False)
fails = []

def check(name, ok, detail=""):
    print("  [%s] %s%s" % ("OK " if ok else "FAIL", name, ("  -> " + detail) if detail else ""))
    if not ok:
        fails.append(name)

MACHINE = {0x14C: "I386(x86)", 0x8664: "AMD64(x64)", 0x1C0: "ARM"}
SUBSYS = {2: "IMAGE_SUBSYSTEM_WINDOWS_GUI", 3: "IMAGE_SUBSYSTEM_WINDOWS_CUI"}

print("\n== 1. PE 头 ==")
check("DOS/PE 签名有效", pe.DOS_HEADER.e_magic == 0x5A4D and pe.NT_HEADERS.Signature == 0x4550)
check("机器类型 = I386 (AnyCPU 的 PE32)", pe.FILE_HEADER.Machine == 0x14C,
      MACHINE.get(pe.FILE_HEADER.Machine, hex(pe.FILE_HEADER.Machine)))
check("子系统 = GUI (对应 /target:winexe)", pe.OPTIONAL_HEADER.Subsystem == 2,
      SUBSYS.get(pe.OPTIONAL_HEADER.Subsystem, str(pe.OPTIONAL_HEADER.Subsystem)))
check("无 DLL 标志 (可执行)", not (pe.FILE_HEADER.Characteristics & 0x2000))
print("  Characteristics = 0x%04X (EXECUTABLE_IMAGE=%s, LARGE_ADDRESS_AWARE=%s, 32BIT_MACHINE=%s)"
      % (pe.FILE_HEADER.Characteristics,
         bool(pe.FILE_HEADER.Characteristics & 0x2),
         bool(pe.FILE_HEADER.Characteristics & 0x20),
         bool(pe.FILE_HEADER.Characteristics & 0x100)))

print("\n== 2. CLR (.NET) 头 ==")
com = pe.OPTIONAL_HEADER.DATA_DIRECTORY[14]
check("存在 CLR 运行时头", com.VirtualAddress != 0 and com.Size > 0,
      "RVA=0x%X Size=%d" % (com.VirtualAddress, com.Size))

# 手工解析 IMAGE_COR20_HEADER（pefile 需显式 parse_data_directories 才有 .struct）
cor = pe.get_data(com.VirtualAddress, 72)
cb, major, minor, meta_rva, meta_size, flags, entry = struct.unpack_from("<IHHIIII", cor, 0)
print("  COR20: cb=%d 运行时版本=%d.%d MetaData RVA=0x%X Size=%d Flags=0x%X EntryPoint=0x%X"
      % (cb, major, minor, meta_rva, meta_size, flags, entry))
check("COR20 头长度有效", cb >= 72, str(cb))
check("标志含 ILONLY (纯托管程序集)", bool(flags & 0x1), "Flags=0x%X" % flags)
check("未置位 32BITREQUIRED (对应 /platform:anycpu)", not (flags & 0x2), "Flags=0x%X" % flags)
check("未置位 32BITPREFERRED (非 32 位优先)", not (flags & 0x20000), "Flags=0x%X" % flags)
check("入口点 token 有效 (MethodDef 0x06xxxxxx)", (entry >> 24) == 0x06, "0x%X" % entry)
meta_sig = pe.get_data(meta_rva, 4)
check("元数据签名 BSJB", meta_sig == b"BSJB", repr(meta_sig))

print("\n== 3. 资源：内嵌清单 + 图标 ==")
RT = {1: "RT_CURSOR", 2: "RT_BITMAP", 3: "RT_ICON", 12: "RT_GROUP_CURSOR",
      14: "RT_GROUP_ICON", 16: "RT_VERSION", 24: "RT_MANIFEST"}
names = set()
manifest_bytes = b""
for entry in getattr(pe, "DIRECTORY_ENTRY_RESOURCE", pe).entries if hasattr(pe, "DIRECTORY_ENTRY_RESOURCE") else []:
    rid = entry.id
    names.add(RT.get(rid, str(rid)))
    if rid == 24:
        for lang in entry.directory.entries:
            d = lang.directory.entries[0].data.struct
            manifest_bytes = pe.get_data(d.OffsetToData, d.Size)
check("存在内嵌应用清单 RT_MANIFEST", "RT_MANIFEST" in names)
check("存在图标资源 RT_ICON", "RT_ICON" in names)
check("存在图标组 RT_GROUP_ICON", "RT_GROUP_ICON" in names)

# 图标不只是「有」就行：exe 里那一份必须和 card.ico 一模一样。
# csc 会把 .ico 拆成「若干 RT_ICON 负载 + 一个 RT_GROUP_ICON 目录」，所以要比两件事：
#   1. RT_GROUP_ICON 的档数/尺寸/长度，和 .ico 目录逐档对齐；
#   2. 每个 RT_ICON 的字节，和 .ico 里对应那一档的负载逐字节相同。
# 少了任何一条，都可能出现「图标换了但 exe 里还是旧的」这种只看名字看不出来的事。
def _ico_entries(ico_path):
    with open(ico_path, "rb") as fh:
        data = fh.read()
    rsv, typ, cnt = struct.unpack_from("<HHH", data, 0)
    out = []
    off = 6
    for _ in range(cnt):
        w, h, _c, _r, planes, bpp, size, offset = struct.unpack_from("<BBBBHHII", data, off)
        out.append({"w": w or 256, "h": h or 256, "planes": planes, "bpp": bpp,
                    "size": size, "payload": data[offset:offset + size]})
        off += 16
    return out


def _find_ico():
    here = os.path.dirname(os.path.abspath(__file__))
    for cand in (os.path.join(here, "card.ico"),
                 os.path.join(here, "src", "XiaoJian", "card.ico"),
                 os.path.join(here, "..", "src", "XiaoJian", "card.ico"),
                 "/tmp/XiaoJian/src/XiaoJian/card.ico"):
        if os.path.isfile(cand):
            return os.path.normpath(cand)
    return None


ico_path = _find_ico()
if ico_path is None:
    check("找得到 card.ico 以核对内嵌图标", False, "交付目录与源码目录都没有")
else:
    ico = _ico_entries(ico_path)
    print("  对照的 card.ico: %s（%d 档）" % (ico_path, len(ico)))

    group_bytes = b""
    icon_by_id = {}
    for entry in pe.DIRECTORY_ENTRY_RESOURCE.entries:
        rid = entry.id
        if rid == 14:
            for lang in entry.directory.entries:
                d = lang.directory.entries[0].data.struct
                group_bytes = pe.get_data(d.OffsetToData, d.Size)
        elif rid == 3:
            for lang in entry.directory.entries:
                d = lang.directory.entries[0].data.struct
                icon_by_id[lang.id] = pe.get_data(d.OffsetToData, d.Size)

    if not group_bytes:
        check("读得到 RT_GROUP_ICON 内容", False)
    else:
        _rsv, _typ, gcount = struct.unpack_from("<HHH", group_bytes, 0)
        group = []
        for i in range(gcount):
            gw, gh, _cc, _rr, gplanes, gbpp, gsize, gid = \
                struct.unpack_from("<BBBBHHIH", group_bytes, 6 + 14 * i)
            group.append({"w": gw or 256, "h": gh or 256, "planes": gplanes, "bpp": gbpp,
                          "size": gsize, "id": gid})
        check("RT_GROUP_ICON 的档数与 card.ico 一致（%d 档）" % len(ico),
              gcount == len(ico), "exe 里 %d 档，card.ico %d 档" % (gcount, len(ico)))
        check("RT_GROUP_ICON 的档位序列与 card.ico 一致",
              [g["w"] for g in group] == [e["w"] for e in ico],
              "exe=%s ico=%s" % ([g["w"] for g in group], [e["w"] for e in ico]))
        check("RT_GROUP_ICON 每档声明的长度与 card.ico 一致",
              [g["size"] for g in group] == [e["size"] for e in ico],
              "exe=%s ico=%s" % ([g["size"] for g in group], [e["size"] for e in ico]))
        check("RT_GROUP_ICON 每档都是 1 平面 32 位",
              all(g["planes"] == 1 and g["bpp"] == 32 for g in group))

        mismatched = []
        for g, e in zip(group, ico):
            blob = icon_by_id.get(g["id"])
            if blob is None:
                mismatched.append("%dpx 缺 RT_ICON #%d" % (e["w"], g["id"]))
            elif blob != e["payload"]:
                mismatched.append("%dpx 负载不同（exe %d 字节 / ico %d 字节）"
                                  % (e["w"], len(blob), len(e["payload"])))
        check("每个 RT_ICON 的字节都与 card.ico 对应档完全相同",
              not mismatched, "; ".join(mismatched[:3]))
        check("RT_ICON 资源个数与档数一致",
              len(icon_by_id) == len(ico), "exe 里 %d 个" % len(icon_by_id))

if manifest_bytes:
    txt = manifest_bytes.decode("utf-8", "replace")
    check("清单含 requestedExecutionLevel asInvoker", "asInvoker" in txt)
    check("清单含 PerMonitorV2 DPI 感知", "PerMonitorV2" in txt)
    check("清单含 XiaoJian.app 标识", "XiaoJian.app" in txt)

print("\n== 4. 程序集元数据 ==")
dn = dnfile.dnPE(path)
asm = dn.net.mdtables.Assembly
row = asm.rows[0]
ver = "%d.%d.%d.%d" % (row.MajorVersion, row.MinorVersion, row.BuildNumber, row.RevisionNumber)
name = row.Name
print("  程序集名: %s" % name)
print("  程序集版本: %s" % ver)
check("程序集名 = XiaoJian", str(name) == "XiaoJian", str(name))
check("程序集版本 = 0.9.12.0", ver == "0.9.12.0", ver)

refs = {}
for r in dn.net.mdtables.AssemblyRef.rows:
    refs[str(r.Name)] = "%d.%d.%d.%d" % (r.MajorVersion, r.MinorVersion, r.BuildNumber, r.RevisionNumber)
print("  引用的程序集 (%d 个):" % len(refs))
for k in sorted(refs):
    print("    - %-28s %s" % (k, refs[k]))

need = ["mscorlib", "System", "System.Core", "System.Drawing", "System.Windows.Forms"]
for n in need:
    check("引用 %s" % n, n in refs, refs.get(n, "缺失"))
for n, v in refs.items():
    if n != "mscorlib":
        check("  %s 版本为 4.0.0.0 (.NET Framework)" % n, v == "4.0.0.0", v)

print("\n== 5. 类型与方法（确认功能代码已编入）==")
typerefs = set(str(t.TypeName) for t in dn.net.mdtables.TypeRef.rows)
print("  TypeRef 数量: %d" % len(typerefs))
typedefs = set(str(t.TypeName) for t in dn.net.mdtables.TypeDef.rows)
print("  本程序集类型: %s" % ", ".join(sorted(typedefs)))
for expect in ["QuoteCardForm", "TransparentHitBoxForm", "ResizeSupport", "CardSizeLimits",
               "DataLocation", "Program"]:
    check("定义类型 %s" % expect, expect in typedefs)

# 方法数（粗略确认代码规模）
print("  TypeDef 数量: %d, MethodDef 数量: %d" % (
    len(dn.net.mdtables.TypeDef.rows), len(dn.net.mdtables.MethodDef.rows)))

print("\n================ 结果 ================")
if fails:
    print("校验未通过项: %s" % ", ".join(fails))
    sys.exit(1)
print("全部结构校验通过 ✔")
