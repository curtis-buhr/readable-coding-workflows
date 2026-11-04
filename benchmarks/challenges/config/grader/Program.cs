using System.Collections;
using System.Text.Json;
using DeploymentProfiles;

var cases = new List<CaseResult>();

void Run(string id, int introduced, Action check)
{
    try
    {
        check();
        cases.Add(new CaseResult(id, introduced, true, null));
    }
    catch (Exception error)
    {
        cases.Add(new CaseResult(id, introduced, false, $"{error.GetType().Name}: {error.Message}"));
    }
}

void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}; received {actual}.");
}

void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

const string legacy = """{"endpoint":"https://example.test/api"}""";
const string custom = """{"version":1,"endpoint":"http://gateway.test/probe","timeoutMs":2413,"retries":2,"enabled":false,"future":{"x":1}}""";
const string modern = """{"version":2,"transport":{"endpoint":"https://v2.test/check","timeoutMs":800},"retry":{"count":2},"enabled":true}""";

Run("legacy.defaults", 0, () => Equal(new ServiceProfile("https://example.test/api", 5000, 0, true), ProfileCodec.Parse(legacy)));
Run("legacy.explicit-fields", 0, () => Equal(new ServiceProfile("http://gateway.test/probe", 2413, 2, false), ProfileCodec.Parse(custom)));
Run("legacy.disabled-adapter", 0, () => Equal<DeploymentRequest?>(null, DeploymentAdapter.Create(custom)));
Run("legacy.adapter-timeout-unit", 0, () =>
    Equal(new DeploymentRequest("https://example.test/api/health", TimeSpan.FromMilliseconds(5000), 0), DeploymentAdapter.Create(legacy)));
Run("legacy.malformed", 0, () =>
{
    foreach (var json in new[] { null!, "", "[]", "{", """{"endpoint":5}""", """{"endpoint":null}""", """{"Endpoint":"https://example.test"}""" })
        Throws<FormatException>(() => ProfileCodec.Parse(json));
});
Run("legacy.field-bounds", 0, () =>
{
    foreach (var json in new[]
    {
        """{"endpoint":"https://example.test","timeoutMs":0}""",
        """{"endpoint":"https://example.test","timeoutMs":60001}""",
        """{"endpoint":"https://example.test","retries":6}""",
        """{"endpoint":"https://example.test","retries":-1}""",
        """{"endpoint":"https://example.test","timeoutMs":2147483648}""",
        """{"endpoint":"https://example.test","enabled":0}"""
    })
        Throws<FormatException>(() => ProfileCodec.Parse(json));
});
Run("legacy.invalid-endpoints", 0, () =>
{
    Throws<FormatException>(() => ProfileCodec.Parse("""{"endpoint":"relative/path"}"""));
    Throws<FormatException>(() => ProfileCodec.Parse("""{"endpoint":"ftp://x.test/p"}"""));
});
Run("legacy.unsupported-version", 0, () =>
{
    foreach (var json in new[]
    {
        """{"version":17,"endpoint":"https://example.test"}""",
        """{"version":null,"endpoint":"https://example.test"}""",
        """{"version":"1","endpoint":"https://example.test"}"""
    })
        Throws<FormatException>(() => ProfileCodec.Parse(json));
});

#if REV1
Run("migration.v2-parsing", 1, () => Equal(new ServiceProfile("https://v2.test/check", 800, 2, true), ProfileCodec.Parse(modern)));
Run("migration.v2-defaults-and-unknowns", 1, () =>
    Equal(new ServiceProfile("https://v2.test", 5000, 0, true),
        ProfileCodec.Parse("""{"version":2,"transport":{"endpoint":"https://v2.test","extra":42},"extra":[],"retry":{}}""")));
Run("migration.v2-schema-errors", 1, () =>
{
    foreach (var json in new[]
    {
        """{"version":2,"endpoint":"https://v2.test"}""",
        """{"version":2,"transport":[]}""",
        """{"version":2,"transport":{"endpoint":"https://v2.test","timeoutMs":null}}""",
        """{"version":2,"transport":{"endpoint":"https://v2.test"},"retry":null}""",
        """{"version":2,"transport":{"endpoint":"https://v2.test"},"retry":{"count":false}}""",
        """{"version":2,"transport":{"endpoint":"https://v2.test"},"retry":{"count":7}}""",
        """{"version":2,"transport":{"endpoint":"https://v2.test","timeoutMs":2.5}}"""
    })
        Throws<FormatException>(() => ProfileMigrator.ToVersion2(json));
});
Run("migration.canonical-roundtrip", 1, () =>
{
    var json = ProfileMigrator.ToVersion2(custom);
    Equal(json, ProfileMigrator.ToVersion2(json));
    Equal(ProfileCodec.Parse(custom), ProfileCodec.Parse(json));
    using var document = JsonDocument.Parse(json);
    Equal("version,transport,retry,enabled", string.Join(",", document.RootElement.EnumerateObject().Select(property => property.Name)));
    Equal("endpoint,timeoutMs", string.Join(",", document.RootElement.GetProperty("transport").EnumerateObject().Select(property => property.Name)));
});
Run("migration.v2-adapter", 1, () =>
    Equal(new DeploymentRequest("https://v2.test/check/health", TimeSpan.FromMilliseconds(800), 2), DeploymentAdapter.Create(modern)));
#if !REV2
Run("migration.original-canonical-shape", 1, () =>
    Equal("""{"version":2,"transport":{"endpoint":"https://example.test/api","timeoutMs":5000},"retry":{"count":0},"enabled":true}""",
        ProfileMigrator.ToVersion2(legacy)));
#endif
#endif

#if REV2
const string backoff = """{"version":2,"transport":{"endpoint":"https://v2.test","timeoutMs":17000},"retry":{"count":2,"backoffMs":5500}}""";
Run("retry.backoff-propagates", 2, () =>
{
    Equal(5500, ProfileCodec.Parse(backoff).BackoffMs);
    Equal(5500, DeploymentAdapter.Create(backoff)!.BackoffMs);
    var (_, timeoutMs, retries, _) = ProfileCodec.Parse(backoff);
    var (_, timeout, deploymentRetries) = DeploymentAdapter.Create(backoff)!;
    Equal(17000, timeoutMs);
    Equal(2, retries);
    Equal(TimeSpan.FromMilliseconds(17000), timeout);
    Equal(2, deploymentRetries);
});
Run("retry.canonical-shape", 2, () =>
{
    Equal("""{"version":2,"transport":{"endpoint":"https://example.test/api","timeoutMs":5000},"retry":{"count":0,"backoffMs":0},"enabled":true}""",
        ProfileMigrator.ToVersion2(legacy));
    Equal("""{"version":2,"transport":{"endpoint":"https://v2.test","timeoutMs":17000},"retry":{"count":2,"backoffMs":5500},"enabled":true}""",
        ProfileMigrator.ToVersion2(backoff));
    Equal(0, ProfileCodec.Parse("""{"version":1,"endpoint":"https://v2.test","backoffMs":"unrecognized"}""").BackoffMs);
});
Run("retry.backoff-validation", 2, () =>
{
    foreach (var json in new[]
    {
        """{"version":2,"transport":{"endpoint":"https://v2.test"},"retry":{"backoffMs":-1}}""",
        """{"version":2,"transport":{"endpoint":"https://v2.test"},"retry":{"backoffMs":30001}}""",
        """{"version":2,"transport":{"endpoint":"https://v2.test"},"retry":{"backoffMs":null}}"""
    })
        Throws<FormatException>(() => ProfileCodec.Parse(json));
});
var batch = new NamedProfile[]
{
    new(" alpha ", "{broken"),
    new("Alpha", legacy),
    new(" ALPHA  ", "{broken"),
    new(" ", legacy),
    new(null!, legacy),
    null!,
    new("Beta", modern)
};
const string expectedIssues = "0:alpha:invalid-profile|2:ALPHA:duplicate-name|3::invalid-name|4:<null>:invalid-name|5:<null>:invalid-name";
string IssueText(BatchMigrationResult result) =>
    string.Join("|", result.Issues.Select(issue => $"{issue.Index}:{issue.Name ?? "<null>"}:{issue.Code}"));

Run("batch.atomic-default", 2, () =>
{
    var result = ProfileMigrator.MigrateBatch(batch);
    Equal(0, result.Profiles.Count);
    Equal(expectedIssues, IssueText(result));
});
Run("batch.best-effort", 2, () =>
{
    var result = ProfileMigrator.MigrateBatch(batch, MigrationMode.BestEffort);
    Equal("Alpha,Beta", string.Join(",", result.Profiles.Select(profile => profile.Name)));
    Equal(expectedIssues, IssueText(result));
    Equal(ProfileMigrator.ToVersion2(legacy), result.Profiles[0].Json);
    Equal(ProfileMigrator.ToVersion2(modern), result.Profiles[1].Json);
});
Run("batch.atomic-success-order-and-single-enumeration", 2, () =>
{
    var rows = new OneShot<NamedProfile>([new("zulu", modern), new("echo", legacy)]);
    var result = ProfileMigrator.MigrateBatch(rows, MigrationMode.Atomic);
    Equal(0, result.Issues.Count);
    Equal("zulu,echo", string.Join(",", result.Profiles.Select(profile => profile.Name)));
});
Run("batch.null-collection-and-invalid-mode", 2, () =>
{
    Throws<ArgumentNullException>(() => ProfileMigrator.MigrateBatch(null!));
    Throws<ArgumentOutOfRangeException>(() => ProfileMigrator.MigrateBatch(new OneShot<NamedProfile>([], rejectFirst: true), (MigrationMode)57));
    var result = ProfileMigrator.MigrateBatch([]);
    Equal(0, result.Profiles.Count);
    Equal(0, result.Issues.Count);
});
#endif

#if REV3
const string budgetOverV1 = """{"endpoint":"https://over.test","timeoutMs":40001,"retries":2,"enabled":false}""";
const string budgetOverV2 = """{"version":2,"transport":{"endpoint":"https://over.test","timeoutMs":56000},"retry":{"count":1,"backoffMs":8001}}""";
const string budgetExactV2 = """{"version":2,"transport":{"endpoint":"https://exact.test","timeoutMs":34000},"retry":{"count":2,"backoffMs":9000}}""";
Run("budget.inclusive-edge", 3, () =>
{
    Equal(9000, ProfileCodec.Parse(budgetExactV2).BackoffMs);
    Equal(60000, ProfileCodec.Parse("""{"endpoint":"https://exact.test","timeoutMs":60000,"retries":1}""").TimeoutMs);
});
Run("budget.reject-both-versions-before-disabled", 3, () =>
{
    Throws<FormatException>(() => ProfileCodec.Parse(budgetOverV1));
    Throws<FormatException>(() => ProfileMigrator.ToVersion2(budgetOverV2));
    Throws<FormatException>(() => DeploymentAdapter.Create(budgetOverV1));
});
Run("budget.batch-both-modes", 3, () =>
{
    NamedProfile[] rows = [new("release", budgetOverV1), new("release", budgetExactV2), new("parallel", budgetOverV2)];
    foreach (var mode in new[] { MigrationMode.Atomic, MigrationMode.BestEffort })
    {
        var result = ProfileMigrator.MigrateBatch(rows, mode);
        Equal("0:release:invalid-profile|2:parallel:invalid-profile", IssueText(result));
        Equal(mode == MigrationMode.Atomic ? 0 : 1, result.Profiles.Count);
    }
});
Run("endpoint.no-query-fragment-credentials", 3, () =>
{
    foreach (var endpoint in new[] { "https://a.test/?q=7", "https://a.test/?", "https://a.test/#f", "https://a.test/#", "https://user:pass@a.test/path" })
    {
        Throws<FormatException>(() => ProfileCodec.Parse(JsonSerializer.Serialize(new { endpoint })));
        Throws<FormatException>(() => ProfileMigrator.ToVersion2(JsonSerializer.Serialize(new { version = 2, transport = new { endpoint } })));
    }
});
Run("endpoint.health-joins-original-spelling", 3, () =>
{
    const string endpoint = "HTTPS://Server.TEST:843/Api%2FNested///";
    var json = JsonSerializer.Serialize(new { endpoint });
    Equal(endpoint, ProfileCodec.Parse(json).Endpoint);
    Equal(endpoint, ProfileCodec.Parse(ProfileMigrator.ToVersion2(json)).Endpoint);
    Equal("HTTPS://Server.TEST:843/Api%2FNested/health", DeploymentAdapter.Create(json)!.HealthUrl);
    Equal("https://v2.test/health", DeploymentAdapter.Create("""{"version":2,"transport":{"endpoint":"https://v2.test/"}}""")!.HealthUrl);
});
#endif

Console.Write(JsonSerializer.Serialize(cases, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

sealed record CaseResult(string Id, int Introduced, bool Passed, string? Detail);

sealed class OneShot<T>(IEnumerable<T> items, bool rejectFirst = false) : IEnumerable<T>
{
    private bool _used;
    public IEnumerator<T> GetEnumerator()
    {
        if (_used || rejectFirst)
            throw new InvalidOperationException("Unexpected enumeration.");
        _used = true;
        return items.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
