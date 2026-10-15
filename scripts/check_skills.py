#!/usr/bin/env python3
"""Validate agent skills under skills/ (stdlib only).

Checks: frontmatter (name regex, reserved words, directory match, description
length and word count), SKILL.md body line count, references one level deep,
and broken relative markdown links in every .md file under skills/.
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SKILLS = os.path.join(ROOT, "skills")
NAME_RE = re.compile(r"^[a-z0-9]+(-[a-z0-9]+)*$")
RESERVED = ("claude", "anthropic")
MAX_BODY = 200
LINK_RE = re.compile(r"\[[^\]]*\]\(([^)\s]+)\)")


def parse_frontmatter(text):
    if not text.startswith("---\n"):
        return None, text
    end = text.find("\n---", 4)
    if end < 0:
        return None, text
    fm = {}
    for line in text[4:end].splitlines():
        m = re.match(r"^([A-Za-z_-]+):\s*(.*)$", line)
        if m:
            fm[m.group(1)] = m.group(2).strip().strip('"').strip("'")
    body = text[end + 4:].lstrip("\n")
    return fm, body


def main():
    errors = []
    if not os.path.isdir(SKILLS):
        print("no skills/ directory")
        return 1
    names = sorted(d for d in os.listdir(SKILLS) if os.path.isdir(os.path.join(SKILLS, d)))
    for d in names:
        sdir = os.path.join(SKILLS, d)
        path = os.path.join(sdir, "SKILL.md")
        if not os.path.isfile(path):
            errors.append("%s: missing SKILL.md" % d)
            continue
        with open(path) as f:
            text = f.read()
        fm, body = parse_frontmatter(text)
        if fm is None:
            errors.append("%s: missing frontmatter" % d)
            continue
        name = fm.get("name", "")
        desc = fm.get("description", "")
        if not NAME_RE.match(name) or len(name) > 64:
            errors.append("%s: bad name %r" % (d, name))
        if name != d:
            errors.append("%s: name %r does not match directory" % (d, name))
        for w in RESERVED:
            if w in name:
                errors.append("%s: name contains reserved word %r" % (d, w))
        if not desc:
            errors.append("%s: empty description" % d)
        if len(desc) > 1024:
            errors.append("%s: description %d chars > 1024" % (d, len(desc)))
        words = len(desc.split())
        if words < 30 or words > 60:
            errors.append("%s: description has %d words (want 30-60)" % (d, words))
        nlines = len(body.splitlines())
        if nlines > MAX_BODY:
            errors.append("%s: body %d lines > %d" % (d, nlines, MAX_BODY))
        print("%-28s name ok, desc %d words/%d chars, body %d lines" % (d, words, len(desc), nlines))
        for dirpath, dirs, files in os.walk(sdir):
            rel = os.path.relpath(dirpath, sdir)
            if rel not in (".", "references"):
                errors.append("%s: unexpected nested directory %s" % (d, rel))
        for dirpath, dirs, files in os.walk(sdir):
            for fn in files:
                if not fn.endswith(".md"):
                    continue
                fp = os.path.join(dirpath, fn)
                with open(fp) as f:
                    content = f.read()
                for target in LINK_RE.findall(content):
                    if re.match(r"^[a-z]+:", target) or target.startswith("#"):
                        continue
                    t = target.split("#")[0]
                    if t and not os.path.exists(os.path.normpath(os.path.join(dirpath, t))):
                        errors.append("%s: broken link %s" % (os.path.relpath(fp, ROOT), target))
    if errors:
        print("\nFAILED:")
        for e in errors:
            print("  - " + e)
        return 1
    print("\nOK: %d skills" % len(names))
    return 0


if __name__ == "__main__":
    sys.exit(main())
