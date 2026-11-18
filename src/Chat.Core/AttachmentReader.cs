namespace Chat.Core;

/// <summary>
/// 附件字节读取器：输入是持久化在 MetaJson 里的相对路径，读不到返回 null。
/// 宿主层实现负责路径越界校验与 IO 异常兜底；Chat.Core 自身不依赖任何存储实现。
/// </summary>
public delegate byte[]? AttachmentReader(string relativePath);
