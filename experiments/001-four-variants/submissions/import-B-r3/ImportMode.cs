namespace StockImport;

/// <summary>Controls when accepted stock adjustments reach the supplied store.</summary>
public enum ImportMode
{
    /// <summary>Commits accepted rows after reading completes, even when other rows have issues.</summary>
    ApplyPartial,

    /// <summary>Applies accepted rows only after reading completes without any issues.</summary>
    ApplyAtomic,

    /// <summary>Evaluates rows against tentative stock without updating the supplied store.</summary>
    Preview
}
