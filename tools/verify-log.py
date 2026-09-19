#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""verify-log.py —— 从 DamageAdvisor.log 里比对「面板值 vs 游戏卡面值」

做的是 A 类校验：把日志里 mod 算出来的数字（dmg/block/poison/draw/shivs）
与同一行 `vars=[...]` 里游戏自己的卡面值对照，找出对不上的地方。

⚠️ 已知盲区（见 STATUS.md 与 card-semantics-change skill）：
   数字只写在卡牌描述里、变量里没有的那类错误，纯文本比对**抓不到**。
   例：匕首雨"造成 4 点伤害两次"没有 Repeat 变量——修之前 dmg=4 与 Damage=4/4
   完全一致、修之后 dmg=8 是 4×2，两种状态脚本都可能判"通过"。

用法：
    python tools/verify-log.py                  # 用默认日志路径
    python tools/verify-log.py --log <路径>      # 指定日志
    python tools/verify-log.py --report <路径>   # 指定报告输出
退出码：0 = 无不一致；1 = 有不一致（可用于拦住发布/提交）
"""

from __future__ import annotations

import argparse
import math
import os
import re
import sys
from dataclasses import dataclass, field

# ---------------------------------------------------------------------------
# 例外表：这几张牌的某个字段与同名字段对不上，是**故意的**，逐个写清楚原因。
# 只放"故意转换/跨字段"的牌；能靠通用规则解释的不要往这里加。
# ---------------------------------------------------------------------------
FIXED_DRAW = {"投掷匕首": 1, "逃脱计划": 1}          # 描述里写死"抽 N 张"，没有 Cards 变量
HARDCODED_HITS = {"匕首雨": 2}                      # 描述里写死"造成 N 点伤害两次"，没有 Repeat 变量
CARDS_MEANS_SHIVS = {"刀刃之舞", "斗篷与匕首", "袖里乾坤", "墨之刃"}   # Cards= 是"生成小刀数"而不是抽牌数
CARDS_MEANS_DISCARD = {"隐秘匕首"}                  # Cards= 是弃牌数（日志不记弃牌，该字段跳过校验）

SNAPSHOT_RE = re.compile(
    r"手牌快照#(?P<n>\d+)\s+turn=(?P<turn>\d+)\s+energy=(?P<energy>-?\d+)\s+hand=(?P<hand>\d+)\s+"
    r"draw=(?P<drawpile>\d+)\s+enemies=(?P<enemies>\d+)\s+hp=(?P<hp>-?\d+)\s+block=(?P<block>-?\d+)\s+"
    r"incoming=(?P<incoming>-?\d+)\s+nodes=(?P<nodes>\d+)"
)

CARD_RE = re.compile(
    r"牌 (?P<name>.+?) cost=(?P<cost>-?\d+) dmg=(?P<dmg>-?[\d.]+) all=(?P<all>True|False) "
    r"block=(?P<block>-?\d+) poison=(?P<poison>-?\d+) draw=(?P<draw>-?\d+) shivs=(?P<shivs>-?\d+) "
    r"ok=(?P<ok>True|False) note=(?P<note>.*?) vars=\[(?P<vars>.*)\]\s*$"
)

# 日志行首形如 `14:10:49 [DamageAdvisor] …`，用来给不一致标时间
TIME_RE = re.compile(r"^(?P<time>\d{2}:\d{2}:\d{2})\s")


@dataclass
class Mismatch:
    lineno: int
    time: str
    snapshot: int
    card: str
    field: str
    got: float
    expected: float
    why: str


@dataclass
class Stats:
    lines_scanned: int = 0
    card_lines: int = 0
    skipped_unmodeled: int = 0
    skipped_uncheckable: int = 0
    compared: int = 0
    cards_seen: set[str] = field(default_factory=set)


def parse_vars(raw: str) -> dict[str, tuple[float, float]]:
    """`Damage=6/6,Cards=2/2` → {"Damage": (6.0, 6.0), ...}（base, preview）"""
    out: dict[str, tuple[float, float]] = {}
    for chunk in raw.split(","):
        chunk = chunk.strip()
        if not chunk or "=" not in chunk:
            continue
        key, _, value = chunk.partition("=")
        base, _, preview = value.partition("/")

        def num(text: str) -> float:
            try:
                return float(text.strip())
            except ValueError:
                return 0.0

        out[key.strip()] = (num(base), num(preview))
    return out


def preview(vars_: dict[str, tuple[float, float]], key: str) -> float | None:
    entry = vars_.get(key)
    return None if entry is None else entry[1]


def hits_of(name: str, vars_: dict[str, tuple[float, float]]) -> int:
    """这段伤害要乘几倍：Repeat 变量优先，其次描述里写死的段数。"""
    repeat = preview(vars_, "Repeat")
    if repeat is not None and repeat > 1:
        return int(repeat)
    return HARDCODED_HITS.get(name, 1)


def expected_damage(name: str, vars_: dict[str, tuple[float, float]]) -> float:
    # Analyze 用的是 EstimateDamage：先找 Damage，再退到 CalculatedDamage，并且**向下取整**
    raw = preview(vars_, "Damage")
    if raw is None:
        raw = preview(vars_, "CalculatedDamage")
    if raw is None:
        return 0.0
    return math.floor(raw) * hits_of(name, vars_)


def expected_block(name: str, vars_: dict[str, tuple[float, float]]) -> float | None:
    raw = preview(vars_, "Block")
    if raw is None:
        # 蜃景这类计算型格挡走 CalculatedBlock（游戏自己算出来的当前值）
        raw = preview(vars_, "CalculatedBlock")
    return None if raw is None else math.floor(raw)


def expected_poison(name: str, vars_: dict[str, tuple[float, float]]) -> float:
    raw = preview(vars_, "PoisonPower")
    if raw is None:
        return 0.0
    raw = math.floor(raw)
    # 与 Analyze 一致："随机给予 N 层中毒 Repeat 次"这类，总毒量 = N × 次数
    if name not in CARDS_MEANS_SHIVS and preview(vars_, "Damage") is None:
        repeat = preview(vars_, "Repeat")
        if repeat is not None and repeat > 1:
            return raw * int(repeat)
    return raw


def expected_draw(name: str, vars_: dict[str, tuple[float, float]]) -> float:
    if name in CARDS_MEANS_DISCARD or name in CARDS_MEANS_SHIVS:
        return 0.0
    cards = preview(vars_, "Cards")
    if cards is not None:
        return cards
    return float(FIXED_DRAW.get(name, 0))


def expected_shivs(name: str, vars_: dict[str, tuple[float, float]]) -> float:
    total = 0.0
    if name in CARDS_MEANS_SHIVS:
        cards = preview(vars_, "Cards")
        if cards is not None:
            total += cards
    shivs_var = preview(vars_, "Shivs")
    if shivs_var is not None:
        total += shivs_var
    return total


def check_card(lineno: int, time_str: str, snapshot: int, m: re.Match, stats: Stats) -> list[Mismatch]:
    name = m.group("name").strip()
    vars_ = parse_vars(m.group("vars"))
    stats.cards_seen.add(name)

    if m.group("ok") != "True":
        stats.skipped_unmodeled += 1
        return []

    got = {
        "dmg": float(m.group("dmg")),
        "block": float(m.group("block")),
        "poison": float(m.group("poison")),
        "draw": float(m.group("draw")),
        "shivs": float(m.group("shivs")),
    }
    expected: dict[str, float] = {
        "dmg": expected_damage(name, vars_),
        "poison": expected_poison(name, vars_),
        "draw": expected_draw(name, vars_),
        "shivs": expected_shivs(name, vars_),
    }
    blk = expected_block(name, vars_)
    if blk is None:
        expected["block"] = 0.0
    else:
        expected["block"] = blk

    out: list[Mismatch] = []
    stats.compared += 1
    for fld, exp in expected.items():
        if abs(got[fld] - exp) > 1e-6:
            out.append(Mismatch(lineno, time_str, snapshot, name, fld, got[fld], exp, m.group("vars")))
    return out


def fmt(x: float) -> str:
    return str(int(x)) if abs(x - round(x)) < 1e-6 else f"{x:.2f}"


def main() -> int:
    here = os.path.dirname(os.path.abspath(__file__))
    repo = os.path.dirname(here)
    default_log = os.path.join(
        r"D:\SteamLibrary\steamapps\common\Slay the Spire 2", "mods", "DamageAdvisor", "DamageAdvisor.log"
    )
    ap = argparse.ArgumentParser(description="比对 DamageAdvisor.log 里的面板值与游戏卡面值")
    ap.add_argument("--log", default=default_log, help=f"日志路径（默认 {default_log}）")
    ap.add_argument("--report", default=os.path.join(repo, "docs", "verify-report.txt"), help="报告输出路径")
    ap.add_argument(
        "--tail",
        type=int,
        default=0,
        help="只扫描日志最后 N 行（0 = 全部）。日志是追加的，修完一处要做复验时用它避开历史数据",
    )
    args = ap.parse_args()

    if not os.path.isfile(args.log):
        print(f"找不到日志：{args.log}", file=sys.stderr)
        alt = args.log + ".old"
        if os.path.isfile(alt):
            print(f"提示：同目录有 {os.path.basename(alt)}，可用 --log 指定它", file=sys.stderr)
        return 2

    stats = Stats()
    mismatches: list[Mismatch] = []
    snapshot = 0
    current_time = "?"
    with open(args.log, "r", encoding="utf-8", errors="replace") as fh:
        all_lines = fh.read().splitlines()
    start = max(0, len(all_lines) - args.tail) if args.tail > 0 else 0
    for lineno, line in enumerate(all_lines[start:], start + 1):
        stats.lines_scanned += 1
        tm = TIME_RE.match(line)
        if tm:
            current_time = tm.group("time")
        snap = SNAPSHOT_RE.search(line)
        if snap:
            snapshot = int(snap.group("n"))
            continue
        m = CARD_RE.search(line)
        if not m:
            continue
        stats.card_lines += 1
        mismatches.extend(check_card(lineno, current_time, snapshot, m, stats))

    lines = [
        "DamageAdvisor 日志验证报告（A 类：面板值 vs 游戏卡面值）",
        f"日志：{args.log}" + (f"（只扫最后 {args.tail} 行）" if args.tail > 0 else ""),
        f"扫描 {stats.lines_scanned} 行，牌行 {stats.card_lines} 条："
        f"比对 {stats.compared} 张、跳过未建模 {stats.skipped_unmodeled} 张",
        f"出现过的牌：{len(stats.cards_seen)} 种",
        "",
    ]
    if mismatches:
        lines.append(f"发现 {len(mismatches)} 处不一致：")
        lines.append("")
        for mm in mismatches:
            lines.append(
                f"  [{mm.time}] 第{mm.lineno}行 快照#{mm.snapshot}  {mm.card}  {mm.field}: "
                f"面板={fmt(mm.got)} 期望={fmt(mm.expected)}   [vars: {mm.why}]"
            )
    else:
        lines.append("没有发现不一致（注意：这只覆盖日志里出现过的牌，且抓不到 B 类错误）")
    report = "\n".join(lines) + "\n"

    report_dir = os.path.dirname(args.report)
    if report_dir:
        os.makedirs(report_dir, exist_ok=True)
    with open(args.report, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(report)
    print(report)
    print(f"（报告已写入 {args.report}）")
    return 1 if mismatches else 0


if __name__ == "__main__":
    sys.exit(main())
