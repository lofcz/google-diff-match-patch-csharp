using static DiffMatchPatch.DiffOperation;

namespace DiffMatchPatch;

public static partial class PatchList
{

    internal static readonly string NullPadding = new string(Enumerable.Range(1, 4).Select(i => (char)i).ToArray());

    /// <summary>
    /// Add some padding on text start and end so that edges can match something.
    /// Intended to be called only from within patch_apply.
    /// </summary>
    /// <param name="patches"></param>
    /// <param name="patchMargin"></param>
    /// <returns>The padding string added to each side.</returns>
    internal static IEnumerable<Patch> AddPadding(this IEnumerable<Patch> patches, string padding)
    {
        int paddingLength = padding.Length;

        IEnumerator<Patch> enumerator = patches.GetEnumerator();

        if (!enumerator.MoveNext())
            yield break;

        Patch current = enumerator.Current.Bump(paddingLength);
        Patch next = current;
        bool isfirst = true;
        while (true)
        {
            bool hasnext = enumerator.MoveNext();
            if (hasnext)
                next = enumerator.Current.Bump(paddingLength);

            yield return (isfirst, hasnext) switch
            {
                (true, false) => current.AddPadding(padding), // list has only one patch
                (true, true) => current.AddPaddingInFront(padding),
                (false, true) => current,
                (false, false) => current.AddPaddingAtEnd(padding)
            };

            isfirst = false;
            if (!hasnext) yield break;

            current = next;
        }
    }


    /// <summary>
    /// Take a list of patches and return a textual representation.
    /// </summary>
    /// <param name="patches"></param>
    /// <returns></returns>
    public static string ToText(this IEnumerable<Patch> patches) => patches.Aggregate(new StringBuilder(), (sb, patch) => sb.Append(patch)).ToString();

    private static readonly Regex PatchHeader = PatchHeaderImpl();

    /// <summary>
    /// Parse a textual representation of patches and return a List of Patch
    /// objects.</summary>
    /// <param name="text"></param>
    /// <returns></returns>
    public static ImmutableList<Patch> Parse(string text) => ParseImpl(text).ToImmutableList();

    private static IEnumerable<Patch> ParseImpl(string text)
    {
        if (text.Length == 0)
        {
            yield break;
        }

        string[] lines = text.SplitBy('\n').ToArray();
        int index = 0;
        while (index < lines.Length)
        {
            string line = lines[index];
            Match m = PatchHeader.Match(line);
            if (!m.Success)
            {
                throw new ArgumentException("Invalid patch string: " + line);
            }

            (int start1, int length1) = m.GetStartAndLength(1, 2);
            (int start2, int length2) = m.GetStartAndLength(3, 4);

            index++;

            IEnumerable<Diff> CreateDiffs()
            {
                while (index < lines.Length)
                {
                    line = lines[index];
                    if (!string.IsNullOrEmpty(line))
                    {
                        char sign = line[0];
                        if (sign == '@') // Start of next patch.
                            break;
                        yield return sign switch
                        {
                            '+' => Diff.Insert(line[1..].Replace("+", "%2b").UrlDecoded()),
                            '-' => Diff.Delete(line[1..].Replace("+", "%2b").UrlDecoded()),
                            _ => Diff.Equal(line[1..].Replace("+", "%2b").UrlDecoded())
                        };

                    }
                    index++;
                }
            }


            yield return new Patch
            (
                start1,
                length1,
                start2,
                length2,
                CreateDiffs().ToImmutableList()
            );
        }
    }


    private static (int start, int length) GetStartAndLength(this Match m, int startIndex, int lengthIndex)
    {
        string lengthStr = m.Groups[lengthIndex].Value;
        int value = Convert.ToInt32(m.Groups[startIndex].Value);
        return lengthStr switch
        {
            "0" => (value, 0),
            "" => (value - 1, 1),
            _ => (value - 1, Convert.ToInt32(lengthStr))
        };
    }

    /// <summary>
    /// Merge a set of patches onto the text.  Return a patched text, as well
    /// as an array of true/false values indicating which patches were applied.</summary>
    /// <param name="patches"></param>
    /// <param name="text">Old text</param>
    /// <returns>Two element Object array, containing the new text and an array of
    ///  bool values.</returns>

    public static (string newText, bool[] results) Apply(this IEnumerable<Patch> patches, string text)
        => Apply(patches, text, MatchSettings.Default, PatchSettings.Default);


    public static (string newText, bool[] results) Apply(this IEnumerable<Patch> patches, string text, MatchSettings matchSettings)
        => Apply(patches, text, matchSettings, PatchSettings.Default);

    /// <summary>
    /// Merge a set of patches onto the text.  Return a patched text, as well
    /// as an array of true/false values indicating which patches were applied.</summary>
    /// <param name="patches"></param>
    /// <param name="text">Old text</param>
    /// <param name="matchSettings"></param>
    /// <param name="settings"></param>
    /// <returns>Two element Object array, containing the new text and an array of
    ///  bool values.</returns>
    public static (string newText, bool[] results) Apply(this IEnumerable<Patch> input, string text,
        MatchSettings matchSettings, PatchSettings settings)
    {
        List<Patch> list = input.ToList();
        
        if (list.Count is 0)
        {
            return (text, []);
        }

        string nullPadding = NullPadding;
        text = nullPadding + text + nullPadding;

        List<Patch> patches = list.AddPadding(nullPadding).SplitMax().ToList();

        int x = 0;
        // delta keeps track of the offset between the expected and actual
        // location of the previous patch.  If there are patches expected at
        // positions 10 and 20, but the first patch was found at 12, delta is 2
        // and the second patch has an effective expected position of 22.
        int delta = 0;
        bool[] results = new bool[patches.Count];
        foreach (Patch aPatch in patches)
        {
            int expectedLoc = aPatch.Start2 + delta;
            string text1 = aPatch.Diffs.Text1();
            int startLoc;
            int endLoc = -1;
            if (text1.Length > Constants.MatchMaxBits)
            {
                // patch_splitMax will only provide an oversized pattern
                // in the case of a monster delete.
                startLoc = text.FindBestMatchIndex(text1[..Constants.MatchMaxBits], expectedLoc, matchSettings);

                if (startLoc != -1)
                {
                    endLoc = text.FindBestMatchIndex(
                        text1[^Constants.MatchMaxBits..], expectedLoc + text1.Length - Constants.MatchMaxBits, matchSettings
                        );

                    if (endLoc == -1 || startLoc >= endLoc)
                    {
                        // Can't find valid trailing context.  Drop this patch.
                        startLoc = -1;
                    }
                }
            }
            else
            {
                startLoc = text.FindBestMatchIndex(text1, expectedLoc, matchSettings);
            }
            if (startLoc == -1)
            {
                // No match found.  :(
                results[x] = false;
                // Subtract the delta for this failed patch from subsequent patches.
                delta -= aPatch.Length2 - aPatch.Length1;
            }
            else
            {
                // Found a match.  :)
                results[x] = true;
                delta = startLoc - expectedLoc;
                int actualEndLoc = endLoc == -1 ? Math.Min(startLoc + text1.Length, text.Length) : Math.Min(endLoc + Constants.MatchMaxBits, text.Length);
                string text2 = text[startLoc..actualEndLoc];
                if (text1 == text2)
                {
                    // Perfect match, just shove the Replacement text in.
                    text = text[..startLoc] + aPatch.Diffs.Text2()
                           + text[(startLoc + text1.Length)..];
                }
                else
                {
                    // Imperfect match.  Run a diff to get a framework of equivalent
                    // indices.
                    ImmutableList<Diff> diffs = Diff.Compute(text1, text2, 0f, false);
                    if (text1.Length > Constants.MatchMaxBits
                        && diffs.Levenshtein() / (float)text1.Length
                        > settings.PatchDeleteThreshold)
                    {
                        // The end points match, but the content is unacceptably bad.
                        results[x] = false;
                    }
                    else
                    {
                        diffs = diffs.CleanupSemanticLossless().ToImmutableList();
                        int index1 = 0;
                        foreach (Diff aDiff in aPatch.Diffs)
                        {
                            if (aDiff.DiffOperation != Equal)
                            {
                                int index2 = diffs.FindEquivalentLocation2(index1);
                                text = aDiff.DiffOperation switch
                                {
                                    Insert => text.Insert(startLoc + index2, aDiff.Text),
                                    Delete => text.Remove(startLoc + index2, diffs.FindEquivalentLocation2(index1 + aDiff.Text.Length) - index2),
                                    _ => text
                                };
                            }
                            if (aDiff.DiffOperation != Delete)
                            {
                                index1 += aDiff.Text.Length;
                            }
                        }
                    }
                }
            }
            x++;
        }
        // Strip the padding off.
        text = text.Substring(nullPadding.Length, text.Length - 2 * nullPadding.Length);
        return (text, results);
    }

    /// <summary>
    /// Look through the patches and break up any which are longer than the
    /// maximum limit of the match algorithm.
    /// Intended to be called only from within patch_apply.
    ///  </summary>
    /// <param name="patches"></param>
    /// <param name="patchMargin"></param>
    internal static IEnumerable<Patch> SplitMax(this IEnumerable<Patch> patches, short patchMargin = 4)
    {
        const short patchSize = Constants.MatchMaxBits;
        foreach (Patch patch in patches)
        {
            if (patch.Length1 <= patchSize)
            {
                yield return patch;
                continue;
            }

            // Remove the big old patch.
            (int start1, _, int start2, _, ImmutableListWithValueSemantics<Diff> diffs) = patch;

            string precontext = string.Empty;
            while (!diffs.IsEmpty)
            {
                // Create one of several smaller patches.
                (int s1, int l1, int s2, int l2, List<Diff> thediffs)
                    = (start1 - precontext.Length, precontext.Length, start2 - precontext.Length, precontext.Length, []);

                bool empty = true;

                if (precontext.Length != 0)
                {
                    thediffs.Add(Diff.Equal(precontext));
                }
                while (!diffs.IsEmpty && l1 < patchSize - patchMargin)
                {
                    Diff first = diffs[0];
                    DiffOperation diffType = diffs[0].DiffOperation;
                    string diffText = diffs[0].Text;

                    if (first.DiffOperation == Insert)
                    {
                        // Insertions are harmless.
                        l2 += diffText.Length;
                        start2 += diffText.Length;
                        thediffs.Add(Diff.Insert(diffText));
                        diffs = diffs.RemoveAt(0);
                        empty = false;
                    }
                    else if (first.IsLargeDelete(2 * patchSize) && thediffs.Count == 1 && thediffs[0].DiffOperation == Equal)
                    {
                        // This is a large deletion.  Let it pass in one chunk.
                        l1 += diffText.Length;
                        start1 += diffText.Length;
                        thediffs.Add(Diff.Delete(diffText));
                        diffs = diffs.RemoveAt(0);
                        empty = false;
                    }
                    else
                    {
                        // Deletion or equality.  Only take as much as we can stomach.
                        string cutoff = diffText[..Math.Min(diffText.Length, patchSize - l1 - patchMargin)];
                        l1 += cutoff.Length;
                        start1 += cutoff.Length;
                        if (diffType == Equal)
                        {
                            l2 += cutoff.Length;
                            start2 += cutoff.Length;
                        }
                        else
                        {
                            empty = false;
                        }
                        thediffs.Add(Diff.Create(diffType, cutoff));
                        diffs = cutoff == first.Text ? diffs.RemoveAt(0) : diffs.RemoveAt(0).Insert(0, first with { Text = first.Text[cutoff.Length..] });
                    }
                }
                
                // Compute the head context for the next patch.
                precontext = thediffs.Text2();
                
                // if (thediffs.Text2() != precontext) throw new E
                precontext = precontext[Math.Max(0, precontext.Length - patchMargin)..];

                // Append the end context for this patch.
                string text1 = diffs.Text1();
                string postcontext = text1.Length > patchMargin ? text1[..patchMargin] : text1;

                if (postcontext.Length != 0)
                {
                    l1 += postcontext.Length;
                    l2 += postcontext.Length;
                    Diff lastDiff = thediffs.Last();
                    if (thediffs.Count > 0 && lastDiff.DiffOperation == Equal)
                        thediffs[^1] = lastDiff.Append(postcontext);
                    else
                        thediffs.Add(Diff.Equal(postcontext));
                }
                if (!empty)
                {
                    yield return new Patch(s1, l1, s2, l2, thediffs.ToImmutableList());
                }
            }
        }
    }

    [GeneratedRegex("^@@ -(\\d+),?(\\d*) \\+(\\d+),?(\\d*) @@$")]
    private static partial Regex PatchHeaderImpl();
}
