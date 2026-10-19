namespace RPM.Core.Tests;

/// <summary>
/// Finds the files that all rpm apps share, such as <c>strings.json</c> and <c>credits.json</c>. They
/// are in the <c>scripts</c> folder of the <c>rpm</c> repo, which must be checked out next to this repo.
/// A checkout that holds every app in one tree has them in its own <c>scripts</c> folder.
/// </summary>
internal static class SharedFiles
{
    public static string Path(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var sibling = System.IO.Path.Combine(dir.FullName, "rpm", "scripts", name);
            if (File.Exists(sibling)) return sibling;

            var own = System.IO.Path.Combine(dir.FullName, "scripts", name);
            if (File.Exists(own)) return own;
        }

        throw new FileNotFoundException(
            $"{name} was not found. It is in the rpm repo (scripts/{name}): check it out next to this repo.");
    }
}
