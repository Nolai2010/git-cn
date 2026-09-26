#!/usr/bin/env python3
"""cn_patch: 把上游 git 源码改造成中文优先、少一点反人类设计的版本。

  python3 cn_patch.py --src <git源码目录> --probe       打印现有译文，确认 msgid 仍在
  python3 cn_patch.py --src <git源码目录> --check-only   只校验能否命中
  python3 cn_patch.py --src <git源码目录> --apply        真正落盘

新增的 advise() 文案一律写成单行 C 字面量，并在这里与 po 的 msgid 共用同一常量，
main() 会反向校验“C 里的那行字符串”确实存在于打完补丁的源码中——否则运行时不翻译。
"""
import argparse
import os
import re
import sys
from pathlib import Path

PLACEHOLDER = re.compile(r"%(?:\([a-zA-Z0-9_]+\)?)?[#\d.]*[slufdxe]|%%")

PROBE_KEYS = [
    "Changes to be committed", "not staged for commit", "Untracked files",
    "nothing to commit", "no changes added to commit", "would be overwritten",
    "has no upstream branch", "HEAD detached", "Your branch is ahead",
    "to discard changes in working directory", "did you mean",
    "Merging is not possible", "Already on", "Note: switching to",
    "You are in 'detached HEAD'",
]

# ---- 新增提示文案（C 字面量与 po msgid 同源） ----
MSG_RESET_MIXED = (
    "no mode given: this is a MIXED reset - commits are undone, staged changes "
    "become unstaged, file contents are NOT touched. Use --soft to keep them "
    "staged, or --hard to also throw away your edits"
)
MSG_CHECKOUT_DOT = (
    "checkout with a path restores file contents and discards uncommitted edits; "
    "since Git 2.23 the clearer command for that is 'git restore'. Consider "
    "'git stash push -u' first"
)

CN_RESET_MIXED = (
    "你没写模式，所以这次是 MIXED 重置：提交记录退回、暂存区清空、文件内容保持不变。\n"
    "  想让改动仍留在暂存区 -> git reset --soft HEAD~1\n"
    "  想连文件内容一起丢弃 -> git reset --hard HEAD~1（不可恢复，先 git stash push -u）"
)
CN_CHECKOUT_DOT = (
    "你这是在还原文件内容，不是切分支：未提交的改动会被丢掉且找不回来。\n"
    "  Git 2.23 起这类操作用 git restore <文件> 更清楚（切分支用 git switch）\n"
    "  想保住改动，先备份：git stash push -u"
)

C_PATCHES = [
    (
        "refs.c",
        """	if (!ret) {
#ifdef WITH_BREAKING_CHANGES
		ret = xstrdup("main");
#else
		ret = xstrdup("master");
#endif /* WITH_BREAKING_CHANGES */
		if (!quiet)
			advise_if_enabled(ADVICE_DEFAULT_BRANCH_NAME,
					  _(default_branch_name_advice), ret);
	}""",
        """	if (!ret) {
#ifdef WITH_BREAKING_CHANGES
		ret = xstrdup("main");
#else
		ret = xstrdup("main"); /* git-cn: 默认分支直接采用 main */
#endif /* WITH_BREAKING_CHANGES */
		if (!quiet && strcmp(ret, "main"))
			advise_if_enabled(ADVICE_DEFAULT_BRANCH_NAME,
					  _(default_branch_name_advice), ret);
	}""",
        "init.defaultBranch 未配置时直接建 main，并去掉 master→main 迁移提示",
        [],
    ),
    (
        "builtin/reset.c",
        """	if (reset_type == NONE)
		reset_type = MIXED; /* by default */""",
        """	if (reset_type == NONE) {
		reset_type = MIXED; /* by default */
		if (!quiet && !pathspec.nr)
			advise(_("%s""" % MSG_RESET_MIXED + """"));
	}""",
        "git reset 不带模式时，讲清它动了提交记录/暂存区/工作区哪三层",
        [MSG_RESET_MIXED],
    ),
    (
        "builtin/checkout.c",
        """	if (argc == 3 && !strcmp(argv[1], "-b")) {""",
        """	if ((argc == 2 && !strcmp(argv[1], ".")) ||
	    (argc == 3 && !strcmp(argv[1], "--") && !strcmp(argv[2], ".")))
		advise(_("%s""" % MSG_CHECKOUT_DOT + """"));
	if (argc == 3 && !strcmp(argv[1], "-b")) {""",
        "git checkout . 是丢弃全部未提交改动，先讲清它不是切分支",
        [MSG_CHECKOUT_DOT],
    ),
    (
        "t/t0001-init.sh",
        """	if test_have_prereq WITH_BREAKING_CHANGES
	then
		expect=main
	else
		expect=master
	fi &&""",
        """	# git-cn: 默认分支固定 main，不再随 WITH_BREAKING_CHANGES 变化
	expect=main &&""",
        "上游断言默认分支为 master，本 fork 有意改为 main，同步改掉断言",
        [],
    ),
    (
        "t/t0001-init.sh",
        """test_expect_success 'advice on unconfigured init.defaultBranch' '
	GIT_TEST_DEFAULT_INITIAL_BRANCH_NAME= git -c color.advice=always \\
		init unconfigured-default-branch-name 2>err &&
	test_decode_color <err >decoded &&
	test_grep "<YELLOW>hint: " decoded
'""",
        """test_expect_success 'no master->main advice under git-cn (default already main)' '
	GIT_TEST_DEFAULT_INITIAL_BRANCH_NAME= git -c color.advice=always \\
		init unconfigured-default-branch-name 2>err &&
	test_decode_color <err >decoded &&
	test_grep ! "<YELLOW>hint: " decoded
'""",
        "默认已是 main，迁移提示被去掉，断言改为“不该再出现 hint”",
        [],
    ),
    (
        "GIT-VERSION-GEN",
        "DEF_VER=v2.55.0",
        "DEF_VER=v2.55.0.cn1",
        "版本号标记 cn1，便于 git --version 与体检识别汉化版",
        [],
    ),
]

# (msgid 唯一前缀, 新 msgstr, 为什么改)
PO_EDITS = [
    ("Changes to be committed:",
     "已暂存：这些内容会进入下一次提交",
     "“要提交的变更”太抽象，直接说清“会进入提交”"),
    ("Changes not staged for commit:",
     "已修改但没 git add：这些内容不会进入下一次提交",
     "官方译名“尚未暂存以备提交的变更”读不懂，新手第一坑就在这行"),
    ("Untracked files",
     "git 还没管起来的文件",
     "把“未跟踪”说成人话"),
    ('  (use "git %s <file>..." to include in what will be committed)',
     '  （要纳入版本控制就 git %s <文件>...；只是本地产物就写进 .gitignore）',
     "顺手教 .gitignore 这条路"),
    ('  (use "git restore --staged <file>..." to unstage)',
     '  （把文件移出暂存区用 git restore --staged <文件>...，文件内容不会被删）',
     "unstage 这个词没人懂"),
    ('  (use "git add <file>..." to update what will be committed)',
     '  （要进入提交必须先 git add <文件>...）',
     "把“更新要提交的内容”说成动作"),
    ('  (use "git restore <file>..." to discard changes in working directory)',
     '  （想丢掉这些未提交改动用 git restore <文件>...，丢出去找不回来）',
     "明确告知不可逆"),
    ('  (use "git push" to publish your local commits)',
     '  （本地有远端还没有的提交，git push 发出去）\n',
     "publish 说得含糊"),
    ("no changes added to commit (use",
     "没有内容进入本次提交：先 git add 挑文件，或者用 git commit -a 连同已跟踪文件的改动一起提交\n",
     "把两个补救动作写全"),
    ("nothing to commit, working tree clean",
     "没有待提交的改动，工作区和最后一次提交一致（也可能你的改动被 .gitignore 忽略了）\n",
     "补一个新手最常见的真实原因"),
    ("Your local changes to the following files would be overwritten by merge:\n  %s",
     "这些文件有未提交改动，会被这次合并覆盖，所以 Git 停住了（这是保护，不是故障）：\n  %s\n"
     "先 git stash push -u 收起来，或提交掉，再重跑刚才的命令",
     "原文只说会被覆盖，不给出路"),
    ("Merging is not possible because you have unmerged files.",
     "还有冲突没解决完，所以不能合并。用 git status 找到标着 UU 的文件先处理。",
     "指出下一步动作"),
    ("The current branch %s has no upstream branch.",
     "分支 %s 还没有上游，Git 不知道该推到远端的哪个分支。\n"
     "第一次推送用下面这条，之后就能直接 git push：\n\n    git push --set-upstream %s %s\n%s",
     "保留 4 个 %s 原顺序，说明 -u 的长期效果"),
    ("(HEAD detached at %s)",
     "%s（游离 HEAD，不属于任何分支）",
     "分支装饰里说清后果"),
    ("You are not currently on a branch.\nTo push",
     "你现在不在任何分支上，所以不能直接 push。\n"
     "要把当前状态推上去，得显式给远端分支名：\n\n    git push %s HEAD:<远程分支名>\n",
     "1 个 %s，说明为什么被拒"),
    ("Note: switching to '%s'.",
     "注意：正在切到 '%s'。\n\n"
     "你现在处于“游离 HEAD”：不在任何分支上。此时的提交不属于任何分支，\n"
     "切走之后只能靠 reflog 找回，看起来就像代码丢了。\n\n"
     "想保住这些提交，随时可以给当前状态建分支（现在做或稍后做都行）：\n\n"
     "  git switch -c <新分支名>\n\n想回到刚才的分支：\n\n  git switch -\n\n"
     "不想再看这条提示：git config --global advice.detachedHead false\n\n",
     "官方直译的这段又长又绕，重写成人话并强调“提交会悬空”"),
]

NEW_PO = [
    (MSG_RESET_MIXED, CN_RESET_MIXED),
    (MSG_CHECKOUT_DOT, CN_CHECKOUT_DOT),
]


def norm(s):
    return s.replace("\r\n", "\n")


def ph(s):
    return sorted(PLACEHOLDER.findall(s))


def po_unescape(s):
    out, i = [], 0
    table = {"n": "\n", "t": "\t", '"': '"', "\\": "\\"}
    while i < len(s):
        if s[i] == "\\" and i + 1 < len(s):
            out.append(table.get(s[i + 1], s[i + 1]))
            i += 2
        else:
            out.append(s[i])
            i += 1
    return "".join(out)


def po_escape(s):
    return s.replace("\\", "\\\\").replace('"', '\\"').replace("\t", "\\t")


def po_render(prefix, s):
    esc = po_escape(s)
    if "\n" not in s and len(esc) <= 66:
        return [f'{prefix} "{esc}"']
    lines = []
    parts = s.split("\n")
    for i, p in enumerate(parts):
        tail = "\\n" if i < len(parts) - 1 else ""
        if p or tail:
            lines.append(f'"{po_escape(p)}{tail}"')
    return [f'{prefix} ""'] + lines


def parse_block(blk_lines):
    comments, fields, field, special = [], {}, None, False
    for ln in blk_lines:
        if ln.startswith("#"):
            comments.append(ln)
            continue
        m = re.match(r"^(msgid|msgstr|msgctxt|msgid_plural)(?:\[(\d+)\])?\s+(.*)$", ln)
        if m:
            base, idx, rest = m.group(1), m.group(2), m.group(3)
            special = special or base in ("msgctxt", "msgid_plural")
            field = base + (f"[{idx}]" if idx else "")
            if rest == '""':
                fields[field] = ""
            else:
                mm = re.match(r'^"(.*)"$', rest)
                fields[field] = po_unescape(mm.group(1)) if mm else ""
        elif ln.startswith('"') and field is not None:
            mm = re.match(r'^"(.*)"$', ln)
            if mm:
                fields[field] = fields.get(field, "") + po_unescape(mm.group(1))
    return comments, fields, special


def po_blocks(text):
    lines = text.split("\n")
    out, start = [], None
    for i, ln in enumerate(lines):
        if not ln.strip():
            if start is not None:
                out.append((start, i))
                start = None
        elif start is None:
            start = i
    if start is not None:
        out.append((start, len(lines)))
    return out


def find_msgid(text, want):
    lines = text.split("\n")
    for a, b in po_blocks(text):
        _, f, special = parse_block(lines[a:b])
        if special:
            continue
        if f.get("msgid") == want:
            return (a, b)
    return None


def find_by_prefix(text, prefix):
    lines = text.split("\n")
    exact, pre = [], []
    for a, b in po_blocks(text):
        _, f, special = parse_block(lines[a:b])
        if special or "msgid" not in f:
            continue
        if f["msgid"] == prefix:
            exact.append((a, b, f))
        elif f["msgid"].startswith(prefix):
            pre.append((a, b, f))
    return exact or pre


def cmd_probe(src):
    text = norm((src / "po" / "zh_CN.po").read_text(encoding="utf-8"))
    lines = text.split("\n")
    seen = set()
    for a, b in po_blocks(text):
        _, f, special = parse_block(lines[a:b])
        if special or "msgid" not in f:
            continue
        for key in PROBE_KEYS:
            if key in f["msgid"] and key not in seen:
                seen.add(key)
                print(f"\n[{key}]")
                print("  MSGID : " + repr(f["msgid"])[:260])
                print("  MSGSTR: " + repr(f.get("msgstr", ""))[:260])
            if key in f["msgid"]:
                break
    missing = [k for k in PROBE_KEYS if k not in seen]
    if missing:
        print("\n未命中: " + " | ".join(missing))
    return 0


def apply_c(src, check_only, notes, errs):
    patched = {}
    for rel, old, new, why, msgs in C_PATCHES:
        old, new = norm(old), norm(new)
        p = src / rel
        if not p.exists():
            errs.append(f"缺少文件 {rel}")
            continue
        text = norm(p.read_text(encoding="utf-8"))
        n = text.count(old)
        if n != 1:
            errs.append(f"{rel}: 片段命中 {n} 次（应 1 次），上游代码已变动")
            continue
        after = text.replace(old, new, 1)
        for m in msgs:
            if m not in after:
                errs.append(f"{rel}: 注入后的 C 字面量与 msgid 不一致，运行时不会翻译")
        if not check_only:
            p.write_text(after, encoding="utf-8", newline="\n")
        patched[rel] = after
        notes.append(f"[C] {rel}: {why}")
    return patched


def apply_po(src, patched, check_only, notes, errs):
    po = src / "po" / "zh_CN.po"
    text = norm(po.read_text(encoding="utf-8"))
    lines = text.split("\n")
    for prefix, new_str, why in PO_EDITS:
        new_str = norm(new_str)
        cands = find_by_prefix("\n".join(lines), norm(prefix))
        if not cands:
            errs.append(f"po: 没有 msgid 以此开头: {prefix[:55]}")
            continue
        if len(cands) > 1:
            errs.append(f"po: 前缀命中 {len(cands)} 条，需要更长前缀: {prefix[:40]}\n"
                        + "".join(f"      - {c[2]['msgid'][:80]!r}\n" for c in cands))
            continue
        a, b, f = cands[0]
        if ph(new_str) != ph(f["msgid"]):
            errs.append(f"po: 占位符不一致，拒绝改写 {prefix[:40]}\n"
                        f"      msgid={ph(f['msgid'])} 新译文={ph(new_str)}")
            continue
        if f["msgid"].endswith("\n") != new_str.endswith("\n"):
            errs.append(f"po: 换行结尾与 msgid 不一致，msgfmt 会报 fatal: {prefix[:40]}")
            continue
        idx = next(i for i, l in enumerate(lines[a:b], start=a) if l.startswith("msgstr"))
        lines[a:b] = lines[a:idx] + po_render("msgstr", new_str)
        notes.append(f"[po改写] {why}")
    body = "\n".join(lines)
    chunks = []
    for msgid, mstr in NEW_PO:
        msgid = norm(msgid)
        if find_msgid(body, msgid):
            continue
        if ph(msgid) != ph(norm(mstr)):
            errs.append(f"po: 新条目占位符不一致: {msgid[:50]}")
            continue
        chunks.append("\n".join(po_render("msgid", msgid) + po_render("msgstr", norm(mstr))))
    if chunks:
        body = body.rstrip("\n") + "\n\n# git-cn 新增译文\n\n" + "\n\n".join(chunks) + "\n"
        notes.append(f"[po新增] {len(chunks)} 条对应 C 补丁的新译文")
    if not check_only:
        po.write_text(body, encoding="utf-8", newline="\n")
    return 0


def cmd_dump(src, keys):
    text = norm((src / "po" / "zh_CN.po").read_text(encoding="utf-8"))
    lines = text.split("\n")
    n = 0
    for a, b in po_blocks(text):
        _, f, special = parse_block(lines[a:b])
        if special or "msgid" not in f:
            continue
        if any(k in f["msgid"] for k in keys):
            n += 1
            print(f"\n===== 命中 {n}  msgctxt={'有' if special else '无'}")
            print("MSGID::" + repr(f["msgid"]))
            print("MSGSTR::" + repr(f.get("msgstr", "")))
    if not n:
        print("(无命中)")
    return 0


def validate_po(src, errs):
    import subprocess
    po = src / "po" / "zh_CN.po"
    try:
        p = subprocess.run(["msgfmt", "-c", "-o", os.devnull, str(po)],
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
    except FileNotFoundError:
        print("  (本机没有 msgfmt，跳过 .po 校验)")
        return
    if p.returncode:
        errs.append("msgfmt -c 校验失败:\n" + (p.stderr or p.stdout or "")[:1500])
    else:
        print("  msgfmt -c 校验通过（含新增/改写的译文）")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", required=True)
    ap.add_argument("--probe", action="store_true")
    ap.add_argument("--dump", action="store_true")
    ap.add_argument("--keys", default="")
    ap.add_argument("--apply", action="store_true")
    ap.add_argument("--check-only", action="store_true")
    a = ap.parse_args()
    src = Path(a.src).expanduser().resolve()
    if not (src / "GIT-VERSION-GEN").exists():
        print(f"!! {src} 不像 git 源码目录", file=sys.stderr)
        return 2
    if a.probe:
        return cmd_probe(src)
    if a.dump:
        return cmd_dump(src, [k for k in a.keys.split(",") if k])
    if not (a.apply or a.check_only):
        ap.print_help()
        return 2
    notes, errs = [], []
    patched = apply_c(src, a.check_only, notes, errs)
    apply_po(src, patched, a.check_only, notes, errs)
    if a.apply and not a.check_only and not errs:
        validate_po(src, errs)
    for n in notes:
        print("  " + n)
    if errs:
        print("\n!! 未完全应用:")
        for e in errs:
            print("   " + e)
        return 1
    print(f"\nOK: C 补丁 {len(C_PATCHES)} / 译文改写 {len(PO_EDITS)} / 新译文 {len(NEW_PO)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
