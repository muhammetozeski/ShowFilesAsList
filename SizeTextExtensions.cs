namespace ShowFilesAsList;

/// <summary>
/// Turns byte counts into the size text written to result.json and to the progress line.
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
}
