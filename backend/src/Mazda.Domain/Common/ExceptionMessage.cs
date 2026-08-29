namespace Mazda.Domain.Common;

public static class ExceptionMessage
{
    /// <summary>
    /// Flattens an exception and its inner causes into one human-readable line.
    /// The useful part of a failure is often two levels down (an
    /// InvalidOperationException wrapping the provider's actual complaint), so
    /// using only <c>ex.Message</c> at a catch site throws it away.
    /// </summary>
    public static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null && parts.Count < 4; e = e.InnerException)
        {
            var m = e.Message.Trim();
            if (m.Length > 0 && !parts.Contains(m)) parts.Add(m);
        }
        return parts.Count == 0 ? ex.GetType().Name : string.Join(" → ", parts);
    }
}
