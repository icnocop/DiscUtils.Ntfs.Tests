namespace DiscUtils.Ntfs.Tests;

using System.Globalization;
using System.Runtime.InteropServices;
using DiscUtils.Partitions;
using DiscUtils.Streams;
using DiscUtils.Vhdx;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.Storage.Vhd;

/// <summary>
/// Reproduces a bug in LTRData.DiscUtils.Ntfs where <see cref="NtfsFileSystem.Format"/>
/// creates NTFS metadata that Windows considers unhealthy
/// (<c>OperationalStatus = 53263</c>, <c>HealthStatus = 1 (Warning)</c>).
/// This causes WMI operations like <c>MSFT_Partition.GetSupportedSize</c> to fail
/// with error code 42008.
///
/// Affected version: LTRData.DiscUtils.Ntfs 1.0.75
/// GitHub: https://github.com/LTRData/DiscUtils
/// </summary>
[TestClass]
public sealed class DiscUtilsNtfsVolumeHealthBugTest
{
    /// <summary>
    /// The WMI <c>OperationalStatus</c> value that indicates a healthy volume (OK).
    /// </summary>
    private const ushort OperationalStatusOk = 2;

    /// <summary>
    /// The WMI <c>HealthStatus</c> value that indicates a healthy volume.
    /// </summary>
    private const ushort HealthStatusHealthy = 0;

    /// <summary>
    /// The GPT partition type GUID for Windows Basic Data (<c>ebd0a0a2-b9e5-4433-87c0-68b6b72699c7</c>).
    /// </summary>
    private const string BasicDataGptType = "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}";

    /// <summary>
    /// Verifies that <see cref="NtfsFileSystem.Format"/> creates a volume that Windows
    /// considers healthy when the VHDX is mounted. Currently fails because DiscUtils
    /// NTFS metadata causes <c>OperationalStatus = 53263</c> and
    /// <c>HealthStatus = 1 (Warning)</c>.
    /// </summary>
    [TestMethod]
    public unsafe void NtfsFormat_ShouldCreateHealthyVolume_WhenMountedByWindows()
    {
        string vhdxPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vhdx");
        long diskCapacityInBytes = 1L * 1024 * 1024 * 1024; // 1 GB
        SafeFileHandle? vhdHandle = null;

        try
        {
            // Step 1: Create a VHDX with a GPT partition table and an NTFS-formatted
            // partition using DiscUtils — exactly the same way production code does it.
            CreateVhdxWithNtfsPartition(vhdxPath, diskCapacityInBytes);

            // Step 2: Mount the VHDX using the Win32 Virtual Disk API.
            vhdHandle = OpenAndAttachVirtualDisk(vhdxPath);

            // Step 3: Retrieve the OS-assigned physical disk number.
            uint diskNumber = GetPhysicalDiskNumber(vhdHandle);

            // Step 4: Wait for Windows to enumerate the volume device.
            // After AttachVirtualDisk, the PnP subsystem needs a moment to
            // create partition and volume device objects.
            Thread.Sleep(5000);

            // Step 5: Query the volume health via WMI and assert it should be healthy.
            AssertVolumeHealthy(diskNumber);

            // Step 6: Verify that MSFT_Partition.GetSupportedSize succeeds.
            // When the volume health is not OK, this fails with error code 42008.
            AssertGetSupportedSizeSucceeds(diskNumber);
        }
        finally
        {
            // Cleanup: detach the VHD and delete the file.
            if (vhdHandle is not null && !vhdHandle.IsInvalid)
            {
                _ = NativeMethods.DetachVirtualDisk(
                    vhdHandle,
                    DETACH_VIRTUAL_DISK_FLAG.DETACH_VIRTUAL_DISK_FLAG_NONE,
                    0);
                vhdHandle.Dispose();
            }

            if (File.Exists(vhdxPath))
            {
                File.Delete(vhdxPath);
            }
        }
    }

    /// <summary>
    /// Creates a VHDX file with a GPT partition table containing a single
    /// NTFS-formatted BasicData partition using DiscUtils.
    /// </summary>
    /// <param name="vhdxPath">The path where the VHDX file will be created.</param>
    /// <param name="diskCapacityInBytes">The total disk capacity in bytes.</param>
    private static void CreateVhdxWithNtfsPartition(string vhdxPath, long diskCapacityInBytes)
    {
        using var fileStream = new FileStream(vhdxPath, FileMode.CreateNew, FileAccess.ReadWrite);
        using var disk = Disk.InitializeDynamic(fileStream, Ownership.Dispose, diskCapacityInBytes);

        var gpt = GuidPartitionTable.Initialize(disk);

        long bytesPerSector = disk.Geometry!.Value.BytesPerSector;

        // Align the first partition to a 1 MB boundary (standard for GPT / UEFI).
        long alignmentInSectors = (1024 * 1024) / bytesPerSector;
        long startSector = Math.Max(gpt.FirstUsableSector, alignmentInSectors);
        long endSector = gpt.LastUsableSector;

        int partitionIndex = gpt.Create(
            startSector,
            endSector,
            GuidPartitionTypes.WindowsBasicData,
            0,
            "TestPartition");

        // Format the partition as NTFS using DiscUtils — this is the code under test.
        var volumeManager = new VolumeManager(disk);
        var logicalVolume = volumeManager.GetLogicalVolumes()[partitionIndex];

        using var ntfsFs = NtfsFileSystem.Format(logicalVolume, "TestLabel");
    }

    /// <summary>
    /// Opens and attaches a VHDX file using the Win32 Virtual Disk API.
    /// The disk is attached without a drive letter to avoid side-effects on the host.
    /// </summary>
    /// <param name="vhdxPath">The path to the VHDX file.</param>
    /// <returns>A <see cref="SafeFileHandle"/> to the attached virtual disk. The caller must
    /// detach and dispose the handle.</returns>
    private static unsafe SafeFileHandle OpenAndAttachVirtualDisk(string vhdxPath)
    {
        var storageType = new VIRTUAL_STORAGE_TYPE
        {
            DeviceId = NativeMethods.VIRTUAL_STORAGE_TYPE_DEVICE_UNKNOWN,
            VendorId = NativeMethods.VIRTUAL_STORAGE_TYPE_VENDOR_UNKNOWN,
        };

        var openParameters = new OPEN_VIRTUAL_DISK_PARAMETERS
        {
            Version = OPEN_VIRTUAL_DISK_VERSION.OPEN_VIRTUAL_DISK_VERSION_2,
        };

        var openResult = NativeMethods.OpenVirtualDisk(
            storageType,
            vhdxPath,
            VIRTUAL_DISK_ACCESS_MASK.VIRTUAL_DISK_ACCESS_NONE,
            OPEN_VIRTUAL_DISK_FLAG.OPEN_VIRTUAL_DISK_FLAG_NONE,
            openParameters,
            out var handle);

        Assert.AreEqual(
            WIN32_ERROR.ERROR_SUCCESS,
            openResult,
            $"OpenVirtualDisk failed for '{vhdxPath}': {openResult}");

        var attachParameters = new ATTACH_VIRTUAL_DISK_PARAMETERS
        {
            Version = ATTACH_VIRTUAL_DISK_VERSION.ATTACH_VIRTUAL_DISK_VERSION_1,
        };

        var attachResult = NativeMethods.AttachVirtualDisk(
            handle,
            new PSECURITY_DESCRIPTOR(null),
            ATTACH_VIRTUAL_DISK_FLAG.ATTACH_VIRTUAL_DISK_FLAG_NO_DRIVE_LETTER,
            0,
            attachParameters,
            null);

        if (attachResult != WIN32_ERROR.ERROR_SUCCESS)
        {
            handle.Dispose();
            Assert.Fail($"AttachVirtualDisk failed for '{vhdxPath}': {attachResult}");
        }

        return handle;
    }

    /// <summary>
    /// Gets the physical disk number for an attached virtual disk handle.
    /// </summary>
    /// <param name="vhdHandle">The virtual disk handle.</param>
    /// <returns>The OS-assigned physical disk number.</returns>
    private static uint GetPhysicalDiskNumber(SafeFileHandle vhdHandle)
    {
        uint pathSize = 0;
        _ = NativeMethods.GetVirtualDiskPhysicalPath(vhdHandle, ref pathSize, []);

        Span<char> pathBuffer = new char[pathSize / sizeof(char)];
        var result = NativeMethods.GetVirtualDiskPhysicalPath(vhdHandle, ref pathSize, pathBuffer);

        Assert.AreEqual(
            WIN32_ERROR.ERROR_SUCCESS,
            result,
            $"GetVirtualDiskPhysicalPath failed: {result}");

        // Path format: \\.\PhysicalDrive{N}
        string physicalPath = new(pathBuffer[..^1]);

        return uint.Parse(
            physicalPath[@"\\.\PhysicalDrive".Length..],
            CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Asserts that the NTFS volume on the given disk is healthy according to Windows
    /// Storage Management (WMI <c>MSFT_Volume</c>).
    /// </summary>
    /// <param name="diskNumber">The physical disk number.</param>
    private static void AssertVolumeHealthy(uint diskNumber)
    {
        using var volume = FindMsftVolume(diskNumber);

        var operationalStatus = (ushort[]?)volume["OperationalStatus"];
        var healthStatus = (ushort?)volume["HealthStatus"];
        string? fileSystemLabel = volume["FileSystemLabel"]?.ToString();

        Console.WriteLine($"Volume FileSystemLabel : {fileSystemLabel}");
        Console.WriteLine($"Volume OperationalStatus: {FormatArray(operationalStatus)}");
        Console.WriteLine($"Volume HealthStatus     : {healthStatus}");

        Assert.IsNotNull(
            operationalStatus,
            "MSFT_Volume.OperationalStatus is null.");

        CollectionAssert.Contains(
            operationalStatus,
            OperationalStatusOk,
            $"MSFT_Volume.OperationalStatus should contain {OperationalStatusOk} (OK), " +
            $"but was [{FormatArray(operationalStatus)}]. " +
            "DiscUtils NtfsFileSystem.Format creates NTFS metadata that Windows considers unhealthy.");

        Assert.AreEqual(
            HealthStatusHealthy,
            healthStatus,
            $"MSFT_Volume.HealthStatus should be {HealthStatusHealthy} (Healthy), but was {healthStatus} (Warning). " +
            "DiscUtils NtfsFileSystem.Format creates NTFS metadata that Windows considers unhealthy.");
    }

    /// <summary>
    /// Asserts that <c>MSFT_Partition.GetSupportedSize</c> succeeds for the BasicData
    /// partition on the given disk. When the volume health is not OK, this call fails
    /// with return value 42008.
    /// </summary>
    /// <param name="diskNumber">The physical disk number.</param>
    private static void AssertGetSupportedSizeSucceeds(uint diskNumber)
    {
        using var partition = FindMsftPartition(diskNumber);

        using var outParams = partition.InvokeMethod(
            "GetSupportedSize",
            partition.GetMethodParameters("GetSupportedSize"),
            options: null);

        uint returnValue = Convert.ToUInt32(outParams?["ReturnValue"], CultureInfo.InvariantCulture);

        Console.WriteLine($"MSFT_Partition.GetSupportedSize ReturnValue: {returnValue}");

        if (outParams is not null)
        {
            Console.WriteLine($"  SizeMin: {outParams["SizeMin"]}");
            Console.WriteLine($"  SizeMax: {outParams["SizeMax"]}");
        }

        Assert.AreEqual(
            0u,
            returnValue,
            $"MSFT_Partition.GetSupportedSize should return 0 (success), but returned {returnValue}. " +
            "Error code 42008 indicates the volume health is not OK, which is a consequence of " +
            "DiscUtils NtfsFileSystem.Format creating unhealthy NTFS metadata.");
    }

    /// <summary>
    /// Finds the <c>MSFT_Volume</c> WMI object on the specified disk by walking the
    /// partition's <c>AccessPaths</c> to locate the volume GUID path.
    /// </summary>
    /// <param name="diskNumber">The physical disk number.</param>
    /// <returns>The <see cref="System.Management.ManagementObject"/> representing the volume.</returns>
    private static System.Management.ManagementObject FindMsftVolume(uint diskNumber)
    {
        var scope = new System.Management.ManagementScope(@"\\localhost\ROOT\Microsoft\Windows\Storage");
        scope.Connect();

        // Find the BasicData partition on this disk.
        using var partition = FindMsftPartition(diskNumber);

        // Get the volume GUID path from the partition's AccessPaths.
        var accessPaths = (string[])partition["AccessPaths"];
        string volumePath = accessPaths
            .First(p => p.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase));

        // Query the MSFT_Volume by Path.
        string escapedPath = volumePath.Replace(@"\", @"\\", StringComparison.Ordinal);
        using var volSearcher = new System.Management.ManagementObjectSearcher(
            scope,
            new System.Management.ObjectQuery(
                $"SELECT * FROM MSFT_Volume WHERE Path = '{escapedPath}'"));

        using var results = volSearcher.Get();
        foreach (System.Management.ManagementObject obj in results.Cast<System.Management.ManagementObject>())
        {
            return obj;
        }

        throw new AssertFailedException(
            $"MSFT_Volume not found for volume path '{volumePath}' on disk {diskNumber}.");
    }

    /// <summary>
    /// Finds the <c>MSFT_Partition</c> WMI object for the BasicData partition on the
    /// specified disk.
    /// </summary>
    /// <param name="diskNumber">The physical disk number.</param>
    /// <returns>The <see cref="System.Management.ManagementObject"/> representing the partition.</returns>
    private static System.Management.ManagementObject FindMsftPartition(uint diskNumber)
    {
        var scope = new System.Management.ManagementScope(@"\\localhost\ROOT\Microsoft\Windows\Storage");
        scope.Connect();

        using var partSearcher = new System.Management.ManagementObjectSearcher(
            scope,
            new System.Management.ObjectQuery(
                $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {diskNumber} AND GptType = '{BasicDataGptType}'"));

        using var results = partSearcher.Get();
        foreach (System.Management.ManagementObject obj in results.Cast<System.Management.ManagementObject>())
        {
            return obj;
        }

        throw new AssertFailedException(
            $"MSFT_Partition (BasicData) not found on disk {diskNumber}.");
    }

    /// <summary>
    /// Formats an array of values as a comma-separated string for diagnostic output.
    /// </summary>
    private static string FormatArray(ushort[]? values)
    {
        return values is null ? "null" : string.Join(", ", values);
    }
}
