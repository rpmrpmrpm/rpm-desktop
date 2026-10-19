using System.Diagnostics;
using RPM.Desktop.Services.Shell;

namespace Elsewhere.Toolkit;

/// <summary>
/// A stand-in for the renderer: a frame that is neither the framework nor our code. Its frames sit
/// between a throw and the line of our code that asked for it.
///
/// <para>It is in its own file and namespace on purpose. <see cref="ExceptionTrace.SiteOf"/> must see
/// its frames as neither <c>System.*</c> nor <c>RPM.*</c>. That depends on the namespace, so a test in
/// the RPM namespace cannot fake it.</para>
/// </summary>
internal static class Dispatcher
{
    internal static string? Ask() => ExceptionTrace.SiteOf(new StackTrace(fNeedFileInfo: false));
}
