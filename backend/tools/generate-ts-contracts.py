#!/usr/bin/env python3
# =============================================================================
# 文件：backend/tools/generate-ts-contracts.py
# 用途：从后端 sealed record DTO 生成前端 TypeScript 契约类型。
#
# 为什么存在（2026-10-18 契约审计的落地）：
#   审计前，前端 types/music.ts 里手写了 34 个接口去镜像 54 个后端 DTO。
#   手写镜像没有校验机制——后端加字段前端不知道，删字段前端也照跑不报错。
#   现在改成「从单一来源生成」：后端 Contracts/ 是唯一真实契约，
#   本脚本把它编译成 TS，漂移会以「重新生成后 git diff 非空」的形式暴露出来。
#
# 用法：
#   python backend/tools/generate-ts-contracts.py          # 生成
#   python backend/tools/generate-ts-contracts.py --check  # 只校验是否漂移（CI 用）
#
# 跳过的目录：Migrations / obj / bin（里面没有对外契约，且大量噪音）。
# 仅处理 `public sealed record`；`private sealed record`（如 PendingUpload）是内部类型，不导出。
# =============================================================================
from __future__ import annotations

import argparse
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
PROJECT = ROOT / "backend" / "ShineMusic.Api"
OUTPUT = ROOT / "ShineMusicApp" / "src" / "types" / "api.generated.ts"

SKIP_DIRS = {"obj", "bin", "Migrations"}

# 只从这几个来源收集对外 DTO：
#   Contracts/     —— 全部对外契约（权威来源）
#   Endpoints/     —— 少数就近定义的请求/响应体
#   TokenService.cs —— AuthTokenResponse 定义在此
# Services/ 下的其它 record 是内部实现细节（如 ItunesMetadataResult、DailyRecommendationResult），
# 不该出现在前端契约里。
SCAN_DIRS = {"Contracts", "Endpoints", "Services"}
SCAN_FILES = {"TokenService.cs"}

# C# 基元类型 → TS
PRIMITIVES = {
    "string": "string",
    "bool": "boolean",
    "int": "number",
    "long": "number",
    "double": "number",
    "float": "number",
    "decimal": "number",
    "Guid": "string",        # 后端 Guid 序列化为字符串
    "DateTime": "string",   # ISO 8601 字符串
    "DateTimeOffset": "string",
    "DateOnly": "string",      # yyyy-MM-dd
    "TimeSpan": "string",      # hh:mm:ss
    "Uri": "string",
    "object": "unknown",
}

HEADER = r'''/**
 * ============================================================================
 * 【自动生成，请勿手工修改】
 *
 * 本文件由 `python backend/tools/generate-ts-contracts.py` 从后端
 * `backend/ShineMusic.Api/**\/*.cs` 中的 `public sealed record` DTO 生成。
 *
 * 修改契约的流程：
 *   1. 改后端 Contracts/ 下的 record；
 *   2. 跑 `yarn gen:contracts` 重新生成本文件；
 *   3. `git diff` 里同步出现的前端变化就是「本次契约变更的影响面」。
 *
 * 若后端的改动没有反映到这里，说明命令没跑；直接手改本文件会在下次生成时被覆盖。
 *
 * 命名规则：C# `XxxDto` / `XxxRequest` / `XxxResponse` → TS `ApiXxx`。
 * 与历史手写名不一致的类型，在 `src/types/music.ts` 中以 export alias 收口，
 * 页面只需 import 老名字，迁移期不需要改业务代码。
 * ============================================================================
 */

'''


def iter_cs_files():
    for p in sorted(PROJECT.rglob("*.cs")):
        rel = p.relative_to(PROJECT)
        if any(part in SKIP_DIRS for part in rel.parts):
            continue
        if rel.parts[0] not in SCAN_DIRS:
            continue
        # Services 下只取白名单文件，其余是实现细节
        if rel.parts[0] == "Services" and rel.name not in SCAN_FILES:
            continue
        yield p


def find_records(text: str):
    """产出 (名称, 泛型参数, 参数列表文本, 前置 XML 文档)。"""
    # record 声明行 → 参数括号结束位置（允许跨行、嵌套泛型）
    for m in re.finditer(r"public\s+sealed\s+record\s+(?P<name>\w+)", text):
        name = m.group("name")
        start = m.end()
        generic = ""
        gm = re.match(r"\s*<(?P<g>[^>]+)>", text[start:])
        if gm:
            generic = gm.group("g").strip()
            start += gm.end()
        # 找形参列表的左括号
        paren = text.find("(", start)
        if paren < 0:
            continue
        depth, i = 0, paren
        while i < len(text):
            c = text[i]
            if c == "(":
                depth += 1
            elif c == ")":
                depth -= 1
                if depth == 0:
                    break
            i += 1
        params_text = text[paren + 1:i]
        # 向前取最近的 /// <summary> 文档块
        doc_start = text.rfind("\n", 0, m.start())
        doc = text[doc_start + 1:m.start()]
        while doc and (not doc.strip().startswith("///")):
            j = text.rfind("\n", 0, doc_start)
            if j < 0:
                doc = ""
                break
            doc_start = j
            doc = text[j + 1:m.start()]
        yield name, generic, params_text, doc


def split_params(params_text: str):
    """按顶层逗号切分位置参数，忽略泛型与数组里的逗号。"""
    out, depth, buf = [], 0, []
    for c in params_text:
        if c in "<([":
            depth += 1
        elif c in ">)]":
            depth -= 1
        if c == "," and depth == 0:
            out.append("".join(buf))
            buf = []
        else:
            buf.append(c)
    if "".join(buf).strip():
        out.append("".join(buf))
    return out


def parse_param(chunk: str):
    """'IReadOnlyList<string>? Artists' → ('IReadOnlyList<string>?', 'Artists')"""
    chunk = chunk.strip()
    # 去掉尾部的行注释
    chunk = re.split(r"\s//", chunk)[0].strip()
    # 去掉默认值（`= null` / `= "x"` / `= []`），只留「类型 + 参数名」
    chunk = re.sub(r"=\s*[^,]+$", "", chunk).strip()
    if not chunk:
        return None
    parts = chunk.rsplit(None, 1)
    if len(parts) != 2:
        # 无参数名（record 里不会出现）或写法特殊，跳过
        return None
    csharp_type, name = parts
    return csharp_type, name


def to_ts_type(csharp: str, alias: dict, generic_param: str | None = None) -> str:
    """C# 类型表达式 → TS 类型表达式。支持嵌套泛型，如 IReadOnlyList<PageResult<TrackDto>>。"""
    csharp = csharp.strip()
    negate = csharp.endswith("?")
    if negate:
        csharp = csharp[:-1].strip()

    # 泛型声明里的 T（如 PageResult<T>）原样透传，不参与 DTO 名映射
    if generic_param and csharp == generic_param:
        return f"{csharp} | null" if negate else csharp

    # 集合：IReadOnlyList<T> / IList<T> / List<T> / IEnumerable<T> / T[]
    list_m = re.match(r"^(?:IReadOnlyList|IList|List|ICollection|IEnumerable)<(.+)>$", csharp)
    arr_m = re.match(r"^(.+)\[\]$", csharp)
    if list_m or arr_m:
        inner = to_ts_type((list_m or arr_m).group(1).strip(), alias, generic_param)
        base = f"readonly {inner}[]"
        return f"{base} | null" if negate else base

    # 自定义泛型：Name<Arg1, Arg2>
    gen_m = re.match(r"^(\w+)<(.+)>$", csharp)
    if gen_m:
        args = ", ".join(
            to_ts_type(a.strip(), alias, generic_param) for a in split_params(gen_m.group(2))
        )
        base = f"{ts_name(gen_m.group(1))}<{args}>"
        return f"{base} | null" if negate else base

    # 基元与命名类型
    if csharp in PRIMITIVES:
        base = PRIMITIVES[csharp]
        return f"{base} | null" if negate else base

    base = alias.get(csharp, ts_name(csharp))
    return f"{base} | null" if negate else base


def ts_name(csharp: str) -> str:
    # 已是 Api 前缀的不再加；PageResult 对应前端的通用分页包装
    if csharp.startswith("Api"):
        return csharp
    if csharp == "PageResult":
        return "ApiPageResult"
    return "Api" + re.sub(r"(Dto|Request|Response)$", "", csharp)


def doc_params(doc: str):
    """提取 <param name="X">说明</param>"""
    out = {}
    for m in re.finditer(r'<param name="([^"]+)">(.*?)</param>', doc, re.S):
        desc = re.sub(r"\s+", " ", m.group(2)).strip()
        out[m.group(1)] = desc
    return out


def doc_summary(doc: str):
    m = re.search(r"<summary>(.*?)</summary>", doc, re.S)
    if not m:
        return None
    return re.sub(r"\s+", " ", m.group(1)).strip()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="只检查是否与现有文件一致（漂移检测）")
    args = ap.parse_args()

    records = {}
    for path in iter_cs_files():
        text = path.read_text(encoding="utf-8", errors="replace")
        for name, generic, params_text, doc in find_records(text):
            params = [p for p in (parse_param(c) for c in split_params(params_text)) if p]
            records[name] = {
                "generic": generic,
                "params": params,
                "summary": doc_summary(doc),
                "docs": doc_params(doc),
                "file": str(path.relative_to(ROOT)).replace("\\", "/"),
            }

    # 第一遍：收集全部类型名，供 alias 推断
    alias = {n: ts_name(n) for n in records}

    lines = [HEADER]
    unknown_refs = set()

    for name in sorted(records):
        info = records[name]
        generic = info["generic"]
        ts = ts_name(name)
        gparam = ""
        if generic:
            only = [g.strip() for g in generic.split(",")][0]
            gparam = only if len(only) == 1 else f"<{only}>"

        if info["summary"]:
            lines.append(f"/** {info['summary']} */")
        for src in (info["file"],):
            lines.append(f"/** 来源：`{src}` */")

        gname = None
        if generic:
            gname = [g.strip() for g in generic.split(",")][0]

        props: list[str] = []
        for csharp_type, prop in info["params"]:
            ts_t = to_ts_type(csharp_type, alias, gname)
            # 记录找不到的类型引用（可能是 IResult/DbContext 等误捕，忽略即可）
            for ref in re.findall(r"\b[A-Z]\w+\b", csharp_type):
                if ref not in PRIMITIVES and ref not in alias and ref not in ("IReadOnlyList", "IList", "List", "ICollection", "IEnumerable"):
                    unknown_refs.add(f"{name}.{prop}: {csharp_type}")
            doc = info["docs"].get(prop)
            if doc:
                props.append(f"  /** {doc} */")
            props.append(f"  {prop[:1].lower() + prop[1:]}{'?' if ts_t.endswith('| null') else ''}: {ts_t}")

        if gparam:
            lines.append(f"export interface {ts}<{gparam}> {{")
        else:
            lines.append(f"export interface {ts} {{")
        lines.extend(props if props else ["  [key: string]: never"])
        lines.append("}")
        lines.append("")

    body = "\n".join(lines).rstrip() + "\n"

    if unknown_refs:
        preview = ", ".join(sorted(unknown_refs)[:5])
        print(f"提示：{len(unknown_refs)} 个字段引用了未识别的 C# 类型（已按接口名映射），例：{preview}")

    if args.check:
        existing = OUTPUT.read_text(encoding="utf-8") if OUTPUT.exists() else ""
        if existing != body:
            print(f"漂移：{OUTPUT} 与当前后端契约不一致，请运行 `python backend/tools/generate-ts-contracts.py`")
            return 1
        print("契约类型与后端一致。")
        return 0

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(body, encoding="utf-8", newline="\n")
    print(f"已生成 {OUTPUT.relative_to(ROOT)}（{len(records)} 个契约类型）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
