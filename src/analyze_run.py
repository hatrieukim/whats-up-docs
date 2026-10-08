"""Phân tích một lượt: câu model chọn so với câu oracle (chọn tham lam theo abstract thật). Không gọi LLM."""
import json, re, sqlite3, sys
from collections import Counter
from pathlib import Path
import numpy as np
from rouge_score import rouge_scorer, tokenize as rtok

RUN = int(sys.argv[1]) if len(sys.argv) > 1 else 2
db = sqlite3.connect(Path(__file__).resolve().parent.parent / "data" / "app.db")
sc = rouge_scorer.RougeScorer(["rouge2"])
f1 = lambda p, r: sc.score(r, p)["rouge2"].fmeasure
tok = lambda s: rtok.tokenize(s, None)
big = lambda s: Counter(zip(tok(s), tok(s)[1:]))

rows = db.execute("""SELECT o.paper_id, o.context_text, o.raw_output, o.summary, o.score, d.summary
                     FROM outputs o JOIN docs d USING(paper_id) WHERE o.run_id=? AND o.error IS NULL""", (RUN,)).fetchall()

def parse_ctx(ctx):
    sents, part = {}, None
    for line in ctx.split("\n"):
        if line.startswith("==="): part = line.strip("= ").split(" ")[0]; continue
        m = re.match(r"^\[(\d+)\] (.*)$", line)
        if m: sents[int(m.group(1))] = (m.group(2), part)
    return sents

def picks(raw):
    m = re.search(r'"sentences"\s*:\s*\[([^\]]*)\]', raw or "")
    return [int(x) for x in re.findall(r"\d+", m.group(1))] if m else []

def oracle(sents, ref, maxn=15):
    target = big(ref); chosen = []; best = 0
    while len(chosen) < maxn:
        cand = [(f1(" ".join(sents[i][0] for i in sorted(chosen + [i])), ref), i) for i in sents if i not in chosen]
        if not cand: break
        f, i = max(cand)
        if f <= best: break
        best = f; chosen.append(i)
    return sorted(chosen), best

def assemble(pk, sents, maxw):
    used, w = [], 0
    for n in dict.fromkeys(pk):
        if n not in sents: continue
        k = len(sents[n][0].split())
        if used and w + k > maxw: continue
        used.append(n); w += k
    return " ".join(sents[n][0] for n in sorted(used))

def report():
    stats = Counter(); part_or, part_md = Counter(), Counter(); rel_or, rel_md = [], []
    nsent_or, nsent_md, words_or = [], [], []
    sweep = {w: [] for w in (120, 150, 170, 190, 210, 230, 250, 300)}
    firstk = {k: [] for k in (3, 4, 5, 6, 7, 8, 10)}
    for pid, ctx, raw, summ, score, ref in rows:
        S = parse_ctx(ctx); pk = picks(raw)
        orc, of = oracle(S, ref)
        used = [n for n in S if summ and S[n][0] in summ]
        stats["docs"] += 1; stats["orc"] += len(orc); stats["md"] += len(used); stats["both"] += len(set(orc) & set(used))
        for n in orc: part_or[S[n][1]] += 1; rel_or.append(n / len(S))
        for n in used: part_md[S[n][1]] += 1; rel_md.append(n / len(S))
        nsent_or.append(len(orc)); nsent_md.append(len(used)); words_or.append(sum(len(S[n][0].split()) for n in orc))
        for w in sweep: sweep[w].append(f1(assemble(pk, S, w), ref))
        for k in firstk: firstk[k].append(f1(assemble(pk[:k], S, 10_000), ref))

    print(f"docs {stats['docs']}  |  câu oracle TB {np.mean(nsent_or):.1f} ({np.mean(words_or):.0f} từ)  |  câu model dùng TB {np.mean(nsent_md):.1f}")
    print(f"trong số câu oracle, model lấy trúng {stats['both']/stats['orc']:.0%}; trong số câu model lấy, {stats['both']/stats['md']:.0%} là câu oracle")
    print("phần của context (oracle vs model):", {k: f"{part_or[k]/stats['orc']:.0%} vs {part_md[k]/stats['md']:.0%}" for k in part_or})
    print("vị trí tương đối trong context, theo 5 khúc (oracle):", np.round(np.histogram(rel_or, bins=5, range=(0,1))[0]/len(rel_or), 2))
    print("vị trí tương đối trong context, theo 5 khúc (model) :", np.round(np.histogram(rel_md, bins=5, range=(0,1))[0]/len(rel_md), 2))
    print("quét số từ tối đa (dùng lại lựa chọn của model):", {w: round(np.mean(v), 4) for w, v in sweep.items()})
    print("chỉ lấy k câu đầu model liệt kê (quan trọng nhất):", {k: round(np.mean(v), 4) for k, v in firstk.items()})


if __name__ == "__main__":
    report()
