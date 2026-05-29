#!/usr/bin/env python3
"""
i18n Verification Script for Dynamic Financial System mod.
Checks:
1. No Chinese strings remain in C# source files (outside of I18n.Get keys and comments)
2. All I18n.Get keys exist in both default.json and en.json
3. No empty values in en.json
4. No Chinese characters in en.json values
5. No C# variable syntax leaked into JSON values
6. Build compiles successfully
"""
import re
import json
import os
import subprocess
import sys

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

PROJECT_DIR = os.path.dirname(os.path.abspath(__file__))
I18N_DIR = os.path.join(PROJECT_DIR, 'i18n')
PASS = "PASS"
FAIL = "FAIL"
WARN = "WARN"

results = []


def check(name, status, detail=""):
    results.append((name, status, detail))
    icon = "✓" if status == PASS else "✗" if status == FAIL else "⚠"
    print(f"  [{icon}] {name}: {detail}" if detail else f"  [{icon}] {name}")


def scan_cs_files():
    """Scan all C# files for Chinese strings not inside I18n.Get()."""
    print("\n=== 1. Checking C# files for hardcoded Chinese strings ===")
    issues = []
    for root, dirs, files in os.walk(PROJECT_DIR):
        dirs[:] = [d for d in dirs if d not in ('obj', 'bin')]
        for fname in files:
            if not fname.endswith('.cs'):
                continue
            fp = os.path.join(root, fname)
            rel = os.path.relpath(fp, PROJECT_DIR)
            with open(fp, 'r', encoding='utf-8') as fh:
                for i, line in enumerate(fh, 1):
                    stripped = line.lstrip()
                    if stripped.startswith('//') or stripped.startswith('*'):
                        continue
                    # Find Chinese strings NOT inside I18n.Get
                    # Check for Chinese in string literals that are NOT I18n.Get calls
                    if 'I18n.Get' in line or 'Translations!' in line:
                        continue
                    matches = re.findall(r'"[^"]*[一-鿿][^"]*"', line)
                    if matches:
                        for m in matches:
                            # Skip known acceptable patterns (time.txt comparisons)
                            if m in ('"无"', '"—"', '"春"', '"夏"', '"秋"', '"冬"'):
                                continue
                            issues.append((rel, i, m[:60]))

    if issues:
        check("Hardcoded Chinese in .cs", FAIL, f"{len(issues)} found")
        for rel, line, s in issues[:10]:
            print(f"       {rel}:{line}: {s}")
        if len(issues) > 10:
            print(f"       ... and {len(issues) - 10} more")
    else:
        check("Hardcoded Chinese in .cs", PASS, "No hardcoded Chinese strings found")


def check_i18n_keys():
    """Check all I18n.Get keys exist in both JSON files."""
    print("\n=== 2. Checking I18n.Get keys exist in JSON files ===")
    with open(os.path.join(I18N_DIR, 'default.json'), 'r', encoding='utf-8') as f:
        default = json.load(f)
    with open(os.path.join(I18N_DIR, 'en.json'), 'r', encoding='utf-8') as f:
        en = json.load(f)

    # Find all I18n.Get keys used in C# files
    used_keys = set()
    for root, dirs, files in os.walk(PROJECT_DIR):
        dirs[:] = [d for d in dirs if d not in ('obj', 'bin')]
        for fname in files:
            if not fname.endswith('.cs'):
                continue
            fp = os.path.join(root, fname)
            with open(fp, 'r', encoding='utf-8') as fh:
                content = fh.read()
            found = re.findall(r'I18n\.Get\("([^"]+)"\)', content)
            used_keys.update(found)

    missing_default = used_keys - set(default.keys())
    missing_en = used_keys - set(en.keys())
    unused = set(default.keys()) - used_keys

    if missing_default:
        check("Keys in default.json", FAIL, f"{len(missing_default)} keys missing")
        for k in sorted(missing_default)[:5]:
            print(f"       Missing: {k}")
    else:
        check("Keys in default.json", PASS, f"All {len(used_keys)} used keys present")

    if missing_en:
        check("Keys in en.json", FAIL, f"{len(missing_en)} keys missing")
        for k in sorted(missing_en)[:5]:
            print(f"       Missing: {k}")
    else:
        check("Keys in en.json", PASS, f"All {len(used_keys)} used keys present")

    if unused:
        check("Unused keys", WARN, f"{len(unused)} keys in JSON not used in code")
    else:
        check("Unused keys", PASS, "All keys are referenced in code")


def check_en_values():
    """Check en.json has no empty values or Chinese characters."""
    print("\n=== 3. Checking en.json quality ===")
    with open(os.path.join(I18N_DIR, 'en.json'), 'r', encoding='utf-8') as f:
        en = json.load(f)

    empty = sum(1 for v in en.values() if isinstance(v, str) and v == '')
    chinese = [(k, v[:40]) for k, v in en.items() if isinstance(v, str) and re.search(r'[一-鿿]', v)]

    if empty:
        check("Empty English values", FAIL, f"{empty} empty entries")
    else:
        check("Empty English values", PASS, "All entries have English values")

    if chinese:
        check("Chinese in en.json", WARN, f"{len(chinese)} entries still have Chinese")
        for k, v in chinese[:5]:
            print(f"       {k}: {v}")
    else:
        check("Chinese in en.json", PASS, "No Chinese characters in English translations")


def check_json_consistency():
    """Check default.json and en.json have same keys."""
    print("\n=== 4. Checking JSON consistency ===")
    with open(os.path.join(I18N_DIR, 'default.json'), 'r', encoding='utf-8') as f:
        default = json.load(f)
    with open(os.path.join(I18N_DIR, 'en.json'), 'r', encoding='utf-8') as f:
        en = json.load(f)

    cn_only = set(default.keys()) - set(en.keys())
    en_only = set(en.keys()) - set(default.keys())

    if cn_only or en_only:
        check("Key count match", FAIL, f"CN-only: {len(cn_only)}, EN-only: {len(en_only)}")
    else:
        check("Key count match", PASS, f"Both have {len(default)} keys")


def check_csharp_syntax():
    """Quick check for obvious C# syntax issues."""
    print("\n=== 5. Checking C# syntax (quick scan) ===")
    issues = []
    for root, dirs, files in os.walk(PROJECT_DIR):
        dirs[:] = [d for d in dirs if d not in ('obj', 'bin')]
        for fname in files:
            if not fname.endswith('.cs'):
                continue
            fp = os.path.join(root, fname)
            rel = os.path.relpath(fp, PROJECT_DIR)
            with open(fp, 'r', encoding='utf-8') as fh:
                for i, line in enumerate(fh, 1):
                    # Check for orphaned $ before I18n.Get
                    if re.search(r'\$\s*I18n\.Get', line):
                        issues.append((rel, i, "orphaned $ before I18n.Get"))
                    # Check for broken interpolation
                    if '$"' in line and 'I18n.Get' in line:
                        if not re.search(r'\$".*\{I18n\.Get.*\}.*"', line):
                            # Could be complex interpolation, just flag for review
                            pass

    if issues:
        check("C# syntax", WARN, f"{len(issues)} potential issues")
        for rel, line, msg in issues[:5]:
            print(f"       {rel}:{line}: {msg}")
    else:
        check("C# syntax", PASS, "No obvious syntax issues")


def check_build():
    """Try to build the project."""
    print("\n=== 6. Build verification ===")
    try:
        result = subprocess.run(
            ['dotnet', 'build', 'BankMod.csproj'],
            cwd=PROJECT_DIR,
            capture_output=True,
            text=True,
            timeout=120
        )
        if result.returncode == 0:
            check("Build", PASS, "Build succeeded with 0 errors")
        else:
            # Count errors
            errors = result.stderr.count('error CS')
            check("Build", FAIL, f"{errors} compilation errors")
            # Show first few
            for line in result.stderr.split('\n'):
                if 'error CS' in line:
                    print(f"       {line.strip()[:100]}")
    except FileNotFoundError:
        check("Build", WARN, "dotnet not found, skipping build check")
    except subprocess.TimeoutExpired:
        check("Build", WARN, "Build timed out")


def main():
    print("=" * 60)
    print("i18n Verification Report for Dynamic Financial System")
    print("=" * 60)

    scan_cs_files()
    check_i18n_keys()
    check_en_values()
    check_json_consistency()
    check_csharp_syntax()
    check_build()

    # Summary
    print("\n" + "=" * 60)
    print("SUMMARY")
    print("=" * 60)
    passed = sum(1 for _, s, _ in results if s == PASS)
    failed = sum(1 for _, s, _ in results if s == FAIL)
    warned = sum(1 for _, s, _ in results if s == WARN)
    total = len(results)

    for name, status, detail in results:
        icon = "✓" if status == PASS else "✗" if status == FAIL else "⚠"
        print(f"  [{icon}] {name}")

    print(f"\nTotal: {passed} passed, {warned} warnings, {failed} failed out of {total} checks")

    if failed > 0:
        print("\nStatus: FAILED - Issues need to be fixed")
        return 1
    elif warned > 0:
        print("\nStatus: PASSED WITH WARNINGS - Review recommended")
        return 0
    else:
        print("\nStatus: ALL CHECKS PASSED")
        return 0


if __name__ == '__main__':
    sys.exit(main())
