namespace StockImport;

public enum ImportMode
{
    /// <summary>Apply accepted rows after reading completes, even if other rows have issues.</summary>
    ApplyPartial,

    /// <summary>Apply accepted rows only after input ends without any issues.</summary>
    ApplyAtomic,

    /// <summary>Evaluate rows against tentative quantities without changing the supplied store.</summary>
    Preview
}
