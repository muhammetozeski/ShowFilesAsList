namespace ShowFilesAsList.Ntfs;

/// <summary>
/// One contiguous extent of an NTFS non-resident attribute: <paramref name="ClusterCount"/> clusters starting
/// at virtual cluster <paramref name="StartVcn"/> within the attribute, physically located at logical cluster
/// <paramref name="StartLcn"/> on the volume.
/// </summary>
readonly record struct NtfsDataRun(long StartVcn, long StartLcn, long ClusterCount);
