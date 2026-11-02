namespace SuperMOOO.Social.Comments;

/// <summary>Bình luận thô lấy từ nền tảng, chưa phân tích cảm xúc/lead.</summary>
public sealed record RawComment(string Author, string? ProfileUrl, string Text, int Likes, string? Created);
