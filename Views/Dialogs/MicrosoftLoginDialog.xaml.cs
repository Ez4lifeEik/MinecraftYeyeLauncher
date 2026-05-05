using System.Diagnostics;
using System.Windows;
using ProjBobcat.Class.Model.Microsoft.Graph;

namespace ArclightLauncher.Views.Dialogs;

public partial class MicrosoftLoginDialog : Window
{
    private string? _verificationUri;
    private string? _userCode;

    public MicrosoftLoginDialog()
    {
        InitializeComponent();
    }

    public void SetDeviceCode(DeviceIdResponseModel deviceCode)
    {
        _verificationUri = string.IsNullOrWhiteSpace(deviceCode.VerificationUri)
            ? "https://microsoft.com/devicelogin"
            : deviceCode.VerificationUri;
        _userCode = deviceCode.UserCode;

        StatusText.Text = "等待网页登录确认...";
        MessageText.Text = deviceCode.Message ??
                           "打开网页登录，输入下面的验证码，然后使用已购买 Minecraft Java 版的 Microsoft 账号登录。";
        CodeText.Text = _userCode;
        UriText.Text = _verificationUri;
        OpenBrowserButton.IsEnabled = true;
        CopyCodeButton.IsEnabled = true;
    }

    public void SetCompleted(string message)
    {
        StatusText.Text = "登录完成";
        MessageText.Text = message;
        OpenBrowserButton.IsEnabled = false;
        CopyCodeButton.IsEnabled = false;
    }

    public void SetFailed(string message)
    {
        StatusText.Text = "登录失败";
        MessageText.Text = message;
    }

    private void OpenBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_verificationUri))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = _verificationUri,
            UseShellExecute = true
        });
    }

    private void CopyCodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_userCode))
            Clipboard.SetText(_userCode);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();
}
