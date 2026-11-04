namespace OllamaRouter.Security;

/// <summary>
/// Owner-only file permissions for files that hold key material (task #50). On Unix the mode
/// is set to <c>0600</c> (owner read/write only) on the file; on Windows the NTFS ACLs (created
/// by the default <see cref="Directory.CreateDirectory"/>/file APIs) already confine access to
/// the owner user + Administrators, so this is a no-op.
/// <para>
/// Shared by the API key store (<c>config/default-key.dat</c>, <c>config/api-keys.json</c>) and
/// the hardened <c>RouterConfigPersistence</c> atomic write (<c>config/config.json</c>).
/// Best-effort by contract: permission failures (e.g. unsupported filesystem) are swallowed —
/// the write itself has already succeeded and must not be masked by a permission hiccup.
/// </para>
/// </summary>
public static class SecretFilePermissions
{
    /// <summary>
    /// Applies owner-only (<c>0600</c>) Unix permissions to <paramref name="path"/>; a no-op on
    /// Windows. Never throws.
    /// </summary>
    public static void ApplyOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best-effort hardening: the file content is already written; a permission
            // failure on an exotic filesystem must never break the write path.
        }
    }
}
