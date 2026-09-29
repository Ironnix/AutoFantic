using AutoFantic.Core.Updates;

namespace AutoFantic.Core.Tests;

/// <summary>With the .NET runtime's own files: Microsoft signs them, on every PC and on GitHub's build machines.</summary>
public sealed class SignatureTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "autofantic-sign-" + Guid.NewGuid().ToString("N"));
    private static readonly string Signed = typeof(object).Assembly.Location;                               // System.Private.CoreLib.dll
    private static readonly string AlsoSigned = Path.Combine(Path.GetDirectoryName(Signed)!, "System.Runtime.dll");

    public SignatureTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Copy(string from, string name)
    {
        string to = Path.Combine(_folder, name);
        File.Copy(from, to);
        return to;
    }

    [Fact]
    public void A_signed_file_names_its_publisher_and_an_unsigned_or_changed_one_doesnt()
    {
        Assert.Contains("Microsoft", Signature.Of(Signed));

        string unsigned = Path.Combine(_folder, "unsigned.exe");
        File.WriteAllBytes(unsigned, new byte[4096]);
        Assert.Null(Signature.Of(unsigned));
        Assert.Null(Signature.Of(Path.Combine(_folder, "missing.exe")));

        // one byte changed in the middle: the signature no longer fits
        string changed = Copy(Signed, "changed.dll");
        var bytes = File.ReadAllBytes(changed);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(changed, bytes);
        Assert.Null(Signature.Of(changed));
    }

    [Fact]
    public void Once_signed_an_update_must_come_from_the_same_publisher()
    {
        string current = Copy(Signed, "current.dll");
        string unsigned = Path.Combine(_folder, "next.exe");
        File.WriteAllBytes(unsigned, new byte[4096]);

        Signature.CheckSamePublisher(current, AlsoSigned); // same publisher: fine
        Assert.Throws<InvalidDataException>(() => Signature.CheckSamePublisher(current, unsigned));

        // an unsigned AutoFantic (a build from source, the releases before signing) takes any update
        Signature.CheckSamePublisher(unsigned, current);
        Signature.CheckSamePublisher(unsigned, unsigned);
    }
}
