"""Sinh fixture để test C# khớp tuyệt đối với code chấm chính thức của BTC.

Chạy: .venv/bin/python src/make_fixtures.py  → data/fixtures/*.
"""
import hashlib, json, random, sys
from pathlib import Path

import pandas as pd

sys.path.insert(0, str(Path(__file__).parent))
import official_rouge as off  # noqa: E402
from baselines import cue_extract, lead  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "data" / "fixtures"
OUT.mkdir(parents=True, exist_ok=True)

train = pd.read_csv(ROOT / "data" / "train.csv")
test = pd.read_csv(ROOT / "data" / "test_features.csv")
scorer = off.RougeScorer(["rouge2"])
rng = random.Random(0)


def score(pred, ref):
    s = scorer.score(ref, pred)["rouge2"]  # score(target, prediction)
    return s.precision, s.recall, s.fmeasure


# 1. Cặp (pred, ref) đa dạng: điểm thấp, trung bình, cao, và văn bản thô nhiều ký tự lạ
EDGE = [
    ("", "a b c"), ("a b c", ""), ("", ""), ("x", "x"), ("the the the", "the the"),
    ("Café-Au-Lait 2020s!!", "cafe au lait 2020s"), ("COVID-19 don't", "covid 19 don t"),
    ("İstanbul İzmir", "i stanbul i zmir"), ("Kelvin scale", "kelvin scale"),
    ("Straße STRASSE", "strasse straße"), ("naïve résumé", "na ve r sum"),
    ("tab\tnew\nline\r\nend", "tab new line end"), ("emoji 🎉 test 🎉 test", "emoji test test"),
    ("“quoted” — dash – en", "quoted dash en"), ("ＦＵＬＬ width", "full width"),
    ("ÀÉÎÕÜ Ç Ñ", "ç ñ"), ("x² 10³ ½", "x 10"),
]
n = 0
with open(OUT / "rouge2_pairs.jsonl", "w") as f:
    for pred, ref in EDGE:
        p, r, fm = score(pred, ref)
        f.write(json.dumps({"pred": pred, "ref": ref, "p": p, "r": r, "f": fm}) + "\n"); n += 1
    for text, ref in zip(train.text, train.summary):
        words = ref.split()
        half = " ".join(words[: len(words) // 2]) + " " + " ".join(rng.sample(text.split(), min(80, len(text.split()))))
        for pred in (cue_extract(text), lead(text, 300), text[:1500], half):
            p, r, fm = score(pred, ref)
            f.write(json.dumps({"pred": pred, "ref": ref, "p": p, "r": r, "f": fm}) + "\n"); n += 1
print("rouge2_pairs:", n)

# 2. Token hoá cả corpus (text + summary) để bắt mọi khác biệt lowercase/regex
with open(OUT / "tokens.jsonl", "w") as f:
    for df in (train, test):
        for row in df.itertuples():
            for field in ("text", "summary"):
                if not hasattr(row, field):
                    continue
                toks = off.tokenize(getattr(row, field), None)
                f.write(json.dumps({"id": int(row.paper_id), "field": field, "n": len(toks),
                                    "sha": hashlib.sha256(" ".join(toks).encode()).hexdigest()}) + "\n")
print("tokens: done")

# 3. Chia tập giống hệt Splits.cs
key = lambda i: hashlib.sha256(f"whatsupdocs-split-v1:{i}".encode()).hexdigest()
ordered = sorted(train.paper_id.tolist(), key=key)
dev = ordered[:150]
splits = {"quick": sorted(dev[:30]), "dev": sorted(dev), "holdout": sorted(ordered[150:250]),
          "pool": sorted(ordered[250:]), "test": sorted(test.paper_id.tolist())}
(OUT / "splits.json").write_text(json.dumps(splits))
print("splits:", {k: len(v) for k, v in splits.items()})

# 4. Điểm cue_extract (Python) trên dev, để so với bản C#
by_id = train.set_index("paper_id")
per = {int(i): score(cue_extract(by_id.text[i]), by_id.summary[i])[2] for i in splits["dev"]}
mean = sum(per.values()) / len(per)
(OUT / "cue_dev.json").write_text(json.dumps({"mean": mean, "perDoc": per}))
print("cue_extract dev (python): %.4f" % mean)
