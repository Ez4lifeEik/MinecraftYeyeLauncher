using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace ArclightLauncher.Helpers;

/// <summary>
/// 校验 Windows 可执行文件的 Authenticode 数字签名（WinVerifyTrust），
/// 并可选校验签名者主题是否包含预期发布者。校验失败抛异常（fail-closed）。
/// 仅在配置了发布者名称时调用，未签名项目不受影响。
/// </summary>
public static class AuthenticodeVerifier
{
    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE          = 2;
    private const uint WTD_REVOKE_NONE      = 0;
    private const uint WTD_CHOICE_FILE      = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE  = 2;

    /// <summary>
    /// 校验文件签名链有效；expectedPublisher 非空时还要求签名者主题包含该字符串。
    /// </summary>
    public static void Verify(string filePath, string? expectedPublisher)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Authenticode 校验仅支持 Windows");

        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct       = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath  = filePath,
            hFile          = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero
        };

        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        Marshal.StructureToPtr(fileInfo, pFile, false);

        var data = new WINTRUST_DATA
        {
            cbStruct            = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice          = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice       = WTD_CHOICE_FILE,
            pFile               = pFile,
            dwStateAction       = WTD_STATEACTION_VERIFY
        };

        var action = WinTrustActionGenericVerifyV2;
        try
        {
            int result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            // 释放 WinVerifyTrust 分配的状态句柄
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            if (result != 0)
                throw new InvalidOperationException(
                    $"更新包数字签名校验未通过（WinVerifyTrust=0x{result:X8}）。文件可能未签名或已被篡改，已拒绝安装。");
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }

        if (!string.IsNullOrWhiteSpace(expectedPublisher))
        {
            var subject = GetSignerSubject(filePath);
            if (subject is null ||
                subject.IndexOf(expectedPublisher, StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException(
                    $"更新包签名者与预期不符，已拒绝安装。期望包含：{expectedPublisher}；实际：{subject ?? "（无签名）"}");
            }
        }
    }

    private static string? GetSignerSubject(string filePath)
    {
        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
            return cert.Subject;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hWnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint   cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint   cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint   dwUIChoice;
        public uint   fdwRevocationChecks;
        public uint   dwUnionChoice;
        public IntPtr pFile;          // union 成员，文件校验时指向 WINTRUST_FILE_INFO
        public uint   dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint   dwProvFlags;
        public uint   dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
