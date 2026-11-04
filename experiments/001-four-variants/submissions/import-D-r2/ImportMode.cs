namespace StockImport;

/// <summary>Controls whether accepted stock adjustments are applied or only evaluated.</summary>
public enum ImportMode
{
    /// <summary>Applies each accepted row immediately, retaining changes if a later row fails.</summary>
    ApplyPartial,

    /// <summary>Applies accepted rows only after input ends without any row issues.</summary>
    ApplyAtomic,

    /// <summary>Evaluates rows against tentative stock without changing the supplied store.</summary>
    Preview
}
