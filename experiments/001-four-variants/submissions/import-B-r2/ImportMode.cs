namespace StockImport;

/// <summary>Controls when accepted stock adjustments reach the supplied store.</summary>
public enum ImportMode
{
    /// <summary>Applies each accepted row immediately, even if later rows fail.</summary>
    ApplyPartial,

    /// <summary>Applies accepted rows only after reading completes without any issues.</summary>
    ApplyAtomic,

    /// <summary>Evaluates rows against tentative stock without updating the supplied store.</summary>
    Preview
}
