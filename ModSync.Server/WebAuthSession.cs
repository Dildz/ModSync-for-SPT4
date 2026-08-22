using SPTarkov.DI.Annotations;

namespace ModSync.Server;

/// <summary>
/// Whether the person driving this browser tab has logged in.
///
/// Scoped, which under Blazor's interactive server mode means one instance per circuit - so this
/// lives on the SERVER for the lifetime of a connected page, and a client can never set it. That is
/// the whole reason the editor can be gated with a plain boolean instead of cookies and middleware:
/// the page's state was never in the browser to begin with, and every save runs as a method call
/// inside the circuit rather than as an HTTP endpoint anyone could POST to.
///
/// The consequence to be honest about: a circuit ends when the tab is closed or reloaded, so a
/// refresh means logging in again. That is the price of not carrying a session cookie and a data
/// protection key ring around, and for a page an admin visits occasionally it is the right trade.
/// </summary>
[Injectable(InjectionType.Scoped)]
public class WebAuthSession
{
    public bool IsAuthenticated { get; private set; }

    public void Grant() => IsAuthenticated = true;

    public void Revoke() => IsAuthenticated = false;
}
