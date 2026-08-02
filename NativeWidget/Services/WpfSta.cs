using System.Runtime.ExceptionServices;
using System.Threading;

namespace NativeWidget.Services;

internal static class WpfSta
{
    public static T Run<T>(Func<T> action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return action();

        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result!;
    }
}
