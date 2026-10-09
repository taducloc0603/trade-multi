using TradeDesktop.Application.Abstractions;

namespace TradeDesktop.App.Services;

// Mở cửa sổ WebView2 để user tự đăng nhập PrimeXBT trên trang thật; trả phiên (JWT + cookie) hoặc null nếu huỷ.
// Tách interface để ConfigViewModel không phụ thuộc trực tiếp vào cửa sổ WPF.
public interface IPrimeXbtLoginDialog
{
    PrimeXbtSession? ShowLogin();
}

public sealed class PrimeXbtLoginDialog : IPrimeXbtLoginDialog
{
    public PrimeXbtSession? ShowLogin()
    {
        var window = new PrimeXbtLoginWindow();
        var owner = System.Windows.Application.Current?.Windows
            .OfType<System.Windows.Window>()
            .FirstOrDefault(w => w.IsActive);
        if (owner is not null && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }

        return window.ShowDialog() == true ? window.CapturedSession : null;
    }
}
