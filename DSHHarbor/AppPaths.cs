namespace DSHHarbor;

internal static class AppPaths
{
    private static readonly string LocalAppData =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string NewRoot = Path.Combine(LocalAppData, "DSHHarbor");
    private static readonly string LegacyRoot = Path.Combine(LocalAppData, "DSHDesktop");
    // Existing installations keep using their original data directory so an
    // in-place product rename never risks moving a live Harness installation.
    public static readonly string Root = Directory.Exists(NewRoot) || !Directory.Exists(LegacyRoot)
        ? NewRoot
        : LegacyRoot;
    public static readonly string RuntimeDirectory = Path.Combine(Root, "runtime");
    public static readonly string HarnessDirectory = Path.Combine(Root, "harness");
    // Keep DSH_HOME below the hoisted Harness installation. On Windows the
    // profile fallback junctions can be untrusted in restricted/non-interactive
    // launch contexts. Node can then continue its parent-directory walk and
    // resolve the same packages from HarnessDirectory/node_modules.
    public static readonly string DshHomeDirectory = Path.Combine(HarnessDirectory, "home");
    public static readonly string LegacyDshHomeDirectory = Path.Combine(Root, "dsh-home");
    public static readonly string LogDirectory = Path.Combine(Root, "logs");
    public static readonly string CacheDirectory = Path.Combine(Root, "cache");
    public static readonly string UpdateStateFile = Path.Combine(Root, "update-state.json");
}
