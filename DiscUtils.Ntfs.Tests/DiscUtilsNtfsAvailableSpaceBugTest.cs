namespace DiscUtils.Ntfs.Tests;

using DiscUtils.Partitions;
using DiscUtils.Streams;
using DiscUtils.Vhdx;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Reproduces a bug in LTRData.DiscUtils.Ntfs where <see cref="NtfsFileSystem.AvailableSpace"/>
/// returns a negative value after writing data to a file on an NTFS-formatted partition.
///
/// Root cause: The <c>ClusterBitmap._usedClusters</c> counter is incremented unconditionally
/// during cluster allocation without checking whether the clusters were already marked as
/// present. After writing ~300 MB in 1 MB chunks, the counter inflates to ~11.3 million
/// clusters on a partition that only has ~384,000 clusters total, producing a negative
/// <see cref="NtfsFileSystem.AvailableSpace"/>.
///
/// Affected version: LTRData.DiscUtils.Ntfs 1.0.75
/// GitHub: https://github.com/LTRData/DiscUtils
/// </summary>
[TestClass]
public sealed class DiscUtilsNtfsAvailableSpaceBugTest
{
    /// <summary>
    /// Verifies that <see cref="NtfsFileSystem.AvailableSpace"/> remains non-negative
    /// after writing data to a file that fills most of the partition.
    ///
    /// Steps:
    /// 1. Create a 2 GB dynamic VHDX with a GPT partition table.
    /// 2. Create a 1500 MB NTFS partition.
    /// 3. Write ~300 MB of dummy data in 1 MB chunks to leave ~1200 MB free.
    /// 4. Assert that <see cref="NtfsFileSystem.AvailableSpace"/> is non-negative.
    ///
    /// Expected: AvailableSpace ≈ 1200 MB (positive).
    /// Actual (bug): AvailableSpace = -44,831,866,880 (negative).
    /// </summary>
    [TestMethod]
    public void AvailableSpace_AfterWritingData_ShouldNotBeNegative()
    {
        string vhdxPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vhdx");
        long diskCapacityInBytes = 2L * 1024 * 1024 * 1024; // 2 GB
        long partitionSizeInBytes = 1500L * 1024 * 1024;     // 1500 MB
        long desiredFreeSpaceInBytes = 1200L * 1024 * 1024;  // 1200 MB

        try
        {
            using var fileStream = new FileStream(vhdxPath, FileMode.CreateNew, FileAccess.ReadWrite);
            using var disk = Disk.InitializeDynamic(fileStream, Ownership.Dispose, diskCapacityInBytes);

            // Initialize GPT partition table
            var gpt = GuidPartitionTable.Initialize(disk);

            // Create a 1500 MB partition
            long bytesPerSector = disk.Geometry!.Value.BytesPerSector;
            long sectorCount = partitionSizeInBytes / bytesPerSector;
            long startSector = gpt.FirstUsableSector;

            // Align to 1 MB boundary
            long alignmentInSectors = (1024 * 1024) / bytesPerSector;
            if (startSector < alignmentInSectors)
            {
                startSector = alignmentInSectors;
            }

            long endSector = startSector + sectorCount - 1;

            int partitionIndex = gpt.Create(
                startSector,
                endSector,
                GuidPartitionTypes.WindowsBasicData,
                0,
                "TestPartition");

            // Format as NTFS
            var volumeManager = new VolumeManager(disk);
            var logicalVolume = volumeManager.GetLogicalVolumes()[partitionIndex];
            using var ntfsFs = NtfsFileSystem.Format(logicalVolume, "Test");

            long availableBefore = ntfsFs.AvailableSpace;
            Console.WriteLine($"Partition size:       {partitionSizeInBytes:N0} bytes");
            Console.WriteLine($"Cluster size:         {ntfsFs.ClusterSize:N0} bytes");
            Console.WriteLine($"Available before:     {availableBefore:N0} bytes");
            Console.WriteLine($"Desired free space:   {desiredFreeSpaceInBytes:N0} bytes");

            // Calculate how much data to write to leave the desired free space
            long dataToWrite = availableBefore - desiredFreeSpaceInBytes;
            Console.WriteLine($"Data to write:        {dataToWrite:N0} bytes");

            Assert.IsTrue(dataToWrite > 0, "Not enough available space to write data.");

            // Write dummy data in 1 MB chunks (same pattern as VirtualHardDiskSteps)
            using (var file = ntfsFs.OpenFile("dummy.bin", FileMode.Create, FileAccess.Write))
            {
                byte[] buffer = new byte[1024 * 1024]; // 1 MB chunks
                long remaining = dataToWrite;

                while (remaining > 0)
                {
                    int toWrite = (int)Math.Min(buffer.Length, remaining);
                    file.Write(buffer, 0, toWrite);
                    remaining -= toWrite;
                }
            }

            // Read AvailableSpace after writing — this is where the bug manifests
            long availableAfter = ntfsFs.AvailableSpace;
            long usedSpace = ntfsFs.UsedSpace;
            long totalSpace = ntfsFs.Size;

            Console.WriteLine($"Available after:      {availableAfter:N0} bytes");
            Console.WriteLine($"Used space:           {usedSpace:N0} bytes");
            Console.WriteLine($"Total space (Size):   {totalSpace:N0} bytes");
            Console.WriteLine($"Used + Available:     {usedSpace + availableAfter:N0} bytes");

            // The bug: AvailableSpace becomes negative because ClusterBitmap._usedClusters
            // is inflated far beyond the actual number of allocated clusters.
            Assert.IsTrue(
                availableAfter >= 0,
                $"AvailableSpace should be non-negative but was {availableAfter:N0} bytes. " +
                $"UsedSpace ({usedSpace:N0}) exceeds TotalSpace ({totalSpace:N0}) by {usedSpace - totalSpace:N0} bytes, " +
                $"indicating that the internal _usedClusters counter is inflated.");

            // Secondary check: UsedSpace should never exceed TotalSpace
            Assert.IsTrue(
                usedSpace <= totalSpace,
                $"UsedSpace ({usedSpace:N0}) should not exceed TotalSpace ({totalSpace:N0}). " +
                $"Overflow: {usedSpace - totalSpace:N0} bytes.");
        }
        finally
        {
            if (File.Exists(vhdxPath))
            {
                File.Delete(vhdxPath);
            }
        }
    }
}