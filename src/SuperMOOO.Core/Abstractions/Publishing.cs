namespace SuperMOOO.Core.Abstractions;

public enum PublishPrivacy { Private, Unlisted, Public }
public sealed record OAuthSession(string Platform, string State, string CodeVerifier, Uri RedirectUri); // http://127.0.0.1:8765/callback/{platform}
