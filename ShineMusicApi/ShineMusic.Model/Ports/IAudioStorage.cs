// 职责：音频文件存储的领域端口（依赖倒置）。
// 当前实现是本地磁盘（单服务器自建曲库）；未来换对象存储 / CDN 时只需替换实现，
// 播放与发布用例不需要改动。端口刻意不暴露「签名 URL」——那是具体实现的能力。

namespace ShineMusic.Model.Ports;

/// <summary>音频资产存储：把不可信的存储键安全地映射到物理位置。</summary>
public interface IAudioStorage
{
    /// <summary>正式曲库根目录绝对路径。</summary>
    string LibraryRoot { get; }

    /// <summary>未审核隔离区根目录绝对路径。</summary>
    string QuarantineRoot { get; }

    /// <summary>解析正式曲库内的文件；路径穿越等非法键一律返回 false（fail-closed）。</summary>
    bool TryResolveLibrary(string storageKey, out string filePath);

    /// <summary>解析隔离区内的文件；路径穿越等非法键一律返回 false（fail-closed）。</summary>
    bool TryResolveQuarantine(string storageKey, out string filePath);
}
