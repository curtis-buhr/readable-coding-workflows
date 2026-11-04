using System.Runtime.InteropServices;
using System.Text.Json;
using StockImport;

var cases = new[]
{
    (Name: "ordinary", Delta: "+1"),
    (Name: "embedded-nul", Delta: "1\0" + "2"),
    (Name: "trailing-nul", Delta: "1\0")
};

var observations = new List<object>();
foreach (var test in cases)
{
    var store = new StockStore();
    var before = store.Snapshot();
    string row = $"op|sku|{test.Delta}";
    using var input = new StringReader(row + "\n");
    ImportResult result = StockImporter.Run(input, store);
    var after = store.Snapshot();

    using var retry = new StringReader("op|sku|1\n");
    ImportResult retryResult = StockImporter.Run(retry, store);
    observations.Add(new
    {
        test.Name,
        InputRow = row,
        Before = before,
        Result = result,
        After = after,
        RetryWithSameId = retryResult,
        AfterRetry = store.Snapshot()
    });
}

var report = new { Runtime = RuntimeInformation.FrameworkDescription, Cases = observations };
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
}));
