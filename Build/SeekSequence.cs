using System.Text;

namespace Imp.Build;

// Locates a sequence of lines within a file using a cascade of equality
// checks: exact → right-trimmed → fully-trimmed → Unicode-folded + trimmed.
// The cascade is load-bearing — real models often emit context with
// cosmetic whitespace or curly-quote drift, and a naive exact match rejects
// those patches with "context not found".
//
// Ported verbatim from nb/Shell/ApplyPatch/SeekSequence.cs (same algorithm,
// same fold table, same mode order).

public static class SeekSequence
{
    public static int Find(IReadOnlyList<string> hay, IReadOnlyList<string> pattern, int start)
    {
        if (pattern.Count == 0) return start;
        if (start < 0) start = 0;
        if (start + pattern.Count > hay.Count) return -1;

        foreach (var mode in new[] { MatchMode.Exact, MatchMode.RTrim, MatchMode.FullTrim, MatchMode.FoldTrim })
        {
            for (int i = start; i + pattern.Count <= hay.Count; i++)
            {
                bool ok = true;
                for (int j = 0; j < pattern.Count; j++)
                {
                    if (!Equals(hay[i + j], pattern[j], mode)) { ok = false; break; }
                }
                if (ok) return i;
            }
        }
        return -1;
    }

    enum MatchMode { Exact, RTrim, FullTrim, FoldTrim }

    static bool Equals(string a, string b, MatchMode mode) => mode switch
    {
        MatchMode.Exact => a == b,
        MatchMode.RTrim => a.TrimEnd() == b.TrimEnd(),
        MatchMode.FullTrim => a.Trim() == b.Trim(),
        MatchMode.FoldTrim => Fold(a).Trim() == Fold(b).Trim(),
        _ => false,
    };

    // Folds common Unicode drift back to ASCII. Mirrors codex's canonicalization.
    static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                ' ' or ' ' or ' ' => ' ',           // non-breaking spaces
                '–' or '—' or '−' => '-',            // en/em dash, minus
                '‘' or '’' or '‚' or '′' => '\'', // curly single quotes, prime
                '“' or '”' or '„' or '″' => '"', // curly double quotes
                '…' => '.',                                    // ellipsis (first dot only)
                _ => c,
            });
        }
        return sb.ToString();
    }
}
