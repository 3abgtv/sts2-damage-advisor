"""一次性的覆盖率扫描：把卡池转储里的牌按"描述里有没有触发器/条件/次数"分组打印。

用法： grep "卡池 " DamageAdvisor.log | python tools/scan-cards.py
（注意用管道喂数据，脚本从 stdin 读；不要再把脚本本身走 heredoc）
"""
import re
import sys

text = sys.stdin.buffer.read().decode("utf-8", "replace")
seen: dict[str, tuple[str, str, str]] = {}
for line in text.splitlines():
    m = re.search(r"卡池 ([A-Za-z]+) .*?name=(\S+) .*? desc=\[(.*)\] vars=\[(.*)\]\s*$", line)
    if not m:
        continue
    cls, name, desc, vars_ = m.groups()
    seen[cls] = (name, desc, vars_)

print(f"parsed {len(seen)} cards")
if not seen:
    sys.exit(1)

KEY = ["每", "额外", "随机", "下个回合", "若", "如果", "翻倍", "无法", "不能", "失去", "重复", "斩杀", "复制", "打出"]
hits = [(c, v) for c, v in seen.items() if any(k in v[1] for k in KEY)]
print(f"keyword hits: {len(hits)}\n")
for cls, (name, desc, vars_) in sorted(hits, key=lambda kv: kv[1][0]):
    clean = re.sub(r"\[/?gold\]|\[/?purple\]", "", desc)
    print(f"{name:8s} | {clean}")
    print(f"         | vars={vars_}")
