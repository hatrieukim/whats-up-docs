"""Soi lỗi chọn câu của một lượt: so câu model chọn với câu oracle theo từng đặc điểm câu. Không gọi LLM.
Chạy: .venv/bin/python src/study_errors.py <run_id> [số bài điểm thấp để in]"""
import re, sys
from collections import Counter, defaultdict
import numpy as np
sys.path.insert(0, __file__.rsplit("/", 1)[0])
from analyze_run import db, f1, big, parse_ctx, picks, oracle, assemble

RUN = int(sys.argv[1]); SHOW = int(sys.argv[2]) if len(sys.argv) > 2 else 0
rows = db.execute("""SELECT o.paper_id, o.context_text, o.raw_output, o.summary, o.score, d.summary
                     FROM outputs o JOIN docs d USING(paper_id) WHERE o.run_id=? AND o.error IS NULL""", (RUN,)).fetchall()

FEATS = {
    "aim (this paper/study/we examine…)": r"\b(this|the present|our) (paper|article|study|research|chapter|essay)\b|\bwe (examine|investigate|explore|ask|analy[sz]e|study|argue|propose)\b|\bthe (aim|goal|purpose|objective)\b",
    "question/hypothesis": r"\?|\bhypothes|\bresearch question|\bwe ask\b|\bwhether\b",
    "data/method": r"\b(data|survey|interview|sample|experiment|dataset|panel|regression|respondents|participants|we use|using)\b",
    "finding": r"\b(we find|find that|found that|results (show|suggest|indicate)|show that|reveal|demonstrate|evidence)\b",
    "implication/contribution": r"\b(contribut|implication|suggest that|policy|literature on)\b",
    "first-person we/our": r"\b(we|our)\b",
    "number/percent": r"\d",
    "citation-ish (et al, year)": r"et al|\(\d{4}|\b(19|20)\d\d\b",
    "structure (section…)": r"\b(section|remainder of|following section|chapter \d)\b",
}
FEATS = {k: re.compile(v, re.I) for k, v in FEATS.items()}

tot = Counter(); orc_n = Counter(); md_n = Counter(); hit = Counter()
missed, wrong = [], []
pos_bucket = lambda S, n: S[n][1] + (":đoạn đầu" if S[n][1] in ("BEGINNING", "FULL") and n - min(k for k in S if S[k][1] == S[n][1]) < 6 else "")
per_doc = []
for pid, ctx, raw, summ, score, ref in rows:
    S = parse_ctx(ctx)
    orc, of = oracle(S, ref)
    used = [n for n in S if summ and S[n][0] in summ]
    refb = set(big(ref))
    def prec(n):
        b = big(S[n][0]); return sum(1 for g in b if g in refb) / max(1, len(b))
    for n in S:
        s = S[n][0]
        keys = [k for k, rx in FEATS.items() if rx.search(s)] + ["part=" + pos_bucket(S, n), "len>35" if len(s.split()) > 35 else "len<=35"]
        for k in keys:
            tot[k] += 1; orc_n[k] += n in orc; md_n[k] += n in used; hit[k] += (n in orc and n in used)
        if n in orc and n not in used: missed.append((prec(n), pid, n, pos_bucket(S, n), s))
        if n in used and n not in orc: wrong.append((prec(n), pid, n, pos_bucket(S, n), s))
    per_doc.append((score, of, pid, used, orc, S, ref))

print(f"{len(rows)} bài. F1 model {np.mean([d[0] for d in per_doc]):.4f}, oracle context {np.mean([d[1] for d in per_doc]):.4f}")
print(f"\n{'đặc điểm':38} {'số câu':>7} {'oracle lấy':>10} {'model lấy':>10} {'model/oracle':>12}")
for k in sorted(tot, key=lambda k: -orc_n[k]):
    if orc_n[k] < 5: continue
    print(f"{k:38} {tot[k]:7} {orc_n[k]/tot[k]:10.1%} {md_n[k]/tot[k]:10.1%} {md_n[k]/max(1,orc_n[k]):12.2f}")
print(f"\ncâu oracle bị bỏ: {len(missed)}; câu model lấy mà oracle không lấy: {len(wrong)}")
wp = np.array([w[0] for w in wrong]); print("độ trùng (tỉ lệ cặp từ có trong abstract) của câu lấy sai:", np.round(np.percentile(wp, [25, 50, 75]), 2))
print("phần chứa câu bị bỏ:", Counter(m[3] for m in missed).most_common())
print("phần chứa câu lấy sai:", Counter(w[3] for w in wrong).most_common())

if SHOW:
    for score, of, pid, used, orc, S, ref in sorted(per_doc, key=lambda d: d[0] - d[1])[:SHOW]:
        print(f"\n===== #{pid}  model {score:.3f}  oracle {of:.3f}")
        print("ABSTRACT:", ref[:700])
        for n in sorted(set(used) | set(orc)):
            tag = "ĐÚNG " if n in used and n in orc else "BỎ   " if n in orc else "SAI  "
            print(f"  {tag}[{n}] ({S[n][1]}) {S[n][0][:220]}")
