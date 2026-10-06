using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace WemLabeler;

/// <summary>
/// 「打开 txtp 用外部程序没反应」这类问题的现场取证。
///
/// 现象可能是：进程确实起来了，但窗口永远不出现。能造成这种情况的只有运行环境，
/// 所以这里把判断环境所需的事实全部收集出来：谁启动了本程序、进程跑在哪个窗口站、
/// 是不是 AppContainer / 受限令牌、%TEMP% 能不能写、宿主有没有塞 Electron 变量。
///
/// 启动时自动往日志里记一份（不需要用户做任何操作），
/// 也可以在「帮助 → 环境诊断」里随时再看一次。
/// </summary>
public partial class MainWindow
{
    #region Win32

    private const int UoiName = 2;
    private const uint TokenQuery = 0x0008;
    private const int TokenIsAppContainer = 29;
    private const int TokenHasRestrictions = 26;

    [DllImport("user32.dll")]
    private static extern IntPtr GetProcessWindowStation();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex,
        StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
        out int tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
        ref ProcessBasicInformation processInformation, int processInformationLength, out int returnLength);

    #endregion

    /// <summary>启动时调用：把环境事实写进日志，方便事后排查外部程序打不开的问题。</summary>
    private void LogEnvironmentReport()
    {
        try
        {
            VgmLog("===== 环境诊断（启动时自动记录）=====");
            foreach (var line in BuildEnvironmentReport().Split('\n'))
            {
                var text = line.TrimEnd('\r', '\n');
                if (text.Length > 0) VgmLog("  " + text);
            }
            VgmLog("===== 环境诊断结束 =====");
        }
        catch (Exception ex)
        {
            VgmLog($"[diag] 环境诊断失败: {ex.Message}");
        }
    }

    private void RunEnvironmentDiagnostics()
    {
        var report = BuildEnvironmentReport();
        try
        {
            VgmLog("===== 环境诊断（手动）=====");
            foreach (var line in report.Split('\n'))
            {
                var text = line.TrimEnd('\r', '\n');
                if (text.Length > 0) VgmLog("  " + text);
            }
            VgmLog("===== 环境诊断结束 =====");
        }
        catch { }

        MessageBox.Show(this, Locale.S("dlg_env_report_hint", report),
            Locale.S("dlg_env_report_title"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string BuildEnvironmentReport()
    {
        var sb = new StringBuilder();

        void Line(string label, string value) => sb.AppendLine($"{label}: {value}");

        // 每一项单独兜底：任何一项读不到都不能拖垮整份报告
        void Try(string label, Func<string> read)
        {
            try { Line(label, read()); }
            catch (Exception ex) { Line(label, $"(读不到: {ex.GetType().Name}: {ex.Message})"); }
        }

        Line("时间", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        Try("程序", () => Environment.ProcessPath ?? AppDomain.CurrentDomain.BaseDirectory);

        Try("启动它的进程", () =>
        {
            var (name, pid) = GetParentProcess();
            return name == null ? "(拿不到)" : $"{name} (pid {pid})";
        });

        Try("进程身份", () => System.Security.Principal.WindowsIdentity.GetCurrent().Name);
        Try("令牌所有者", () =>
            System.Security.Principal.WindowsIdentity.GetCurrent().Owner?.Value ?? "(未知)");
        Try("会话 ID", () => System.Diagnostics.Process.GetCurrentProcess().SessionId.ToString());

        Try("窗口站", () => GetObjectName(GetProcessWindowStation()));
        Try("桌面", () => GetObjectName(GetThreadDesktop(GetCurrentThreadId())));

        Try("AppContainer", () =>
        {
            var (isAppContainer, _, tokenError) = GetTokenFlags();
            return tokenError != null ? $"(读不到: {tokenError})" : (isAppContainer ? "是" : "否");
        });
        Try("受限令牌", () =>
        {
            var (_, hasRestrictions, tokenError) = GetTokenFlags();
            return tokenError != null ? "(读不到)" : (hasRestrictions ? "是" : "否");
        });

        Try("%TEMP%", () => Path.GetTempPath());
        Try("%TEMP% 可写", () => CanWrite(Path.GetTempPath()) ? "是" : "否（外部程序常常因此打不开）");
        Try("TEMP 环境变量", () => Environment.GetEnvironmentVariable("TEMP") ?? "(未设置)");

        Try("TEMP 是否被程序改过", () => Program.TempRedirected ? "是" : "否");
        if (Program.TempRedirected)
        {
            Try("原来的 TEMP", () => Program.OriginalTemp ?? "(拿不到)");
            Try("原 TEMP 本身可写", () => Program.OriginalTempWritable ? "是" : "否");
            Try("原 TEMP\\WPF 可写", () =>
                Program.OriginalTemp != null && CanWrite(Path.Combine(Program.OriginalTemp, "WPF")) ? "是" : "否");
        }

        foreach (var name in HostVariablesToReport)
        {
            var variable = name;
            Try($"env {variable}", () => Environment.GetEnvironmentVariable(variable) ?? "(未设置)");
        }

        Try("VSCODE_IPC_HOOK_CLI",
            () => Environment.GetEnvironmentVariable("VSCODE_IPC_HOOK_CLI") ?? "(未设置)");

        return sb.ToString();
    }

    /// <summary>
    /// 宿主（Electron/Node 类工具，比如各种 AI 代理终端）会往环境变量里塞的东西。
    /// 带着 <c>ELECTRON_RUN_AS_NODE</c> 启动 Code.exe，VS Code 会退化成 Node 解释器。
    /// </summary>
    private static readonly string[] HostVariablesToReport =
    {
        "ELECTRON_RUN_AS_NODE",
        "ELECTRON_NO_ATTACH_CONSOLE",
        "NODE_OPTIONS"
    };

    private static (string? Name, int Id) GetParentProcess()
    {
        try
        {
            var info = new ProcessBasicInformation();
            var status = NtQueryInformationProcess(GetCurrentProcess(), 0, ref info,
                Marshal.SizeOf<ProcessBasicInformation>(), out _);
            if (status != 0) return (null, 0);

            var pid = info.InheritedFromUniqueProcessId.ToInt64();
            if (pid <= 0) return (null, (int)pid);

            using var parent = System.Diagnostics.Process.GetProcessById((int)pid);
            return (parent.ProcessName, (int)pid);
        }
        catch
        {
            return (null, 0);
        }
    }

    private static (bool IsAppContainer, bool HasRestrictions, string? Error) GetTokenFlags()
    {
        var token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
                return (false, false, $"OpenProcessToken 失败 ({Marshal.GetLastWin32Error()})");

            var appContainer = ReadTokenBool(token, TokenIsAppContainer);
            var restricted = ReadTokenBool(token, TokenHasRestrictions);
            return (appContainer, restricted, null);
        }
        catch (Exception ex)
        {
            return (false, false, ex.Message);
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    private static bool ReadTokenBool(IntPtr token, int informationClass)
    {
        if (!GetTokenInformation(token, informationClass, out var value, sizeof(int), out _))
            return false;
        return value != 0;
    }

    /// <summary>取窗口站 / 桌面的名字（WinSta0、Default 才说明是正常交互式桌面）。</summary>
    private static string GetObjectName(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return "(拿不到句柄)";
        try
        {
            var buffer = new StringBuilder(256);
            if (!GetUserObjectInformation(handle, UoiName, buffer, buffer.Capacity * 2, out _))
                return $"(读不到, 错误 {Marshal.GetLastWin32Error()})";
            return buffer.ToString();
        }
        catch (Exception ex)
        {
            return $"(异常: {ex.Message})";
        }
    }

    private static bool CanWrite(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $"probe_{Guid.NewGuid():N}.tmp");
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1, FileOptions.DeleteOnClose);
            stream.WriteByte(0);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
