# EDA: dữ liệu thật (train 1.000 / test 345)

Script tái lập: `src/metric.py` (ROUGE-2 đúng như BTC), `src/baselines.py`. Env: `.venv` (uv, Python 3.12).

## Kích thước & độ dài

| | text (ký tự) | summary (ký tự) | summary (từ) |
|---|---|---|---|
| mean | 42.050 | 1.275 | 184 |
| p5 / p50 / p95 | 16k / 39k / 77k | 630 / 1.258 / 1.919 | 94 / 182 / 279 |
| min / max | 10k / 197k | 90 / 4.359 | 12 / 721 |

- Test có phân bố độ dài text giống train (mean 41.7k, max 156k).
- Độ dài summary gần như **không tương quan** với độ dài text (corr 0.14). Target ~180 từ là an toàn.
- 12% summary có nhiều đoạn; 1.5% là structured abstract (Background:/Methods:...); 56% có cụm "this paper/study/article".

## Đặc điểm text (Markdown từ PDF)

- 73% doc mở đầu bằng header "Introduction"; trung bình 14,7 header/doc; 69% có header Conclusion/Discussion.
- Nhiễu: 71% doc còn HTML entity (`&amp;`), 59% có `<!-- image -->` (3,5 cái/doc), ~4,4 dòng caption Figure/Table/doc,
  ~27 trích dẫn dạng `(Author, 2020)`/doc. Summary gần như không có nhiễu này → **phải làm sạch trước khi đưa vào LLM**.
- ~9% doc kết thúc bằng một dòng ngắn không có dấu chấm, nhiều trường hợp chính là **tiêu đề bài báo** bị đẩy xuống cuối.
- Doc thoái hóa: train có 1 doc (id 197) gần như chỉ còn bảng, không có câu văn; cần fallback.

## Rò rỉ abstract vào body (quan trọng)

- 65% doc có ít nhất 1 cụm 8-gram trùng nguyên văn với abstract; 33% có ≥10 cụm 8-gram trùng.
  → Việc "xóa đoạn giống abstract" của BTC không triệt để. Câu trong Introduction/Conclusion thường
  lặp lại abstract gần nguyên văn. **Copy nguyên văn là chiến lược ăn điểm**.
- 56% bigram của abstract xuất hiện đâu đó trong text. Vị trí xuất hiện lần đầu: 42% nằm trong 10% đầu doc,
  rồi tăng nhẹ lại ở 10% cuối. Mật độ bigram-khớp/1k token: section đầu 64, section cuối 62, giữa 42.

## Baseline không dùng ML (ROUGE-2 trên toàn bộ train)

| Phương pháp | ROUGE-2 | P | R |
|---|---|---|---|
| Benchmark BTC (Gemma3 1B, leaderboard) | 0.078 | | |
| Lead-400 token thô | 0.1165 | 0.088 | 0.190 |
| Lead-400 đã làm sạch + bỏ trích dẫn | 0.123 | | |
| Lead-300 + Tail-100 | 0.117 | | |
| Cue-phrase extractive ("this paper", "we find"...) ≤250 từ, làm sạch | **0.135** | 0.126 | 0.161 |
| Oracle extractive tham lam (chọn câu theo ref, 150 doc) | **0.350** | | |

- Lead-K đạt đỉnh ở K≈400–500 token rồi giảm (precision tụt). Độ dài output rất nhạy.
- Cue-phrase extractive 0.135 đã ngang **top 20 leaderboard** mà chưa dùng mô hình nào.
- Phân bố per-doc của cue-extract: p10 0.048, p50 0.115, p90 0.230; 11% doc < 0.05 (summary quá ngắn/khác giọng,
  hoặc doc không có câu "tuyên bố đóng góp").
- Oracle 0.35 cho thấy trần của extractive còn rất xa so với top 0.18: vấn đề là **chọn đúng câu**, không phải viết lại.

## Hàm ý thiết kế

1. Pipeline bắt buộc có bước làm sạch Markdown (entity, image marker, caption, trích dẫn, bảng).
2. Chiến lược chính: **hybrid extractive-abstractive** — LLM đọc intro + conclusion (đã lọc), được yêu cầu
   viết abstract ~180 từ, giữ nguyên câu/cụm từ gốc khi có thể, mẫu few-shot lấy từ train.
3. Kiểm soát độ dài output (150–220 từ) và post-process (bỏ "Here is...", bỏ markdown).
4. Có thể huấn luyện **sentence scorer** (feature: vị trí, cue, similarity với doc, độ dài) trên train bằng nhãn oracle
   để tiến gần 0.35, dùng làm bước chọn ngữ cảnh cho LLM hoặc tự đứng một mình.
5. Validation: giữ split cố định (vd. 800/200, seed 0) và chỉ đánh giá trên 200 doc để tránh overfit prompt.

## Môi trường

- Không có API key Anthropic/OpenAI trong env, chưa cài Ollama/MLX. Cần quyết định local vs API trước khi làm bước LLM.
