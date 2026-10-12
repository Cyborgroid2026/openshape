using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace OpenShapeInstaller;

/// <summary>Calls into the Rust core (libosc_installer.so). Falls back to managed code when it cannot be loaded.</summary>
public static partial class NativeCore
{
    private const string Lib = "osc_installer";

    [LibraryImport(Lib, EntryPoint = "osi_sha256_file", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Sha256Native(string path, byte[] out65);

    [LibraryImport(Lib, EntryPoint = "osi_compare_versions", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int CompareNative(string a, string b);

    private static bool? _ok;

    /// <summary>True when the Rust library is available.</summary>
    public static bool Available
    {
        get
        {
            if (_ok is { } ok) return ok;
            try
            {
                CompareNative("1", "1");
                _ok = true;
            }
            catch (Exception)
            {
                _ok = false;
            }
            return _ok.Value;
        }
    }

    public static string Sha256File(string path)
    {
        if (Available)
        {
            var buf = new byte[65];
            if (Sha256Native(path, buf) == 0) return Encoding.ASCII.GetString(buf, 0, 64);
        }
        using var f = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }

    /// <summary>dpkg --compare-versions result (-1/0/1), or null without the Rust core.</summary>
    public static int? CompareVersions(string a, string b) => Available ? CompareNative(a, b) : null;
}
