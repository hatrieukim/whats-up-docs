"""Zero-ML extractive baselines, used to calibrate the metric and as fallbacks."""
import re, html
from rouge_score import tokenize as rtok

CUE = re.compile(r"\b(this (paper|article|study|chapter|research|essay|work)|we (argue|show|find|examine|investigate|propose|use|analy[sz]e|draw|contribute|explore|demonstrate|test|present|develop|document|estimate|identify)|our (findings|results|analysis|study|paper)|the (paper|article|study) (argues|shows|finds|examines|explores))\b", re.I)

def clean(t: str, drop_citations: bool = True) -> str:
    t = html.unescape(t)
    t = t.replace("<!-- image -->", "")
    t = re.sub(r"^#+ .*$", "", t, flags=re.M)
    t = re.sub(r"^(Figure|Fig\.|Table)\s*\d[^\n]*$", "", t, flags=re.M)
    if drop_citations:
        t = re.sub(r"\s*\((?:[^()]*\d{4}[^()]*)\)", "", t)   # (Smith, 2020; ...)
        t = re.sub(r"\s*\[\d+(?:[,–-]\s*\d+)*\]", "", t)      # [12], [3-5]
    return t

def sents(t: str):
    s = re.split(r"(?<=[.!?])\s+(?=[A-Z(\"'])", re.sub(r"\s+", " ", clean(t)))
    return [x.strip() for x in s if 6 <= len(x.split()) <= 90]

def lead(t: str, k: int = 400) -> str:
    return " ".join(rtok.tokenize(clean(t), None)[:k])

def cue_extract(t: str, maxw: int = 250, lead_frac: float = 0.25, tail_frac: float = 0.85) -> str:
    S = sents(t); n = len(S); out = []; w = 0
    for i, s in enumerate(S):
        if CUE.search(s) and (i < n * lead_frac or i > n * tail_frac):
            out.append(s); w += len(s.split())
            if w >= maxw: break
    if w < 120:
        for s in S:
            if s not in out:
                out.append(s); w += len(s.split())
            if w >= maxw: break
    return " ".join(out)
