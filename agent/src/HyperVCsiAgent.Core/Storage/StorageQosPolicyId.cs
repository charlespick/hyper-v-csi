using System.Security.Cryptography;
using System.Text;

namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// The Storage QoS policy ID a volume's policy has, derived from the volume ID
/// alone: <c>UUIDv5(</c><see cref="Namespace"/><c>, volumeId)</c>.
/// </summary>
/// <remarks>
/// Derived rather than recorded, which is the whole of how this driver keeps no
/// QoS state: create, attach, modify and delete can each find a volume's policy
/// without a lookup table, and whether that policy exists is the record of
/// whether the volume has QoS. A volume that predates QoS support, a static PV
/// and a volume in a <c>qos: "none"</c> class all simply have no policy at this
/// ID.
/// </remarks>
public static class StorageQosPolicyId
{
    /// <summary>
    /// This driver's UUIDv5 namespace. Fixed for good: changing it would orphan
    /// every policy already created, and every volume's QoS with it.
    /// </summary>
    public static readonly Guid Namespace = new("8a5cd3dd-7939-4c82-b68e-98c5f479376d");

    public static Guid For(string volumeId) => Version5(Namespace, volumeId);

    /// <summary>An RFC 9562 version 5 (SHA-1, name-based) UUID.</summary>
    internal static Guid Version5(Guid namespaceId, string nameText)
    {
        // RFC 9562 section 5.5: SHA-1 over the namespace's bytes in network
        // order, then the name, with the version and variant bits overwritten.
        var name = Encoding.UTF8.GetBytes(nameText);
        var input = new byte[16 + name.Length];
        namespaceId.TryWriteBytes(input, bigEndian: true, out _);
        name.CopyTo(input, 16);

        Span<byte> hash = stackalloc byte[SHA1.HashSizeInBytes];
        SHA1.HashData(input, hash);

        var uuid = hash[..16];
        uuid[6] = (byte)((uuid[6] & 0x0F) | 0x50);
        uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80);
        return new Guid(uuid, bigEndian: true);
    }

    /// <summary>
    /// Whether a disk of <paramref name="volumeId"/>'s that names
    /// <paramref name="actual"/> is in the state this driver wants:
    /// <paramref name="expected"/> exactly, or - when the volume should have no
    /// QoS (<see cref="Guid.Empty"/>) - anything but this volume's own derived
    /// ID.
    /// </summary>
    /// <remarks>
    /// The second half is what keeps a volume that does not use QoS out of
    /// this driver's hands: a policy someone set on its disk by hand is not
    /// this driver's to clear. Its own derived ID with no policy behind it is,
    /// since nothing but this driver puts that one there, and a disk left
    /// naming a deleted policy is what Hyper-V reports as <c>UnknownPolicyId</c>.
    /// </remarks>
    public static bool Satisfies(Guid actual, Guid expected, string volumeId) =>
        actual == expected || (expected == Guid.Empty && actual != For(volumeId));
}
