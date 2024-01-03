using SteamDownloader.WebApi;

namespace Ilyfairy.Tools;

public class ModInfo
{
    public WorkshopFileDetails details;
    public ModInfo(WorkshopFileDetails details)
    {
        this.details = details;
        Tags = details.Tags?.Select(v => v.DisplayName).ToArray() ?? [];
    }

    public bool IsValid
    {
        get
        {
            if (details.Result == 1)
                return true;
            if (details.Result == 8)
                return false;

            return true;
        }
    }

    /// <summary>
    /// Mod名称
    /// </summary>
    public string Name => details.Title;

    /// <summary>
    /// 是否可以订阅
    /// </summary>
    public bool CanSubscribe => details.CanSubscribe;

    /// <summary>
    /// 是否是UGC的Mod
    /// </summary>
    public bool IsUGC => string.IsNullOrEmpty(details.FileUrl) && IsValid;

    /// <summary>
    /// Mod描述
    /// </summary>
    public string Description => details.FileDescription;

    /// <summary>
    /// Mod ID
    /// </summary>
    public ulong Id => details.PublishedFileId;

    /// <summary>
    /// Tags
    /// </summary>
    public string[] Tags { get; }

    /// <summary>
    /// 文件链接
    /// </summary>
    public string FileUrl => details.FileUrl;

    /// <summary>
    /// Mod文件大小
    /// </summary>
    public ulong FileSize => details.FileSize;

    /// <summary>
    /// 预览图片Url
    /// </summary>
    public string PreviewImageUrl => details.PreviewUrl;

    /// <summary>
    /// 预览图片大小
    /// </summary>
    public ulong PreviewImageSize => details.PreviewFileSize;

    /// <summary>
    /// 不重复访客次数
    /// </summary>
    public ulong Views => details.Views;

    /// <summary>
    /// 当前订阅人数
    /// </summary>
    public uint Subscriptions => details.Subscriptions;

    /// <summary>
    /// 当前收藏人数
    /// </summary>
    public long Favorited => details.Favorited;

    /// <summary>
    /// 公开的评论数量
    /// </summary>
    public int CommentsPublic => details.NumCommentsPublic;

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTimeOffset TimeCreated => details.TimeCreated;

    /// <summary>
    /// 更新时间
    /// </summary>
    public DateTimeOffset TimeUpdated => details.TimeUpdated;

    /// <summary>
    /// 创建者的SteamID
    /// </summary>
    public ulong Creator => details.Creator;

}