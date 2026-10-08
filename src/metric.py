"""ROUGE-2 F1 exactly as the competition scores it (google rouge_score, no stemming)."""
from rouge_score import rouge_scorer
import numpy as np

_scorer = rouge_scorer.RougeScorer(["rouge2"], use_stemmer=False)

def rouge2_f1(pred: str, ref: str) -> float:
    return _scorer.score(ref, pred)["rouge2"].fmeasure

def rouge2_prf(pred: str, ref: str):
    s = _scorer.score(ref, pred)["rouge2"]
    return s.precision, s.recall, s.fmeasure

def mean_rouge2(preds, refs) -> float:
    return float(np.mean([rouge2_f1(p, r) for p, r in zip(preds, refs)]))
