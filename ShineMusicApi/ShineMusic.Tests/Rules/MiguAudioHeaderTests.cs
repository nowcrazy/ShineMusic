using ShineMusic.Service.SourceDownload;
using Xunit;

namespace ShineMusic.Tests.Rules;

public sealed class MiguAudioHeaderTests
{
    [Theory]
    [InlineData("49443303000000000000", true)]
    [InlineData("49443304000000000000", true)]
    [InlineData("664C614300000000", true)]
    [InlineData("FFFB900000000000", true)]
    [InlineData("3C68746D6C3E", false)]
    [InlineData("4944", false)]
    public void Header_accepts_id3_versions_and_rejects_error_pages(string hex, bool accepted)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".audio");
        try
        {
            File.WriteAllBytes(path, Convert.FromHexString(hex));
            Assert.Equal(accepted, MiguSourceProvider.IsPlausibleAudioFile(path));
        }
        finally { File.Delete(path); }
    }
}
