using AppLedger;
using Xunit;

namespace AppLedger.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public void VersionInfo_IncludesRuntimeAndExecutableIdentity()
    {
        var info = Diagnostics.GetVersionInfo();

        Assert.Equal("AppLedger", info.Product);
        Assert.False(string.IsNullOrWhiteSpace(info.Version));
        Assert.False(string.IsNullOrWhiteSpace(info.InformationalVersion));
        Assert.False(string.IsNullOrWhiteSpace(info.Runtime));
        Assert.False(string.IsNullOrWhiteSpace(info.BaseDirectory));
    }

    [Fact]
    public void DoctorReport_IncludesCoreChecks()
    {
        var report = Diagnostics.BuildDoctorReport(Directory.GetCurrentDirectory());

        Assert.Contains(report.Checks, check => check.Name == "Current binary");
        Assert.Contains(report.Checks, check => check.Name == "PATH command");
        Assert.Contains(report.Checks, check => check.Name == "Elevation");
        Assert.Contains(report.Checks, check => check.Name == "Native ETW files");
    }

    [Fact]
    public void Install_CopiesReleaseFolderWithoutPathMutation()
    {
        var root = Path.Combine(Path.GetTempPath(), "appledger-tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(Path.Combine(source, "amd64"));
        File.WriteAllText(Path.Combine(source, "appledger.exe"), "fake");
        File.WriteAllText(Path.Combine(source, "README.md"), "readme");
        File.WriteAllText(Path.Combine(source, "amd64", "KernelTraceControl.dll"), "native");

        try
        {
            var result = Diagnostics.Install(source, target, addPath: false);

            Assert.Equal(3, result.CopiedFiles);
            Assert.False(result.PathUpdated);
            Assert.False(result.AlreadyInstalled);
            Assert.True(File.Exists(Path.Combine(target, "appledger.exe")));
            Assert.True(File.Exists(Path.Combine(target, "amd64", "KernelTraceControl.dll")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
