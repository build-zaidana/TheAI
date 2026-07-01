namespace CodexBar.Providers;

public enum ProviderAttachmentKind
{
    LocalImage,
    FileMention
}

public sealed record ProviderInputAttachment(
    string Path,
    string DisplayName,
    ProviderAttachmentKind Kind);
