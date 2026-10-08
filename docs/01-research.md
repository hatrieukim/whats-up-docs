# Research: DrivenData "What's Up, Docs?" (competition 297)

Nguồn: trang competition (overview, problem description, about, rules, leaderboard),
blog benchmark https://drivendata.co/blog/whats-up-docs-benchmark/ . Ngày tra cứu: 2026-10-07.

## 1. Vấn đề gốc

- Loại: practice competition, beginner, **không có giải tiền**, deadline còn ~1 năm (có thể gia hạn).
- Bài toán: **sinh abstract học thuật (1 đoạn)** cho paper khoa học xã hội lấy từ SocArXiv (OSF Preprints).
- Dữ liệu đã được tiền xử lý: body paper ở dạng Markdown, **đã bỏ abstract, references,
  acknowledgements và các đoạn quá giống abstract**. Paper > 10.000 ký tự, tiếng Anh, CC-BY 4.0.
- Abstract gốc do tác giả viết. Vì paper là public trên SocArXiv nên có thể tra ngược abstract thật
  → #1 leaderboard đạt 0.982 gần như chắc chắn là leakage, không phải mô hình.

## 2. Dữ liệu

| File | Nội dung |
|---|---|
| `train.csv` | 1.000 dòng: `paper_id`, `text`, `summary` (abstract tham chiếu) |
| `test_features.csv` | 345 dòng: `paper_id` (1000–1344), `text` |
| `submission_format.csv` | mẫu nộp: `paper_id`, `summary` |
| attribution file | title / author / link các paper |

Thống kê (train):

| | mean | std | min | max |
|---|---|---|---|---|
| `text` (ký tự) | 42.050 | 21.412 | 10.145 | ~196.000 |
| `summary` (ký tự) | 1.275 | 426 | 90 | 4.359 |

Ước lượng token: ~4 ký tự/token → text trung bình ~10k token, dài nhất ~50k token.
Text có HTML entity (`&amp;`) chưa unescape.

Data download cần đăng nhập DrivenData + join competition.

## 3. Metric: mean ROUGE-2 F1 per document

- Score = (1/N) Σ F1(pred_i, ref_i); F1 = 2PR/(P+R);
  P = bigram_matches / bigrams_in_pred; R = bigram_matches / bigrams_in_ref.
- Implementation: port của google-research `rouge_score`, **không stemming**, không dùng NLTK.
- Tokenizer: lowercase → thay mọi ký tự không phải alnum bằng space → split → giữ token khớp `^[a-z0-9]+$`.
  Hệ quả: dấu câu, gạch nối, ký tự ngoài ASCII (é, ü...) đều bị bỏ/ cắt token.
- Bigram đếm bằng Counter (có trùng lặp, overlap = min count).
- Hàm ý thiết kế:
  - Metric hoàn toàn lexical. Diễn đạt lại đúng nghĩa nhưng khác từ = 0 điểm.
  - **Độ dài dự đoán ảnh hưởng mạnh**: quá ngắn → recall thấp; quá dài → precision thấp.
    Tối ưu độ dài nên gần độ dài abstract thật (~1.275 ký tự ≈ 200 từ).
  - Copy nguyên văn các câu "abstract-like" từ body (extractive) thường ăn điểm ROUGE-2 tốt
    hơn abstractive thuần. Dữ liệu đã cố bỏ các đoạn quá giống abstract nhưng intro/conclusion vẫn còn.

## 4. Rules quan trọng

- Được dùng external data và pre-trained model nếu có quyền sử dụng. **Không giới hạn LLM phải chạy local**
  (benchmark chạy local chỉ để dễ tiếp cận).
- Mỗi test sample phải xử lý **độc lập**, không dùng thông tin từ sample test khác.
- Không annotate thủ công test. Pipeline phải chạy tự động trên test mới, không retrain.
- Code chỉ được chia sẻ public (MIT). Cấm chia sẻ private.
- Giới hạn submission/ngày: hiển thị trên site sau khi join (chưa xem được vì chưa login).

## 5. Benchmark (tham chiếu chính thức)

- Model: Gemma 3 1B qua Ollama (OpenAI-compatible API, `localhost:11434`), context mặc định 2.048 token.
- Truncate text còn 7.500 ký tự đầu. Prompt: "write a one-paragraph academic abstract ... Return only your paragraph".
- Split 700/300 rồi 490/210 train/val; "split forever".
- Kết quả: prompt 1 câu: train 0.0578 / val 0.0595. Prompt abstract: train 0.0798 / val 0.0779.
  **Leaderboard: 0.0781**.
- Gợi ý từ benchmark: tăng context, recursive summarization, few-shot, prompt optimization (DSPy),
  model lớn hơn.

## 6. Leaderboard (public, 2026-10-07, 622 người tham gia)

| Hạng | Score | Ghi chú |
|---|---|---|
| 1 | 0.9820 | leakage (1 submission) |
| 2 | 0.1800 | |
| 3 | 0.1775 | |
| 4–6 | 0.1742 – 0.1728 | |
| 7–13 | 0.1654 – 0.1548 | nhiều người đứng ở 0.1578 |
| Benchmark | 0.0781 | Gemma3 1B |
| median người chơi | ~0.10–0.13 | |

Mục tiêu thực tế: **> 0.175** là top 3 hợp lệ; ~0.16 vào top 10.

## 7. Môi trường local hiện có

- MacBook Apple M1 Pro, 14 core, **16 GB RAM** → chạy local được model ~7–8B quantized (Q4),
  14B sẽ rất chậm/ chật. Chưa cài Ollama. Python 3.14 + uv có sẵn.
- Project dir hiện trống, chưa có data.

## 8. Câu hỏi mở cho phần thiết kế

1. Chạy LLM local (Ollama/MLX trên M1 Pro) hay API (Claude/GPT)? Rules cho phép API; 345 test doc
   × ~10k token ≈ 3.5M input token, chi phí thấp.
2. Extractive vs abstractive vs hybrid: với ROUGE-2 thuần lexical, hybrid (LLM được ép dùng câu/cụm
   từ nguyên văn + kiểm soát độ dài) nhiều khả năng cho điểm cao nhất.
3. Có fine-tune model nhỏ (vd. Qwen 7B LoRA) trên 1.000 cặp train không, hay chỉ prompt engineering
   + few-shot + chọn độ dài tối ưu bằng validation?
4. Cần tài khoản DrivenData đã join competition để tải data (cần user làm bước này).
