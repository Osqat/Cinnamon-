using System;

namespace Cinnamon
{
    /// <summary>
    /// Apply to an assembly to opt into CinnamonPatcher auto-update from GitHub Releases.
    /// The GitHub release must have a DLL asset named exactly: AssemblyName + ".dll"
    /// Example: [assembly: Cinnamon.AutoUpdate("myusername/mymod")]
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class AutoUpdateAttribute : Attribute
    {
        public string RepoSlug { get; }
        public AutoUpdateAttribute(string repoSlug) { RepoSlug = repoSlug; }
    }
}
