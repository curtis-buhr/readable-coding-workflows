namespace StockImport;

/// <summary>Controls whether accepted stock adjustments are applied or only evaluated.</summary>
public enum ImportMode
{
    /// <summary>Commits accepted rows after successful reading, even when other rows have issues.</summary>
    ApplyPartial,

    /// <summary>Applies accepted rows only after input ends without any row issues.</summary>
    ApplyAtomic,

    /// <summary>Evaluates rows against tentative stock without changing the supplied store.</summary>
    Preview
}
