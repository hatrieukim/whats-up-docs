# EDA: the real data (train 1,000 / test 345)

Reproduction scripts: `src/metric.py` (ROUGE-2 exactly as the organizers compute it), `src/baselines.py`. Environment: `.venv` (Python 3.12).

## Size and length

| | text (chars) | summary (chars) | summary (words) |
|---|---|---|---|
| mean | 42,050 | 1,275 | 184 |
| p5 / p50 / p95 | 16k / 39k / 77k | 630 / 1,258 / 1,919 | 94 / 182 / 279 |
| min / max | 10k / 197k | 90 / 4,359 | 12 / 721 |

- The test set has the same text length distribution as train (mean 41.7k, max 156k).
- Summary length is almost **uncorrelated** with text length (corr 0.14). A target of ~180 words is safe.
- 12% of summaries have several paragraphs; 1.5% are structured abstracts (Background:/Methods:...); 56% contain "this paper/study/article".

## Text characteristics (Markdown converted from PDF)

- 73% of docs start with an "Introduction" header; 14.7 headers per doc on average; 69% have a Conclusion/Discussion header.
- Noise: 71% of docs still contain HTML entities (`&amp;`), 59% contain `<!-- image -->` (3.5 per doc), ~4.4 Figure/Table caption lines per doc,
  ~27 `(Author, 2020)`-style citations per doc. Summaries have almost none of this → **the text must be cleaned before it goes to the LLM**.
- ~9% of docs end with a short line without a period; in many cases this is the **paper title** pushed to the end.
- Degenerate docs: train has 1 doc (id 197) that is almost only tables, with no prose; a fallback is needed.

## Abstract leaking into the body (important)

- 65% of docs share at least one verbatim 8-gram with their abstract; 33% share ≥ 10 such 8-grams.
  → The organizers' "remove passages similar to the abstract" step is incomplete. Introduction/Conclusion sentences often
  repeat the abstract almost verbatim. **Copying verbatim is the scoring strategy**.
- 56% of the abstract's bigrams appear somewhere in the text. First occurrence: 42% fall in the first 10% of the doc,
  with a small rise again in the last 10%. Matching bigrams per 1k tokens: first section 64, last section 62, middle 42.

## Baselines without ML (ROUGE-2 on the whole train set)

| Method | ROUGE-2 | P | R |
|---|---|---|---|
| Organizers' benchmark (Gemma 3 1B, leaderboard) | 0.078 | | |
| Lead-400 tokens, raw | 0.1165 | 0.088 | 0.190 |
| Lead-400, cleaned + citations removed | 0.123 | | |
| Lead-300 + Tail-100 | 0.117 | | |
| Cue-phrase extractive ("this paper", "we find"...) ≤ 250 words, cleaned | **0.135** | 0.126 | 0.161 |
| Greedy extractive oracle (picks sentences using the reference, 150 docs) | **0.350** | | |

- Lead-K peaks at K ≈ 400–500 tokens, then drops (precision falls). Output length is very sensitive.
- The cue-phrase extractive baseline at 0.135 already matches the **top 20 of the leaderboard** without any model.
- Per-doc distribution of cue-extract: p10 0.048, p50 0.115, p90 0.230; 11% of docs < 0.05 (summary too short / different voice,
  or the doc has no "contribution claim" sentences).
- The 0.35 oracle shows the extractive ceiling is far above the top score of 0.18: the problem is **picking the right sentences**, not rewriting.

## Design implications

1. The pipeline needs a Markdown cleaning step (entities, image markers, captions, citations, tables).
2. Main strategy: **hybrid extractive-abstractive**: the LLM reads the (filtered) introduction + conclusion, is asked
   to write a ~180-word abstract, keeping the original sentences/phrases where possible, with few-shot examples from train.
   (In the end the system went fully extractive: the LLM only picks sentence numbers. See the README.)
3. Control the output length (150–220 words) and post-process (drop "Here is...", drop Markdown).
4. A **sentence scorer** (features: position, cue, similarity to the doc, length) could be trained on train with oracle labels
   to get closer to 0.35, used to select context for the LLM or on its own.
5. Validation: keep a fixed split (e.g. 800/200, seed 0) and evaluate only on the 200 docs to avoid overfitting the prompt.
