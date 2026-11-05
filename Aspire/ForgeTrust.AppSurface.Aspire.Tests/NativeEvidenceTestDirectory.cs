namespace ForgeTrust.AppSurface.Aspire.Tests;

/// <summary>Deletes a test-owned directory, including Git's read-only objects, without following links.</summary>
internal static class NativeEvidenceTestDirectory
{
    internal static void Delete(string root)
    {
        var rootAttributes = File.GetAttributes(root);
        if (rootAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            Directory.Delete(root);
            return;
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (attributes.HasFlag(FileAttributes.Directory)) Directory.Delete(path);
                else File.Delete(path);
                continue;
            }

            if (attributes.HasFlag(FileAttributes.Directory)) Delete(path);
            else
            {
                if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                File.Delete(path);
            }
        }

        if (rootAttributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(root, rootAttributes & ~FileAttributes.ReadOnly);
        Directory.Delete(root);
    }
}
