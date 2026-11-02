namespace HangAll;

/// <summary>Names shared by the app, its installer and its per-user state.</summary>
public static class AppIdentity
{
#if HANGALL_TEST
    public const bool IsTestBuild = true;
    public const string ProductName = "HangAll 测试版";
    public const string DataFolderName = "HangAllTest";
    public const string MutexName = @"Local\HangAll.Test.SingleInstance";
    public const string ToggleEventName = @"Local\HangAll.Test.Toggle";
    public const string AppUserModelId = "HangAll.Test.Desktop.V1";
    public const string ExecutableName = "HangAllTest.exe";
    public const string StartupRunName = "HangAllTest";
    public const string DefaultHotKey = "Ctrl+Alt+Shift+H";
#else
    public const bool IsTestBuild = false;
    public const string ProductName = "HangAll";
    public const string DataFolderName = "HangAll";
    public const string MutexName = @"Local\HangAll.V1.SingleInstance";
    public const string ToggleEventName = @"Local\HangAll.V1.Toggle";
    public const string AppUserModelId = "HangAll.Desktop.V1";
    public const string ExecutableName = "HangAll.exe";
    public const string StartupRunName = "HangAll";
    public const string DefaultHotKey = "Ctrl+Alt+H";
#endif
}
