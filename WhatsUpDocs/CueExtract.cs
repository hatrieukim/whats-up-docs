using System.Text.RegularExpressions;

namespace WhatsUpDocs;

// Baseline không dùng LLM, port từ src/baselines.py: lấy câu "this paper / we find..." ở đầu và cuối bài
public static class CueExtract
{
    public static readonly Regex Cue = new(
        @"\b(this (paper|article|study|chapter|research|essay|work)|we (argue|show|find|examine|investigate|propose|use|analy[sz]e|draw|contribute|explore|demonstrate|test|present|develop|document|estimate|identify)|our (findings|results|analysis|study|paper)|the (paper|article|study) (argues|shows|finds|examines|explores))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Run(string text, int maxWords = 250, double leadFrac = 0.25, double tailFrac = 0.85)
    {
        var sentences = TextClean.Sentences(TextClean.Clean(text, keepHeaders: false));
        int n = sentences.Count, words = 0;
        var output = new List<string>();
        for (int i = 0; i < n; i++)
        {
            string s = sentences[i];
            if (Cue.IsMatch(s) && (i < n * leadFrac || i > n * tailFrac))
            {
                output.Add(s);
                words += TextClean.Words(s);
                if (words >= maxWords) break;
            }
        }
        if (words < 120)
        {
            foreach (string s in sentences)
            {
                if (!output.Contains(s))
                {
                    output.Add(s);
                    words += TextClean.Words(s);
                }
                if (words >= maxWords) break;
            }
        }
        return string.Join(" ", output);
    }
}
