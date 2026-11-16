using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ShineMusic.Service;

var output = args.Length > 0 ? Path.GetFullPath(args[0]) : throw new ArgumentException("Specify a scratch output directory.");
Directory.CreateDirectory(output);
var services = new ServiceCollection();
services.AddLogging();
services.AddHttpClient(); // Anonymous iTunes requests; no credentials/configuration/database.
services.AddTransient<ArtworkFetcher>();
using var provider = services.BuildServiceProvider();
var fetcher = provider.GetRequiredService<ArtworkFetcher>();
var samples = new[]
{
    new Sample("晴天", "周杰伦", "叶惠美", 269000),
    new Sample("富士山下", "陈奕迅", "What's Going On…?", 259000),
    new Sample("月亮代表我的心", "邓丽君", null, null),
    new Sample("Hello", "Adele", "25", 295000)
};
var attempts = new List<object>();
foreach (var sample in samples)
{
    ItunesMetadataResult result;
    try
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        result = await fetcher.FetchMatchedAsync(sample.Title, [sample.Artist], sample.Album, sample.DurationMs,
            "metadata-probe", Path.Combine(output, "artwork"), deadline.Token);
    }
    catch (Exception exception)
    {
        result = new ItunesMetadataResult(false, Error: exception.GetType().Name + ": " + exception.Message);
    }
    attempts.Add(new { sample, result });
    Console.WriteLine($"{sample.Title}: success={result.Success}, error={result.Error ?? "none"}");
}
var report = new
{
    timestamp = DateTimeOffset.UtcNow,
    note = "匿名真实 iTunes 请求；未写数据库。未命中可能涉及名称本地化、商店目录、时长或专辑差异，也可能是网络问题；不放宽身份匹配，不将未命中声明为成功。",
    attempts
};
var reportPath = Path.Combine(output, "metadata-probe.json");
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(reportPath);

internal sealed record Sample(string Title, string Artist, string? Album, long? DurationMs);
