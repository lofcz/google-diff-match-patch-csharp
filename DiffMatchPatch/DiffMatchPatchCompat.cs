namespace DiffMatchPatch;

/// <summary>
/// Compatibility layer for https://github.com/google/diff-match-patch
/// </summary>
public partial class diff_match_patch
{
    /// <summary>
    /// Parse a textual representation of patches and return a List of Patch
    /// objects.</summary>
    /// <param name="textline"></param>
    /// <returns></returns>
    public ImmutableList<Patch> patch_fromText(string textline)
    {
        return PatchList.Parse(textline);
    }

    /// <summary>
    /// Merge a set of patches onto the text.  Return a patched text, as well
    /// as an array of true/false values indicating which patches were applied.</summary>
    /// <param name="patches"></param>
    /// <param name="text">Old text</param>
    /// <returns>Two element Object array, containing the new text and an array of
    ///  bool values.</returns>
    public (string newText, bool[] results) patch_apply(IEnumerable<Patch> patches, string text)
    {
        return patches.Apply(text);
    }
}