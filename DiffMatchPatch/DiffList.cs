using static DiffMatchPatch.DiffOperation;

namespace DiffMatchPatch;

public static partial class DiffList
{
    /// <summary>
    /// Compute and return the source text (all equalities and deletions).
    /// </summary>
    /// <param name="diffs"></param>
    /// <returns></returns>
    public static string Text1(this IEnumerable<Diff> diffs)
        => diffs
        .Where(d => d.DiffOperation != Insert)
        .Aggregate(new StringBuilder(), (sb, diff) => sb.Append(diff.Text))
        .ToString();

    /// <summary>
    /// Compute and return the destination text (all equalities and insertions).
    /// </summary>
    /// <param name="diffs"></param>
    /// <returns></returns>
    public static string Text2(this IEnumerable<Diff> diffs)
        => diffs
        .Where(d => d.DiffOperation != Delete)
        .Aggregate(new StringBuilder(), (sb, diff) => sb.Append(diff.Text))
        .ToString();

    private readonly record struct LevenshteinState(int Insertions, int Deletions, int Levenshtein)
    {
        public LevenshteinState Consolidate() => new LevenshteinState(0, 0, Levenshtein + Math.Max(Insertions, Deletions));
    }

    /// <summary>
    /// Compute the Levenshtein distance; the number of inserted, deleted or substituted characters.
    /// </summary>
    /// <param name="diffs"></param>
    /// <returns></returns>
    internal static int Levenshtein(this IEnumerable<Diff> diffs)
    {
        LevenshteinState state = new LevenshteinState(0, 0, 0);
        foreach (Diff aDiff in diffs)
        {
            state = aDiff.DiffOperation switch
            {
                Insert => state with { Insertions = state.Insertions + aDiff.Text.Length },
                Delete => state with { Deletions = state.Deletions + aDiff.Text.Length },
                Equal => state.Consolidate(),
                _ => throw new IndexOutOfRangeException()
            };
        }
        return state.Consolidate().Levenshtein;
    }
    private static StringBuilder AppendHtml(this StringBuilder sb, string tag, string backgroundColor, string content)
        => sb
        .Append(string.IsNullOrEmpty(backgroundColor) ? $"<{tag}>" : $"<{tag} style=\"background:{backgroundColor};\">")
        .Append(content)
        .Append($"</{tag}>");

    private static StringBuilder AppendHtml(this StringBuilder sb, DiffOperation diffOperation, string text) => diffOperation switch
    {
        Insert => sb.AppendHtml("ins", "#e6ffe6", text),
        Delete => sb.AppendHtml("del", "#ffe6e6", text),
        Equal => sb.AppendHtml("span", "", text),
        _ => throw new IndexOutOfRangeException()
    };

    /// <summary>
    /// Convert a Diff list into a pretty HTML report.
    /// </summary>
    /// <param name="diffs"></param>
    /// <returns></returns>
    public static string PrettyHtml(this IEnumerable<Diff> diffs) => diffs
        .Aggregate(new StringBuilder(), (sb, diff) => sb.AppendHtml(diff.DiffOperation, diff.Text.HtmlEncodeLight()))
        .ToString();

    private static string HtmlEncodeLight(this string s)
    {
        string text = new StringBuilder(s)
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\n", "&para;<br>")
            .ToString();
        return text;
    }

    private static char ToDelta(this DiffOperation o) => o switch
    {
        Delete => '-',
        Insert => '+',
        Equal => '=',
        _ => throw new ArgumentException($"Unknown Operation: {o}")
    };

    private static DiffOperation FromDelta(char c) => c switch
    {
        '-' => Delete,
        '+' => Insert,
        '=' => Equal,
        _ => throw new ArgumentException($"Invalid Delta Token: {c}")
    };

    /// <summary>
    /// Crush the diff into an encoded string which describes the operations
    /// required to transform text1 into text2.
    /// E.g. =3\t-2\t+ing  -> Keep 3 chars, delete 2 chars, insert 'ing'.
    /// Operations are tab-separated.  Inserted text is escaped using %xx
    /// notation.
    /// </summary>
    /// <param name="diffs"></param>
    /// <returns></returns>
    public static string ToDelta(this IEnumerable<Diff> diffs)
    {
        IEnumerable<string> s =
            from aDiff in diffs
            let sign = aDiff.DiffOperation.ToDelta()
            let textToAppend = aDiff.DiffOperation == Insert
                ? aDiff.Text.UrlEncoded()
                : aDiff.Text.Length.ToString()
            select string.Concat(sign, textToAppend);

        string delta = string.Join("\t", s);
        return delta;
    }

    /// <summary>
    /// Given the original text1, and an encoded string which describes the
    /// operations required to transform text1 into text2, compute the full diff.
    /// </summary>
    /// <param name="text1">Source string for the diff.</param>
    /// <param name="delta">Delta text.</param>
    /// <returns></returns>
    public static IEnumerable<Diff> FromDelta(string text1, string delta)
    {
        int pointer = 0;  // Cursor in text1

        foreach (string token in delta.SplitBy('\t'))
        {
            if (token.Length == 0)
            {
                // Blank tokens are ok (from a trailing \t).
                continue;
            }
            // Each token begins with a one character parameter which specifies the
            // operation of this token (delete, insert, equality).
            string param = token[1..];
            DiffOperation diffOperation = FromDelta(token[0]);
            int n = 0;
            if (diffOperation != Insert)
            {
                if (!int.TryParse(param, out n))
                {
                    throw new ArgumentException($"Invalid number in Diff.FromDelta: {param}");
                }
                if (pointer > text1.Length - n)
                {
                    throw new ArgumentException($"Delta length ({pointer}) larger than source text length ({text1.Length}).");
                }
            }

            (string text, pointer) = diffOperation switch
            {
                Insert => (param.Replace("+", "%2b").UrlDecoded(), pointer),
                Equal => (text1.Substring(pointer, n), pointer + n),
                Delete => (text1.Substring(pointer, n), pointer + n),
                _ => throw new ArgumentException($"Unknown Operation: {diffOperation}")
            };
            yield return Diff.Create(diffOperation, text);
        }
        if (pointer != text1.Length)
        {
            throw new ArgumentException($"Delta length ({pointer}) smaller than source text length ({text1.Length}).");
        }
    }

    internal static IEnumerable<Diff> CleanupMergePass1(this IEnumerable<Diff> diffs)
    {
        StringBuilder sbDelete = new StringBuilder();
        StringBuilder sbInsert = new StringBuilder();

        Diff lastEquality = Diff.Empty;

        IEnumerator<Diff> enumerator = diffs.Concat(Diff.Empty).GetEnumerator();
        while (enumerator.MoveNext())
        {
            Diff diff = enumerator.Current;

            (sbInsert, sbDelete) = diff.DiffOperation switch
            {
                Insert => (sbInsert.Append(diff.Text), sbDelete),
                Delete => (sbInsert, sbDelete.Append(diff.Text)),
                _ => (sbInsert, sbDelete)
            };

            switch (diff.DiffOperation)
            {
                case Equal:
                    // Upon reaching an equality, check for prior redundancies.
                    if (sbInsert.Length > 0 || sbDelete.Length > 0)
                    {
                        // first equality after number of inserts/deletes
                        // Factor out any common prefixies.
                        int prefixLength = TextUtil.CommonPrefix(sbInsert, sbDelete);
                        if (prefixLength > 0)
                        {
                            string commonprefix = sbInsert.ToString(0, prefixLength);
                            sbInsert.Remove(0, prefixLength);
                            sbDelete.Remove(0, prefixLength);
                            lastEquality = lastEquality.Append(commonprefix);
                        }

                        // Factor out any common suffixies.
                        int suffixLength = TextUtil.CommonSuffix(sbInsert, sbDelete);
                        if (suffixLength > 0)
                        {
                            string commonsuffix = sbInsert.ToString(sbInsert.Length - suffixLength, suffixLength);
                            sbInsert.Remove(sbInsert.Length - suffixLength, suffixLength);
                            sbDelete.Remove(sbDelete.Length - suffixLength, suffixLength);
                            diff = diff.Prepend(commonsuffix);
                        }

                        // Delete the offending records and add the merged ones.
                        if (!lastEquality.IsEmpty)
                        {
                            yield return lastEquality;
                        }
                        if (sbDelete.Length > 0) yield return Diff.Delete(sbDelete.ToString());
                        if (sbInsert.Length > 0) yield return Diff.Insert(sbInsert.ToString());
                        lastEquality = diff;
                        sbDelete.Clear();
                        sbInsert.Clear();
                    }
                    else
                    {
                        // Merge this equality with the previous one.
                        lastEquality = lastEquality.Append(diff.Text);
                    }
                    break;
            }
        }
        if (!lastEquality.IsEmpty)
            yield return lastEquality;
    }

    internal static IEnumerable<Diff> CleanupMergePass2(this IEnumerable<Diff> input, out bool haschanges)
    {
        haschanges = false;
        // Second pass: look for single edits surrounded on both sides by
        // equalities which can be shifted sideways to eliminate an equality.
        // e.g: A<ins>BA</ins>C -> <ins>AB</ins>AC
        List<Diff> diffs = input.ToList();
        // Intentionally ignore the first and last element (don't need checking).
        for (int i = 1; i < diffs.Count - 1; i++)
        {
            Diff previous = diffs[i - 1];
            Diff current = diffs[i];
            Diff next = diffs[i + 1];
            if (previous.DiffOperation == Equal && next.DiffOperation == Equal)
            {
                ReadOnlySpan<char> currentSpan = current.Text.AsSpan();
                ReadOnlySpan<char> previousSpan = previous.Text.AsSpan();
                ReadOnlySpan<char> nextSpan = next.Text.AsSpan();

                // This is a single edit surrounded by equalities.
                if (currentSpan.Length >= previousSpan.Length &&
                    currentSpan[^previousSpan.Length..].SequenceEqual(previousSpan))
                {
                    // Shift the edit over the previous equality.
                    string text = previous.Text + current.Text[..^previous.Text.Length];
                    diffs[i] = current.Replace(text);
                    diffs[i + 1] = next.Replace(previous.Text + next.Text);
                    diffs.Splice(i - 1, 1);
                    haschanges = true;
                }
                else if (currentSpan.Length >= nextSpan.Length &&
                         currentSpan[..nextSpan.Length].SequenceEqual(nextSpan))
                {
                    // Shift the edit over the next equality.
                    diffs[i - 1] = previous.Replace(previous.Text + next.Text);
                    diffs[i] = current.Replace(current.Text[next.Text.Length..] + next.Text);
                    diffs.Splice(i + 1, 1);
                    haschanges = true;
                }
            }
        }
        return diffs;
    }

    /// <summary>
    /// Reorder and merge like edit sections.  Merge equalities.
    /// Any edit section can move as long as it doesn't cross an equality.
    /// </summary>
    /// <param name="diffs">list of Diffs</param>
    internal static IEnumerable<Diff> CleanupMerge(this IEnumerable<Diff> diffs)
    {
        bool changes;
        do
        {
            diffs = diffs
                .CleanupMergePass1()
                .CleanupMergePass2(out changes)
                .ToList(); // required to detect if anything changed
        } while (changes);

        return diffs;
    }


    private readonly record struct EditBetweenEqualities(string Equality1, string Edit, string Equality2)
    {
        public int Score => DiffCleanupSemanticScore(Equality1, Edit) + DiffCleanupSemanticScore(Edit, Equality2);

        private readonly record struct ScoreHelper(string Str, Index I, Regex Regex)
        {
            private char C => Str[I];
            public bool IsEmpty => Str.Length == 0;
            public bool NonAlphaNumeric => !char.IsLetterOrDigit(C);
            public bool IsWhitespace => char.IsWhiteSpace(C);
            public bool IsLineBreak => C == '\n' || C == '\r';
            public bool IsBlankLine => IsLineBreak && Regex.IsMatch(Str);
        }

        /// Given two strings, computes a score representing whether the internal boundary falls on logical boundaries.
        /// higher is better
        private static int DiffCleanupSemanticScore(string one, string two)
            => (h1: new ScoreHelper(one, ^1, BlankLineEnd), h2: new ScoreHelper(two, 0, BlankLineStart)) switch
            {
                { h1.IsEmpty: true } or { h2.IsEmpty: true } => 6,
                { h1.IsBlankLine: true } or { h2.IsBlankLine: true } => 5,
                { h1.IsLineBreak: true } or { h2.IsLineBreak: true } => 4,
                { h1.NonAlphaNumeric: true } and { h1.IsWhitespace: false } and { h2.IsWhitespace: true } => 3,
                { h1.IsWhitespace: true } or { h2.IsWhitespace: true } => 2,
                { h1.NonAlphaNumeric: true } or { h2.NonAlphaNumeric: true } => 1,
                _ => 0
            };

        // Shift the edit as far left as possible.
        public EditBetweenEqualities ShiftLeft()
        {
            int commonOffset = TextUtil.CommonSuffix(Equality1, Edit);

            if (commonOffset > 0)
            {
                string? commonString = Edit[^commonOffset..];
                string? equality1 = Equality1[..^commonOffset];
                string edit = commonString + Edit[..^commonOffset];
                string equality2 = commonString + Equality2;
                return new EditBetweenEqualities(Equality1: equality1, Edit: edit, Equality2: equality2);
            }

            return this;
        }

        // Shift one right
        private EditBetweenEqualities ShiftRight() => new EditBetweenEqualities(Equality1: Equality1 + Edit[0], Edit: Edit[1..] + Equality2[0], Equality2: Equality2[1..]);

        public IEnumerable<EditBetweenEqualities> TraverseRight()
        {
            EditBetweenEqualities item = this;
            while (item.Edit.Length != 0 && item.Equality2.Length != 0 && item.Edit[0] == item.Equality2[0])
            {
                yield return item = item.ShiftRight();
            }
        }

        public IEnumerable<Diff> ToDiffs(DiffOperation edit)
        {
            yield return Diff.Equal(Equality1);
            yield return Diff.Create(edit, Edit);
            yield return Diff.Equal(Equality2);
        }
    }

    /// <summary>
    /// Look for single edits surrounded on both sides by equalities
    /// which can be shifted sideways to align the edit to a word boundary.
    /// e.g: The c<ins>at c</ins>ame. -> The <ins>cat </ins>came.
    /// </summary>
    /// <param name="diffs"></param>
    internal static IEnumerable<Diff> CleanupSemanticLossless(this IEnumerable<Diff> diffs)
    {
        IEnumerator<Diff> enumerator = diffs.GetEnumerator();

        if (!enumerator.MoveNext()) yield break;

        Diff previous = enumerator.Current;

        if (!enumerator.MoveNext())
        {
            yield return previous;
            yield break;
        }

        Diff current = enumerator.Current;

        while (true)
        {
            if (!enumerator.MoveNext())
            {
                yield return previous;
                yield return current;
                yield break;
            }

            Diff next = enumerator.Current;

            if (previous.DiffOperation == Equal && next.DiffOperation == Equal)
            {
                // This is a single edit surrounded by equalities.
                EditBetweenEqualities item = new EditBetweenEqualities(previous.Text, current.Text, next.Text).ShiftLeft();

                // Second, step character by character right, looking for the best fit.
                EditBetweenEqualities best = item.TraverseRight().Aggregate(item, (best, x) => best.Score > x.Score ? best : x);

                if (previous.Text != best.Equality1)
                {
                    // We have an improvement; yield the improvement instead of the original diffs
                    foreach (Diff d in best.ToDiffs(current.DiffOperation).Where(d => !d.IsEmpty))
                        yield return d;

                    if (!enumerator.MoveNext())
                        yield break;

                    current = next;
                    next = enumerator.Current;
                }
                else
                {
                    yield return previous;
                }
            }
            else
            {
                yield return previous;
            }

            previous = current;
            current = next;
        }
    }

    // Define some regex patterns for matching boundaries.
    private static readonly Regex BlankLineEnd = BlankLineEndImpl();
    private static readonly Regex BlankLineStart = BlankLineStartImpl();

    /// <summary>
    /// Reduce the number of edits by eliminating operationally trivial equalities.
    /// </summary>
    /// <param name="input"></param>
    /// <param name="diffEditCost"></param>
    internal static IEnumerable<Diff> CleanupEfficiency(this IEnumerable<Diff> input, short diffEditCost = 4)
    {
        List<Diff> diffs = input.ToList();
        bool changes = false;
        // Stack of indices where equalities are found.
        Stack<int> equalities = new Stack<int>();
        // Always equal to equalities[equalitiesLength-1][1]
        string lastEquality = string.Empty;
        // Is there an insertion operation before the last equality.
        bool insertionBeforeLastEquality = false;
        // Is there a deletion operation before the last equality.
        bool deletionBeforeLastEquality = false;
        // Is there an insertion operation after the last equality.
        bool insertionAfterLastEquality = false;
        // Is there a deletion operation after the last equality.
        bool deletionAfterLastEquality = false;

        for (int i = 0; i < diffs.Count; i++)
        {
            Diff diff = diffs[i];
            if (diff.DiffOperation == Equal)
            {  // Equality found.
                if (diff.Text.Length < diffEditCost && (insertionAfterLastEquality || deletionAfterLastEquality))
                {
                    // Candidate found.
                    equalities.Push(i);
                    (insertionBeforeLastEquality, deletionBeforeLastEquality) = (insertionAfterLastEquality, deletionAfterLastEquality);
                    lastEquality = diff.Text;
                }
                else
                {
                    // Not a candidate, and can never become one.
                    equalities.Clear();
                    lastEquality = string.Empty;
                }
                insertionAfterLastEquality = deletionAfterLastEquality = false;
            }
            else
            {  // An insertion or deletion.
                if (diff.DiffOperation == Delete)
                {
                    deletionAfterLastEquality = true;
                }
                else
                {
                    insertionAfterLastEquality = true;
                }
                /*
                 * Five types to be split:
                 * <ins>A</ins><del>B</del>XY<ins>C</ins><del>D</del>
                 * <ins>A</ins>X<ins>C</ins><del>D</del>
                 * <ins>A</ins><del>B</del>X<ins>C</ins>
                 * <ins>A</del>X<ins>C</ins><del>D</del>
                 * <ins>A</ins><del>B</del>X<del>C</del>
                 */
                if ((lastEquality.Length != 0)
                    && ((insertionBeforeLastEquality && deletionBeforeLastEquality && insertionAfterLastEquality && deletionAfterLastEquality)
                        || ((lastEquality.Length < diffEditCost / 2)
                            && (insertionBeforeLastEquality ? 1 : 0) + (deletionBeforeLastEquality ? 1 : 0) + (insertionAfterLastEquality ? 1 : 0)
                            + (deletionAfterLastEquality ? 1 : 0) == 3)))
                {
                    // replace equality by delete/insert
                    diffs.Splice(equalities.Peek(), 1, Diff.Delete(lastEquality), Diff.Insert(lastEquality));
                    equalities.Pop();  // Throw away the equality we just deleted.
                    lastEquality = string.Empty;
                    if (insertionBeforeLastEquality && deletionBeforeLastEquality)
                    {
                        // No changes made which could affect previous entry, keep going.
                        insertionAfterLastEquality = deletionAfterLastEquality = true;
                        equalities.Clear();
                    }
                    else
                    {
                        if (equalities.Count > 0)
                        {
                            equalities.Pop();
                        }

                        i = equalities.Count > 0 ? equalities.Peek() : -1;
                        insertionAfterLastEquality = deletionAfterLastEquality = false;
                    }
                    changes = true;
                }
            }
        }

        return changes ? diffs.CleanupMerge() : diffs;
    }
    /// <summary>
    /// A diff of two unrelated texts can be filled with coincidental matches. 
    /// For example, the diff of "mouse" and "sofas" is 
    /// `[(-1, "m"), (1, "s"), (0, "o"), (-1, "u"), (1, "fa"), (0, "s"), (-1, "e")]`. 
    /// While this is the optimum diff, it is difficult for humans to understand. Semantic 
    /// cleanup rewrites the diff, expanding it into a more intelligible format. The above 
    /// example would become: `[(-1, "mouse"), (1, "sofas")]`.
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static IImmutableList<Diff> MakeHumanReadable(this IEnumerable<Diff> input) => input.CleanupSemantic().ToImmutableList();
    /// <summary>
    /// This function is similar to `OptimizeForReadability`, except that instead of optimising a diff 
    /// to be human-readable, it optimises the diff to be efficient for machine processing. The results 
    /// of both cleanup types are often the same.
    /// The efficiency cleanup is based on the observation that a diff made up of large numbers of 
    /// small diffs edits may take longer to process(in downstream applications) or take more capacity 
    /// to store or transmit than a smaller number of larger diffs.
    /// </summary>
    /// <param name="input"></param>
    /// <param name="diffEditCost">The cost of handling a new edit in terms of handling extra characters in an existing edit. 
    /// The default value is 4, which means if expanding the length of a diff by three characters can eliminate one edit, 
    /// then that optimisation will reduce the total costs</param>
    /// <returns></returns>
    public static IImmutableList<Diff> OptimizeForMachineProcessing(this IEnumerable<Diff> input, short diffEditCost = 4) => input.CleanupEfficiency(diffEditCost).ToImmutableList();

    /// <summary>
    /// Reduce the number of edits by eliminating semantically trivial equalities.
    /// </summary>
    /// <param name="input"></param>
    internal static List<Diff> CleanupSemantic(this IEnumerable<Diff> input)
    {
        List<Diff> diffs = input.ToList();
        // Stack of indices where equalities are found.
        Stack<int> equalities = new Stack<int>();
        // Always equal to equalities[equalitiesLength-1][1]
        string? lastEquality = null;
        int pointer = 0;  // Index of current position.
                          // Number of characters that changed prior to the equality.
        int lengthInsertions1 = 0;
        int lengthDeletions1 = 0;
        // Number of characters that changed after the equality.
        int lengthInsertions2 = 0;
        int lengthDeletions2 = 0;
        while (pointer < diffs.Count)
        {
            if (diffs[pointer].DiffOperation == Equal)
            {  // Equality found.
                equalities.Push(pointer);
                lengthInsertions1 = lengthInsertions2;
                lengthDeletions1 = lengthDeletions2;
                lengthInsertions2 = 0;
                lengthDeletions2 = 0;
                lastEquality = diffs[pointer].Text;
            }
            else
            {  // an insertion or deletion
                if (diffs[pointer].DiffOperation == Insert)
                {
                    lengthInsertions2 += diffs[pointer].Text.Length;
                }
                else
                {
                    lengthDeletions2 += diffs[pointer].Text.Length;
                }
                // Eliminate an equality that is smaller or equal to the edits on both
                // sides of it.
                if (lastEquality != null
                    && (lastEquality.Length <= Math.Max(lengthInsertions1, lengthDeletions1))
                    && (lastEquality.Length <= Math.Max(lengthInsertions2, lengthDeletions2)))
                {
                    // Duplicate record.

                    diffs.Splice(equalities.Peek(), 1, Diff.Delete(lastEquality), Diff.Insert(lastEquality));

                    // Throw away the equality we just deleted.
                    equalities.Pop();
                    if (equalities.Count > 0)
                    {
                        equalities.Pop();
                    }
                    pointer = equalities.Count > 0 ? equalities.Peek() : -1;
                    lengthInsertions1 = 0;  // Reset the counters.
                    lengthDeletions1 = 0;
                    lengthInsertions2 = 0;
                    lengthDeletions2 = 0;
                    lastEquality = null;
                }
            }
            pointer++;
        }

        diffs = diffs.CleanupMerge().CleanupSemanticLossless().ToList();

        // Find any overlaps between deletions and insertions.
        // e.g: <del>abcxxx</del><ins>xxxdef</ins>
        //   -> <del>abc</del>xxx<ins>def</ins>
        // e.g: <del>xxxabc</del><ins>defxxx</ins>
        //   -> <ins>def</ins>xxx<del>abc</del>
        // Only extract an overlap if it is as big as the edit ahead or behind it.
        pointer = 1;
        while (pointer < diffs.Count)
        {
            if (diffs[pointer - 1].DiffOperation == Delete &&
                diffs[pointer].DiffOperation == Insert)
            {
                ReadOnlySpan<char> deletion = diffs[pointer - 1].Text.AsSpan();
                ReadOnlySpan<char> insertion = diffs[pointer].Text.AsSpan();
                int overlapLength1 = TextUtil.CommonOverlap(deletion, insertion);
                int overlapLength2 = TextUtil.CommonOverlap(insertion, deletion);
                int minLength = Math.Min(deletion.Length, insertion.Length);

                Diff[]? newdiffs = null;
                if ((overlapLength1 >= overlapLength2) && (overlapLength1 >= minLength / 2.0))
                {
                    // Overlap found.
                    // Insert an equality and trim the surrounding edits.
                    newdiffs =
                    [
                        Diff.Delete(deletion[..^overlapLength1].ToArray()),
                            Diff.Equal(insertion[..overlapLength1].ToArray()),
                            Diff.Insert(insertion[overlapLength1..].ToArray())
                    ];
                }
                else if ((overlapLength2 >= overlapLength1) && overlapLength2 >= minLength / 2.0)
                {
                    // Reverse overlap found.
                    // Insert an equality and swap and trim the surrounding edits.
                    newdiffs =
                    [
                        Diff.Insert(insertion[..^overlapLength2]),
                                Diff.Equal(deletion[..overlapLength2]),
                                Diff.Delete(deletion[overlapLength2..])
                    ];
                }

                if (newdiffs != null)
                {
                    diffs.Splice(pointer - 1, 2, newdiffs);
                    pointer++;
                }

                pointer++;
            }
            pointer++;
        }
        return diffs;
    }


    /// <summary>
    /// Rehydrate the text in a diff from a string of line hashes to real lines of text.
    /// </summary>
    /// <param name="diffs"></param>
    /// <param name="lineArray">list of unique strings</param>
    /// <returns></returns>
    internal static IEnumerable<Diff> CharsToLines(this ICollection<Diff> diffs, IList<string> lineArray)
    {
        foreach (Diff diff in diffs)
        {
            StringBuilder text = new StringBuilder();
            foreach (char c in diff.Text)
            {
                text.Append(lineArray[c]);
            }
            yield return diff.Replace(text.ToString());
        }
    }

    /// <summary>
    /// Compute and return equivalent location in target text.
    /// </summary>
    /// <param name="diffs">list of diffs</param>
    /// <param name="location1">location in source</param>
    /// <returns>location in target</returns>
    internal static int FindEquivalentLocation2(this IEnumerable<Diff> diffs, int location1)
    {
        int chars1 = 0;
        int chars2 = 0;
        int lastChars1 = 0;
        int lastChars2 = 0;
        Diff lastDiff = Diff.Empty;
        foreach (Diff aDiff in diffs)
        {
            if (aDiff.DiffOperation != Insert)
            {
                // Equality or deletion.
                chars1 += aDiff.Text.Length;
            }
            if (aDiff.DiffOperation != Delete)
            {
                // Equality or insertion.
                chars2 += aDiff.Text.Length;
            }
            if (chars1 > location1)
            {
                // Overshot the location.
                lastDiff = aDiff;
                break;
            }
            lastChars1 = chars1;
            lastChars2 = chars2;
        }
        if (lastDiff.DiffOperation == Delete)
        {
            // The location was deleted.
            return lastChars2;
        }
        // Add the remaining character length.
        return lastChars2 + (location1 - lastChars1);
    }

    [GeneratedRegex("\\n\\r?\\n\\Z", RegexOptions.Compiled)]
    private static partial Regex BlankLineEndImpl();
    [GeneratedRegex("\\A\\r?\\n\\r?\\n", RegexOptions.Compiled)]
    private static partial Regex BlankLineStartImpl();
}
