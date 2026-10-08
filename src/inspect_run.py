"""In mẫu câu oracle model bỏ sót và câu model chọn sai cho một lượt."""
import random, sys
from collections import Counter
sys.path.insert(0, __import__("os").path.dirname(__file__))
from analyze_run import rows, parse_ctx, oracle  # noqa: E402

random.seed(1)
first = Counter(); tot = 0; miss, wrong = [], []
for pid, ctx, raw, summ, score, ref in rows:
    S = parse_ctx(ctx); orc, _ = oracle(S, ref); used = [n for n in S if summ and S[n][0] in summ]
    begin = [n for n in S if S[n][1] in ("BEGINNING", "FULL")][:5]
    for j, n in enumerate(begin): first[(j, "o")] += n in orc; first[(j, "m")] += n in used
    tot += 1
    miss += [(pid, n, len(S), S[n][1], S[n][0]) for n in set(orc) - set(used)]
    wrong += [(pid, n, len(S), S[n][1], S[n][0]) for n in set(used) - set(orc)]
print("5 câu đầu bài, tỉ lệ là câu oracle / model chọn:", [f"{first[(j,'o')]/tot:.0%}/{first[(j,'m')]/tot:.0%}" for j in range(5)])
print("\n=== Câu ORACLE model BỎ SÓT ===")
for x in random.sample(miss, 14): print(f"#{x[0]} [{x[1]}/{x[2]}] {x[3]}: {x[4][:220]}")
print("\n=== Câu model CHỌN nhưng không phải oracle ===")
for x in random.sample(wrong, 14): print(f"#{x[0]} [{x[1]}/{x[2]}] {x[3]}: {x[4][:220]}")
