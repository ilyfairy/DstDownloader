using SteamKit2.Internal;

namespace Ilyfairy.Tools;

public class ModInfo
{
    public PublishedFileDetails details;
    public ModInfo(PublishedFileDetails details)
    {
        this.details = details;
        Tags = details.tags.Select(v => v.display_name).ToArray();
    }
    /// <summary>
    /// Mod名称
    /// </summary>
    public string Name => details.title;
    /// <summary>
    /// 是否可以订阅
    /// </summary>
    public bool CanSubscribe => details.can_subscribe;
    /// <summary>
    /// 是否是UGC的Mod
    /// </summary>
    public bool IsUgc => string.IsNullOrEmpty(details?.file_url);
    /// <summary>
    /// Mod描述
    /// </summary>
    public string Description => details.file_description;
    /// <summary>
    /// Mod ID
    /// </summary>
    public ulong Id => details.publishedfileid;
    public string[] Tags { get; }

    /// <summary>
    /// 文件链接
    /// </summary>
    public string FileUrl => details.file_url;
    /// <summary>
    /// Mod文件大小
    /// </summary>
    public ulong FileSize => details.file_size;

    /// <summary>
    /// 预览图片Url
    /// </summary>
    public string PreviewImageUrl => details.preview_url;
    /// <summary>
    /// 预览图片大小
    /// </summary>
    public ulong PreviewImageSize => details.preview_file_size;

    /// <summary>
    /// 不重复访客次数
    /// </summary>
    public ulong Views => details.views;
    /// <summary>
    /// 当前订阅人数
    /// </summary>
    public uint Subscriptions => details.subscriptions;
    /// <summary>
    /// 当前收藏人数
    /// </summary>
    public long Favorited => details.favorited;
    /// <summary>
    /// 公开的评论数量
    /// </summary>
    public int CommentsPublic => details.num_comments_public;

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTimeOffset TimeCreated => DateTimeOffset.FromUnixTimeSeconds(details.time_created);
    /// <summary>
    /// 更新时间
    /// </summary>
    public DateTimeOffset TimeUpdated => DateTimeOffset.FromUnixTimeSeconds(details.time_updated);

    /// <summary>
    /// 创建者的SteamID
    /// </summary>
    public ulong Creator => details.creator;

}