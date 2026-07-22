namespace AIVTuber.Core.Avatar;

/// <summary>
/// Head-layer Y follow for breath ScaleY about the foot pivot.
/// Feathering / seam soft edges live in the head PNG; callers must not edge-process.
/// </summary>
public static class LayeredBreathFollow
{
    /// <summary>
    /// WPF <c>TranslateTransform.Y</c> (+down) that keeps the cut seated on the body
    /// when the body scales about <paramref name="pivotY"/>.
    /// Formula: <c>-(pivot_y - cut_y) × (scaleY - 1)</c> — by cut height, not body top / canvas bottom.
    /// </summary>
    public static double HeadTranslateY(double pivotY, double cutY, double scaleY)
        => -(pivotY - cutY) * (scaleY - 1.0);
}
