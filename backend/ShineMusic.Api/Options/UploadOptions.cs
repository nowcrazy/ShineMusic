// 上传限制配置选项：绑定 "Uploads" 配置节，约束批量导入时的文件大小、数量与允许格式，启动时强校验。
namespace ShineMusic.Api.Options;

/// <summary>
/// 音频文件批量上传限制，对应配置节 "Uploads"。
/// </summary>
public sealed class UploadOptions
{
    /// <summary>该选项在 appsettings 中对应的配置节名称。</summary>
    public const string SectionName = "Uploads";

    /// <summary>
    /// 单个文件大小上限，单位字节；启动校验要求取值大于 0 且不超过 1073741824（1 GiB）。
    /// 该值会通过系统能力接口下发给客户端，上传前即可拦截超限文件。
    /// </summary>
    public long? MaxFileSizeBytes { get; set; }

    /// <summary>
    /// 单次批量上传的总大小上限，单位字节；必须不小于 <see cref="MaxFileSizeBytes"/>，
    /// 且不得超过 4294967296（4 GiB，与 Kestrel/表单请求体上限保持一致）。默认 1 GiB。
    /// </summary>
    public long MaxBatchSizeBytes { get; set; } = 1073741824;

    /// <summary>每个批次允许上传的最大文件数，取值范围 1～20，默认 10。</summary>
    public int MaxFilesPerBatch { get; set; } = 10;

    /// <summary>
    /// 允许上传的音频容器格式标识列表（如 flac、mp3、m4a），小写匹配；
    /// 至少配置一项，否则应用启动校验失败。
    /// </summary>
    public List<string> AllowedFormats { get; set; } = [];
}