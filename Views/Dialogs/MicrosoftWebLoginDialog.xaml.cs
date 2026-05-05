using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace ArclightLauncher.Views.Dialogs;

public partial class MicrosoftWebLoginDialog : Window
{
    private TaskCompletionSource<Uri?>? _completion;
    private CancellationTokenRegistration _cancellationRegistration;

    public MicrosoftWebLoginDialog()
    {
        InitializeComponent();
    }

    public async Task<Uri?> SignInAsync(Uri authorizationUri, CancellationToken ct)
    {
        _completion = new TaskCompletionSource<Uri?>();
        _cancellationRegistration = ct.Register(() =>
            Dispatcher.Invoke(() =>
            {
                _completion?.TrySetCanceled(ct);
                Close();
            }));

        try
        {
            await LoginBrowser.EnsureCoreWebView2Async();
            LoginBrowser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            LoginBrowser.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
            LoginBrowser.NavigationStarting += LoginBrowser_NavigationStarting;
            LoginBrowser.Source = authorizationUri;

            return await _completion.Task;
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            throw new InvalidOperationException(
                "未安装 Microsoft Edge WebView2 Runtime，无法打开内置正版登录窗口。请安装 WebView2 Runtime 后重试。",
                ex);
        }
    }

    private void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        LoginBrowser.CoreWebView2.Navigate(e.Uri);
    }

    private void LoginBrowser_NavigationStarting(
        object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
            return;

        if (!IsOAuthRedirect(uri))
            return;

        e.Cancel = true;
        StatusText.Text = "登录完成，正在校验 Minecraft 正版资料...";
        _completion?.TrySetResult(uri);
        Close();
    }

    private static bool IsOAuthRedirect(Uri uri)
        => uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
           uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    protected override void OnClosed(EventArgs e)
    {
        LoginBrowser.NavigationStarting -= LoginBrowser_NavigationStarting;
        if (LoginBrowser.CoreWebView2 is not null)
            LoginBrowser.CoreWebView2.NewWindowRequested -= CoreWebView2_NewWindowRequested;

        _completion?.TrySetResult(null);
        _cancellationRegistration.Dispose();
        base.OnClosed(e);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _completion?.TrySetResult(null);
        Close();
    }
}
