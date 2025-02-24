namespace DiffMatchPatch;

/// <summary>
/// Compatibility layer for https://github.com/google/diff-match-patch
/// </summary>
public partial class diff_match_patch
{
    public ImmutableList<Patch> patch_fromText(string textline)
    {
        return PatchList.Parse(textline);
    }

    public (string newText, bool[] results) patch_apply(IEnumerable<Patch> patches, string text)
    {
        return patches.Apply(text);
    }
}