#!/usr/bin/env python3
"""Fails when a Dockerfile's FROM names a base image without a digest (BCHAT-018).

    python infra/ci/check_dockerfile_pins.py             # every Dockerfile of the repository
    python infra/ci/check_dockerfile_pins.py --self-test

A tag moves; a digest does not, so a compromised or silently rebuilt base image cannot change what the next build contains. Dependabot
(`docker` entries in .github/dependabot.yml) proposes the new digest as a pull request. `FROM scratch` and `FROM <earlier stage>` are fine.
"""
import os
import re
import subprocess
import sys

try:
    import yaml
except ImportError:
    sys.exit("PyYAML is required: pip install pyyaml")

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
FROM = re.compile(r"^\s*FROM\s+(?:--platform=\S+\s+)?(\S+)(?:\s+AS\s+(\S+))?", re.IGNORECASE)
DIGEST = re.compile(r"@sha256:[0-9a-f]{64}$")


ARG = re.compile(r"^\s*ARG\s+(\w+)=(\S+)")


def check_text(text, name):
    errors, stages, args = [], set(), {}
    for n, line in enumerate(text.splitlines(), 1):
        a = ARG.match(line)
        if a:
            args[a.group(1)] = a.group(2).strip("\"'")
        m = FROM.match(line)
        if not m:
            continue
        image, alias = m.group(1), m.group(2)
        # FROM ${BASE} / $BASE: judge the default of the ARG it names (a build-arg can still override it, which is then a deliberate act).
        image = re.sub(r"\$\{(\w+):?[-+]?([^}]*)\}|\$(\w+)", lambda v: args.get(v.group(1) or v.group(3)) or v.group(2) or v.group(0), image)
        if image.lower() != "scratch" and image.lower() not in stages and not DIGEST.search(image):
            errors.append(f"{name}:{n}: base image '{image}' is not pinned by digest (@sha256:...)")
        if alias:
            stages.add(alias.lower())
    # COPY --from=<image> / RUN --mount=from=<image> pull an image too; an earlier stage's alias is fine.
    for n, line in enumerate(text.splitlines(), 1):
        for m in re.finditer(r"(?:--from=|--mount=[^ ]*\bfrom=)(\S+)", line):
            ref = m.group(1)
            if ref.lower() not in stages and not ref.isdigit() and not DIGEST.search(ref) and re.search(r"[/:.]", ref):
                errors.append(f"{name}:{n}: external image '{ref}' is not pinned by digest (@sha256:...)")
    return errors


def dockerfiles():
    out = subprocess.run(["git", "ls-files"], cwd=ROOT, capture_output=True, text=True, check=True).stdout.split("\n")
    return [f for f in out if re.search(r"(^|/)(Dockerfile|Containerfile)(\.[\w-]+)?$|\.(Dockerfile|Containerfile)$", f) and not f.endswith(".dockerignore")]


def main():
    if "--self-test" in sys.argv:
        good = "FROM node:22@sha256:" + "a" * 64 + " AS build\nFROM build AS test\nFROM scratch\nFROM --platform=linux/amd64 x@sha256:" + "b" * 64 + "\n"
        assert not check_text(good, "good"), "a pinned Dockerfile must pass"
        assert not check_text("ARG B=node:22@sha256:" + "a" * 64 + "\nFROM ${B} AS x\n", "arg"), "a pinned ARG default must pass"
        assert check_text("ARG B=node:22\nFROM $B\n", "arg"), "an unpinned ARG default must fail"
        assert not check_text("FROM ${B:-node:22@sha256:" + "a" * 64 + "}\n", "default"), "a pinned default must pass"
        assert check_text("FROM ${B:-node:22}\n", "default"), "an unpinned default must fail"
        assert check_text("FROM a@sha256:" + "a" * 64 + "\nCOPY --from=nginx:1 /x /y\n", "copy"), "an unpinned COPY --from image must fail"
        assert not check_text("FROM a@sha256:" + "a" * 64 + " AS b\nCOPY --from=b /x /y\nCOPY --from=0 /x /y\n", "copy"), "stage references are fine"
        for bad in ("FROM node:22-alpine\n", "FROM node:22 AS build\n", "FROM x@sha256:abc\n", "FROM x:1 as b\nFROM b\nFROM y:2\n"):
            assert check_text(bad, "bad"), f"not caught: {bad!r}"
        print("self-test passed")
        return 0
    files = dockerfiles()
    if not files:
        print("ERROR: no Dockerfile found")
        return 1
    errors = []
    for f in files:
        with open(os.path.join(ROOT, f), encoding="utf-8") as fh:
            errors += check_text(fh.read(), f)
    # Dependabot must watch the directory of every Dockerfile, or its pinned digests go stale.
    with open(os.path.join(ROOT, ".github", "dependabot.yml"), encoding="utf-8") as fh:
        config = yaml.safe_load(fh)
    watched = set()
    for u in config.get("updates", []):
        if u.get("package-ecosystem") == "docker":
            watched |= set(u.get("directories", [])) | ({u["directory"]} if "directory" in u else set())
    for f in files:
        d = "/" + os.path.dirname(f)
        if d not in watched:
            errors.append(f"{f}: .github/dependabot.yml has no docker entry for {d}")
    for e in errors:
        print("ERROR:", e)
    if not errors:
        print(f"{len(files)} Dockerfiles, every FROM pinned by digest")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
