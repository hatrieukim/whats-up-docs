# Research: DrivenData "What's Up, Docs?" (competition 297)

Sources: the competition pages (overview, problem description, about, rules, leaderboard) and the
benchmark blog post https://drivendata.co/blog/whats-up-docs-benchmark/ . Looked up on 2026-10-07.

These are the notes taken before any code was written; later findings are in [02-eda.md](02-eda.md)
and the [README](../README.md).

## 1. The problem

- Type: practice competition, beginner level, **no prize money**, deadline about a year away (may be extended).
- Task: **generate an academic abstract (one paragraph)** for social-science papers taken from SocArXiv (OSF Preprints).
- The data is preprocessed: the paper body is in Markdown, **with the abstract, references,
  acknowledgements and passages too similar to the abstract removed**. Papers are > 10,000 characters, in English, CC-BY 4.0.
- The reference abstracts were written by the authors. Since the papers are public on SocArXiv, the real abstracts
  can be looked up, so the #1 leaderboard score of 0.982 is almost certainly leakage, not a model.

## 2. Data

| File | Content |
|---|---|
| `train.csv` | 1,000 rows: `paper_id`, `text`, `summary` (reference abstract) |
| `test_features.csv` | 345 rows: `paper_id` (1000–1344), `text` |
| `submission_format.csv` | submission template: `paper_id`, `summary` |
| attribution file | title / authors / link of each paper |

Statistics (train):

| | mean | std | min | max |
|---|---|---|---|---|
| `text` (chars) | 42,050 | 21,412 | 10,145 | ~196,000 |
| `summary` (chars) | 1,275 | 426 | 90 | 4,359 |

Token estimate: ~4 chars/token, so the average text is ~10k tokens and the longest ~50k tokens.
The text still contains unescaped HTML entities (`&amp;`).

Downloading the data requires a DrivenData account that has joined the competition.

## 3. Metric: mean ROUGE-2 F1 per document

- Score = (1/N) Σ F1(pred_i, ref_i); F1 = 2PR/(P+R);
  P = bigram_matches / bigrams_in_pred; R = bigram_matches / bigrams_in_ref.
- Implementation: a port of google-research `rouge_score`, **no stemming**, no NLTK.
- Tokenizer: lowercase → replace every non-alphanumeric character with a space → split → keep tokens matching `^[a-z0-9]+$`.
  As a result, punctuation, hyphens and non-ASCII characters (é, ü...) are dropped or split tokens.
- Bigrams are counted with a Counter (duplicates count, overlap = min count).
- Design implications:
  - The metric is purely lexical. A correct paraphrase with different words scores 0.
  - **Prediction length matters a lot**: too short → low recall; too long → low precision.
    The best length is close to the real abstract length (~1,275 chars ≈ 200 words).
  - Copying "abstract-like" sentences verbatim from the body (extractive) usually scores better on ROUGE-2
    than pure abstractive generation. The organizers tried to remove passages too similar to the abstract, but the introduction and conclusion remain.

## 4. Important rules

- External data and pre-trained models are allowed if you have the right to use them. **The LLM does not have to run locally**
  (the benchmark runs locally only to be accessible).
- Each test sample must be processed **independently**, without information from other test samples.
- No manual annotation of the test set. The pipeline must run automatically on new test data, without retraining.
- Code may only be shared publicly (MIT). Private sharing is forbidden.
- Daily submission limit: shown on the site after joining.

## 5. Benchmark (official reference)

- Model: Gemma 3 1B via Ollama (OpenAI-compatible API, `localhost:11434`), default context of 2,048 tokens.
- Text truncated to the first 7,500 characters. Prompt: "write a one-paragraph academic abstract ... Return only your paragraph".
- Split 700/300, then 490/210 train/val; "split forever".
- Results: one-sentence prompt: train 0.0578 / val 0.0595. Abstract prompt: train 0.0798 / val 0.0779.
  **Leaderboard: 0.0781**.
- Suggestions from the benchmark: more context, recursive summarization, few-shot, prompt optimization (DSPy),
  bigger models.

## 6. Leaderboard (public, 2026-10-07, 622 participants)

| Rank | Score | Note |
|---|---|---|
| 1 | 0.9820 | leakage (1 submission) |
| 2 | 0.1800 | |
| 3 | 0.1775 | |
| 4–6 | 0.1742 – 0.1728 | |
| 7–13 | 0.1654 – 0.1548 | many entries at 0.1578 |
| Benchmark | 0.0781 | Gemma 3 1B |
| median participant | ~0.10–0.13 | |

Realistic target: **> 0.175** is a legitimate top 3; ~0.16 reaches the top 10.

## 7. Local environment at the time

- Apple M1 Pro laptop, 16 GB RAM → can run ~7–8B quantized (Q4) models locally;
  14B would be very slow / tight. No Ollama installed. Python and uv available.

## 8. Open design questions

1. Run the LLM locally (Ollama/MLX) or through an API? The rules allow APIs; 345 test docs
   × ~10k tokens ≈ 3.5M input tokens, which is cheap.
2. Extractive vs abstractive vs hybrid: with a purely lexical ROUGE-2, a hybrid (the LLM is forced to reuse sentences/phrases
   verbatim + length control) is most likely to score highest.
3. Fine-tune a small model (e.g. Qwen 7B LoRA) on the 1,000 training pairs, or only prompt engineering
   + few-shot + choosing the best length on validation?
4. A DrivenData account that has joined the competition is needed to download the data.
