namespace ShowFilesAsList;

/// <summary>
/// Formats byte counts and durations the way result.json and the progress display show them.
/// </summary>
static class SizeTextExtensions
{
    const int BytesPerUnitStep = 1024;
    static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <param name="byteCount">A size in bytes.</param>
    extension(long byteCount)
    {
        /// <summary>
        /// Writes the size in the largest unit it reaches, each unit being 1024 times the previous one.
        /// Sizes under 1 KB stay whole ("512 B"); larger ones get two decimals in the number format of the current culture ("1.50 GB").
        /// </summary>
        /// <returns>The size followed by its unit.</returns>
        public string ToSizeText()
        {
            double size = byteCount;
            int unitIndex = 0;

            while (size >= BytesPerUnitStep && unitIndex < Units.Length - 1)
            {
                size /= BytesPerUnitStep;
                unitIndex++;
            }

            return unitIndex == 0 ? $"{byteCount} {Units[0]}" : $"{size:F2} {Units[unitIndex]}";
        }
    }

    /// <param name="duration">An elapsed time.</param>
    extension(TimeSpan duration)
    {
        /// <summary>Milliseconds under a second, seconds with two decimals under a minute, whole minutes and seconds beyond that.</summary>
        public string ToDurationText() => duration.TotalSeconds switch
        {
            < 1 => $"{duration.TotalMilliseconds:F0} ms",
            < 60 => $"{duration.TotalSeconds:F2} s",
            _ => $"{(int)duration.TotalMinutes} m {duration.Seconds} s",
        };
    }
}
